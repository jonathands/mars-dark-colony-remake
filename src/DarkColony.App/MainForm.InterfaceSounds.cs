using DarkColony.App.Audio;
using DarkColony.Engine.Data;

namespace DarkColony.App;

/// <summary>
/// The menus' sounds. Each plays on its own waveOut stream, which Windows
/// mixes, so a click does not cut off the sound before it as one
/// <c>SoundPlayer</c> would.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>The pointer moving onto a push or check button: sound 0x88, HLIGHT.WAV (<c>0x42426F</c>).</summary>
    private const int ButtonHighlightSound = 0x88;

    private readonly List<WaveOutStream> _interfaceSounds = [];
    private readonly Dictionary<string, PcmWave?> _interfaceWaves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A sound2.dat sound at its catalog volume (the third value, hundredths of a decibel).</summary>
    private WaveOutStream? PlayInterfaceSound(int soundId)
    {
        if (_installation is null) return null;
        try
        {
            _soundCatalog ??= SoundCatalog.Load(_installation.DataFile("sound"));
            if (!_soundCatalog.Sounds.TryGetValue(soundId, out var sound)) return null;
            var volume = sound.Parameters.Count > 2 ? sound.Parameters[2] : 0;
            return PlayInterfaceWave(Path.Combine(_installation.RootPath, sound.RelativePath.Replace('/', Path.DirectorySeparatorChar)), volume);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException)
        {
            _status = $"Sound error: {error.Message}";
            return null;
        }
    }

    /// <summary>
    /// A WAV file at <paramref name="volume"/> hundredths of a decibel (0 is
    /// full, -1000 is -10 dB), as DirectSound's SetVolume takes it.
    /// </summary>
    private WaveOutStream? PlayInterfaceWave(string path, int volume)
    {
        _interfaceSounds.RemoveAll(stream =>
        {
            if (!stream.Finished) return false;
            stream.Dispose();
            return true;
        });
        if (!_interfaceWaves.TryGetValue(path, out var wave))
        {
            wave = File.Exists(path) ? PcmWave.Read(path) : null;
            _interfaceWaves[path] = wave;
        }
        if (wave is null) return null;
        var stream = WaveOutStream.ForPcm(wave.Channels, wave.SamplesPerSecond, wave.BitsPerSample, wave.Samples);
        if (volume < 0) stream.SetVolume((int)(0xFFFF * Math.Pow(10, volume / 2000.0)));
        _interfaceSounds.Add(stream);
        return stream;
    }

    private void StopInterfaceSounds()
    {
        foreach (var stream in _interfaceSounds) stream.Dispose();
        _interfaceSounds.Clear();
    }

    /// <summary>A PCM WAV file's format and samples.</summary>
    private sealed record PcmWave(int Channels, int SamplesPerSecond, int BitsPerSample, byte[] Samples)
    {
        public static PcmWave Read(string path)
        {
            var data = File.ReadAllBytes(path);
            int channels = 0, rate = 0, bits = 0;
            for (var offset = 12; offset + 8 <= data.Length;)
            {
                var id = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                var size = BitConverter.ToInt32(data, offset + 4);
                if (id == "fmt " && size >= 16)
                {
                    channels = BitConverter.ToInt16(data, offset + 10);
                    rate = BitConverter.ToInt32(data, offset + 12);
                    bits = BitConverter.ToInt16(data, offset + 22);
                }
                if (id == "data" && channels > 0)
                    return new PcmWave(channels, rate, bits, data.AsSpan(offset + 8, Math.Min(size, data.Length - offset - 8)).ToArray());
                offset += 8 + size + (size & 1);
            }
            throw new InvalidDataException($"{path} has no PCM data chunk.");
        }
    }
}
