using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DarkColony.Engine.Network;

/// <summary>Carries lockstep messages to every other peer.</summary>
public interface ILockstepTransport : IDisposable
{
    /// <summary>Sends to every other peer.</summary>
    void Send(LockstepMessage message);

    /// <summary>The next message another peer sent, if one has arrived.</summary>
    bool TryReceive(out LockstepMessage message);
}

/// <summary>Peers in one process, for checks: each endpoint's sends reach every other endpoint.</summary>
public sealed class LoopbackLockstepNetwork
{
    private readonly List<Endpoint> _endpoints = [];

    public ILockstepTransport Connect()
    {
        var endpoint = new Endpoint(this);
        _endpoints.Add(endpoint);
        return endpoint;
    }

    private sealed class Endpoint(LoopbackLockstepNetwork network) : ILockstepTransport
    {
        private readonly ConcurrentQueue<LockstepMessage> _inbox = new();

        public void Send(LockstepMessage message)
        {
            // Through the codec, so loopback runs exercise the wire format too.
            var line = LockstepCodec.Encode(message);
            foreach (var other in network._endpoints.Where(other => other != this)) other._inbox.Enqueue(LockstepCodec.Decode(line));
        }

        public bool TryReceive(out LockstepMessage message) => _inbox.TryDequeue(out message!);

        public void Dispose() { }
    }
}

/// <summary>
/// Lockstep over TCP as JSON lines. The host accepts the other players and
/// relays each one's messages to the rest (a star); a client talks to the host only.
/// </summary>
public sealed class TcpLockstepTransport : ILockstepTransport
{
    private readonly List<Connection> _connections = [];
    private readonly ConcurrentQueue<LockstepMessage> _inbox = new();
    private readonly ConcurrentQueue<int> _lost = new();
    private readonly bool _relay;
    private TcpListener? _listener;

    private TcpLockstepTransport(bool relay) => _relay = relay;

    /// <summary>
    /// Hosts on <paramref name="listener"/> and keeps accepting players in the
    /// background (a lobby) until <see cref="StopAccepting"/>.
    /// </summary>
    public static TcpLockstepTransport Listen(TcpListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var transport = new TcpLockstepTransport(relay: true) { _listener = listener };
        new Thread(() =>
        {
            try
            {
                while (true) transport.Add(listener.AcceptTcpClient());
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException) { }
        }) { IsBackground = true, Name = "Lockstep accept" }.Start();
        return transport;
    }

    /// <summary>Stops taking new players (the game starts).</summary>
    public void StopAccepting()
    {
        _listener?.Stop();
        _listener = null;
    }

    /// <summary>Connected peers (the host's clients, or the client's host).</summary>
    public int PeerCount
    {
        get
        {
            lock (_connections) return _connections.Count(connection => !connection.Closed);
        }
    }

    /// <summary>
    /// A connection that closed, by the player it carried. The player is the
    /// first one its messages named, or -1 if it named none (a client's link
    /// to its host).
    /// </summary>
    public bool TryTakeLostPlayer(out int player) => _lost.TryDequeue(out player);

    /// <summary>Waits for <paramref name="clients"/> players to connect on <paramref name="listener"/>.</summary>
    public static TcpLockstepTransport Host(TcpListener listener, int clients, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var transport = new TcpLockstepTransport(relay: true);
        var deadline = DateTime.UtcNow + timeout;
        while (transport.ConnectionCount < clients)
        {
            var accept = listener.AcceptTcpClientAsync();
            if (!accept.Wait(deadline - DateTime.UtcNow)) throw new TimeoutException("Lockstep players did not connect in time.");
            transport.Add(accept.Result);
        }
        return transport;
    }

    public static TcpLockstepTransport Join(IPEndPoint host, TimeSpan timeout)
    {
        var client = new TcpClient { NoDelay = true };
        if (!client.ConnectAsync(host).Wait(timeout)) throw new TimeoutException($"No lockstep host at {host}.");
        var transport = new TcpLockstepTransport(relay: false);
        transport.Add(client);
        return transport;
    }

    private int ConnectionCount
    {
        get
        {
            lock (_connections) return _connections.Count;
        }
    }

    public void Send(LockstepMessage message) => Broadcast(LockstepCodec.Encode(message), except: null);

    public bool TryReceive(out LockstepMessage message) => _inbox.TryDequeue(out message!);

    public void Dispose()
    {
        StopAccepting();
        lock (_connections)
            foreach (var connection in _connections) connection.Dispose();
    }

    private void Add(TcpClient client)
    {
        client.NoDelay = true;
        var connection = new Connection(client);
        lock (_connections) _connections.Add(connection);
        connection.Start(line =>
        {
            var message = LockstepCodec.Decode(line);
            if (connection.Player < 0 && message.Player >= 0) connection.Player = message.Player;
            _inbox.Enqueue(message);
            if (_relay) Broadcast(line, except: connection);
        }, () => _lost.Enqueue(connection.Player));
    }

    private void Broadcast(string line, Connection? except)
    {
        Connection[] connections;
        lock (_connections) connections = [.. _connections];
        foreach (var connection in connections)
            if (connection != except) connection.Write(line);
    }

    private sealed class Connection(TcpClient client) : IDisposable
    {
        private readonly StreamWriter _writer = new(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        private readonly object _writeLock = new();

        /// <summary>The player this connection carries, once a message names one.</summary>
        public int Player { get; set; } = -1;

        public bool Closed { get; private set; }

        public void Start(Action<string> received, Action closed)
        {
            var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
            new Thread(() =>
            {
                try
                {
                    while (reader.ReadLine() is { } line) received(line);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException
                    or System.Text.Json.JsonException) { }
                Closed = true;
                closed();
            }) { IsBackground = true, Name = "Lockstep receive" }.Start();
        }

        public void Write(string line)
        {
            lock (_writeLock)
            {
                try
                {
                    _writer.WriteLine(line);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            }
        }

        public void Dispose() => client.Dispose();
    }
}
