using System.Runtime.InteropServices;
using DarkColony.Engine.Audio;

namespace DarkColony.App.Audio;

/// <summary>
/// Streams PCM through winmm waveOut, which Windows mixes with the effect
/// sounds. It carries:
/// <list type="bullet">
/// <item><description>the CD soundtrack (44.1 kHz, 16-bit stereo, read from the
/// image's tracks), standing in for MCI <c>cdaudio</c>;</description></item>
/// <item><description>the videos' 8-bit mono sound, which the original plays
/// through DirectSound.</description></item>
/// </list>
/// </summary>
internal sealed partial class WaveOutStream : IDisposable
{
    private const int BufferCount = 4;
    private const int WaveMapper = -1;
    private const int CallbackEvent = 0x50000;
    private const int HeaderDone = 1;

    private readonly short _channels;
    private readonly int _samplesPerSecond;
    private readonly short _bitsPerSample;
    private readonly Func<Stream> _openSource;
    private readonly AutoResetEvent _bufferDone = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile bool _finished;
    private long _playedBytes;
    private IntPtr _device;
    private int _volume = -1;

    private WaveOutStream(int channels, int samplesPerSecond, int bitsPerSample, Func<Stream> openSource, string name)
    {
        _channels = (short)channels;
        _samplesPerSecond = samplesPerSecond;
        _bitsPerSample = (short)bitsPerSample;
        _openSource = openSource;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Red Book audio from the image's tracks, played one after another as a disc does.</summary>
    public static WaveOutStream ForCdTracks(IReadOnlyList<CueTrack> tracks) =>
        new(2, 44100, 16, () => new TrackStream(tracks), "CD audio");

    public static WaveOutStream ForPcm(int channels, int samplesPerSecond, int bitsPerSample, byte[] samples) =>
        new(channels, samplesPerSecond, bitsPerSample, () => new MemoryStream(samples, writable: false), "Video audio");

    private int BlockAlign => _channels * _bitsPerSample / 8;
    private int BytesPerSecond => _samplesPerSecond * BlockAlign;

    /// <summary>The source ran out and the device played it all, or the device failed.</summary>
    public bool Finished => _finished;

    /// <summary>
    /// The stream's own volume, one 16-bit word per channel (0xFFFF full),
    /// as <c>auxSetVolume</c> takes it.
    /// </summary>
    public void SetVolume(int word)
    {
        _volume = Math.Clamp(word, 0, 0xFFFF);
        var device = _device;
        if (device != IntPtr.Zero) waveOutSetVolume(device, (uint)(_volume | _volume << 16));
    }

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
            FormatTag = 1, Channels = _channels, SamplesPerSecond = _samplesPerSecond,
            AverageBytesPerSecond = BytesPerSecond, BlockAlign = (short)BlockAlign, BitsPerSample = _bitsPerSample,
        };
        if (BlockAlign <= 0 || waveOutOpen(out var device, WaveMapper, ref format, _bufferDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent) != 0)
        {
            _finished = true;
            return;
        }

        _device = device;
        if (_volume >= 0) waveOutSetVolume(device, (uint)(_volume | _volume << 16));
        var bufferBytes = Math.Max(BlockAlign, BytesPerSecond / 4 / BlockAlign * BlockAlign);
        var headerSize = Marshal.SizeOf<WaveHeader>();
        var flagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
        var lengthOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.BufferLength));
        var headers = new IntPtr[BufferCount];
        var buffers = new IntPtr[BufferCount];
        var queued = new bool[BufferCount];
        var chunk = new byte[bufferBytes];
        try
        {
            for (var index = 0; index < BufferCount; index++)
            {
                buffers[index] = Marshal.AllocHGlobal(bufferBytes);
                headers[index] = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(new WaveHeader { Data = buffers[index], BufferLength = bufferBytes }, headers[index], false);
                waveOutPrepareHeader(device, headers[index], headerSize);
            }

            using var source = _openSource();
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
                    if (queued[index]) Interlocked.Add(ref _playedBytes, Marshal.ReadInt32(headers[index], lengthOffset));
                    queued[index] = false;
                    if (exhausted) continue;
                    var read = source.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false);
                    read -= read % BlockAlign;
                    if (read == 0)
                    {
                        exhausted = true;
                        continue;
                    }
                    Marshal.Copy(chunk, 0, buffers[index], read);
                    Marshal.WriteInt32(headers[index], lengthOffset, read);
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
            // An unreadable source ends the stream, as a failed MCI request would.
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
            _device = IntPtr.Zero;
            waveOutClose(device);
            _finished = true;
        }
    }

    /// <summary>The tracks' byte ranges one after another, as a disc plays on.</summary>
    private sealed class TrackStream(IReadOnlyList<CueTrack> tracks) : Stream
    {
        private int _track = -1;
        private FileStream? _file;
        private long _remaining;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (_remaining == 0 && !Advance()) return 0;
                var read = _file!.Read(buffer, offset, (int)Math.Min(count, _remaining));
                if (read > 0)
                {
                    _remaining -= read;
                    return read;
                }
                _remaining = 0;
            }
        }

        private bool Advance()
        {
            if (++_track >= tracks.Count) return false;
            var track = tracks[_track];
            if (_file is null || !string.Equals(_file.Name, Path.GetFullPath(track.FilePath), StringComparison.OrdinalIgnoreCase))
            {
                _file?.Dispose();
                _file = new FileStream(track.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            }
            _file.Position = track.Offset;
            _remaining = track.Length;
            return true;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file?.Dispose();
            base.Dispose(disposing);
        }
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

    [LibraryImport("winmm.dll")]
    private static partial int waveOutSetVolume(IntPtr device, uint volume);
}
