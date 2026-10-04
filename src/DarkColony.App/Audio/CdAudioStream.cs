using System.Runtime.InteropServices;
using DarkColony.Engine.Audio;

namespace DarkColony.App.Audio;

/// <summary>
/// Streams Red Book audio (44.1 kHz, 16-bit stereo, little-endian PCM) from
/// raw CD image tracks through winmm waveOut, which Windows mixes with the
/// effect sounds. It stands in for the MCI <c>cdaudio</c> device the original
/// plays the disc with.
/// </summary>
internal sealed partial class CdAudioStream : IDisposable
{
    private const int SampleRate = 44100;
    private const int BytesPerSecond = SampleRate * 4;
    private const int BufferBytes = BytesPerSecond / 4;
    private const int BufferCount = 4;
    private const int WaveMapper = -1;
    private const int CallbackEvent = 0x50000;
    private const int HeaderDone = 1;

    private readonly IReadOnlyList<CueTrack> _tracks;
    private readonly AutoResetEvent _bufferDone = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile bool _finished;
    private long _playedBytes;

    public CdAudioStream(IReadOnlyList<CueTrack> tracks)
    {
        _tracks = tracks;
        _thread = new Thread(Run) { IsBackground = true, Name = "CD audio" };
        _thread.Start();
    }

    /// <summary>The disc stopped: the last track ended, or the device failed.</summary>
    public bool Finished => _finished;

    /// <summary>Audio the device has finished playing, in seconds.</summary>
    public double PlayedSeconds => Interlocked.Read(ref _playedBytes) / (double)BytesPerSecond;

    public void Dispose()
    {
        _stopping = true;
        _bufferDone.Set();
        _thread.Join();
        _bufferDone.Dispose();
    }

    private void Run()
    {
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = 2, SamplesPerSecond = SampleRate,
            AverageBytesPerSecond = BytesPerSecond, BlockAlign = 4, BitsPerSample = 16,
        };
        if (waveOutOpen(out var device, WaveMapper, ref format, _bufferDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent) != 0)
        {
            _finished = true;
            return;
        }

        var headerSize = Marshal.SizeOf<WaveHeader>();
        var flagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
        var headers = new IntPtr[BufferCount];
        var buffers = new IntPtr[BufferCount];
        var queued = new bool[BufferCount];
        var chunk = new byte[BufferBytes];
        try
        {
            for (var index = 0; index < BufferCount; index++)
            {
                buffers[index] = Marshal.AllocHGlobal(BufferBytes);
                headers[index] = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(new WaveHeader { Data = buffers[index], BufferLength = BufferBytes }, headers[index], false);
                waveOutPrepareHeader(device, headers[index], headerSize);
            }

            using var source = new TrackReader(_tracks);
            var exhausted = false;
            while (!_stopping)
            {
                var pending = false;
                for (var index = 0; index < BufferCount; index++)
                {
                    if (queued[index] && (Marshal.ReadInt32(headers[index], flagsOffset) & HeaderDone) == 0)
                    {
                        pending = true;
                        continue;
                    }
                    if (queued[index]) Interlocked.Add(ref _playedBytes, Marshal.ReadInt32(headers[index], (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.BufferLength))));
                    queued[index] = false;
                    if (exhausted) continue;
                    var read = source.Read(chunk);
                    if (read == 0)
                    {
                        exhausted = true;
                        continue;
                    }
                    Marshal.Copy(chunk, 0, buffers[index], read);
                    Marshal.WriteInt32(headers[index], (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.BufferLength)), read);
                    Marshal.WriteInt32(headers[index], flagsOffset, Marshal.ReadInt32(headers[index], flagsOffset) & ~HeaderDone);
                    if (waveOutWrite(device, headers[index], headerSize) != 0)
                    {
                        exhausted = true;
                        continue;
                    }
                    queued[index] = true;
                    pending = true;
                }
                if (exhausted && !pending) break;
                _bufferDone.WaitOne(100);
            }
        }
        catch (IOException)
        {
            // An unreadable image ends the disc, as a failed MCI request would.
        }
        finally
        {
            waveOutReset(device);
            for (var index = 0; index < BufferCount; index++)
            {
                if (headers[index] == IntPtr.Zero) continue;
                waveOutUnprepareHeader(device, headers[index], headerSize);
                Marshal.FreeHGlobal(headers[index]);
                Marshal.FreeHGlobal(buffers[index]);
            }
            waveOutClose(device);
            _finished = true;
        }
    }

    /// <summary>Reads the tracks' byte ranges one after another, as a disc plays on.</summary>
    private sealed class TrackReader(IReadOnlyList<CueTrack> tracks) : IDisposable
    {
        private int _track = -1;
        private FileStream? _file;
        private long _remaining;

        public int Read(byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                if (_remaining == 0 && !Advance()) break;
                var read = _file!.Read(buffer, total, (int)Math.Min(buffer.Length - total, _remaining));
                if (read == 0)
                {
                    _remaining = 0;
                    continue;
                }
                total += read;
                _remaining -= read;
            }
            return total - total % 4;
        }

        private bool Advance()
        {
            if (++_track >= tracks.Count) return false;
            var track = tracks[_track];
            if (_file is null || !string.Equals(_file.Name, Path.GetFullPath(track.FilePath), StringComparison.OrdinalIgnoreCase))
            {
                _file?.Dispose();
                _file = new FileStream(track.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes);
            }
            _file.Position = track.Offset;
            _remaining = track.Length;
            return true;
        }

        public void Dispose() => _file?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [LibraryImport("winmm.dll")]
    private static partial int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutWrite(IntPtr device, IntPtr header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutReset(IntPtr device);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutClose(IntPtr device);
}
