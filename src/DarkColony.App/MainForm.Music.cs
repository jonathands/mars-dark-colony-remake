using DarkColony.App.Audio;
using DarkColony.App.Diagnostics;
using DarkColony.Engine.Audio;

namespace DarkColony.App;

/// <summary>CD music from the user's disc image, on the original's schedule (<see cref="CdMusic"/>).</summary>
public sealed partial class MainForm
{
    private readonly CdMusicPoll _cdMusicPoll = new();
    private IReadOnlyList<CueTrack>? _cdMusicPass;
    private CdAudioStream? _cdMusic;

    /// <summary>The CUE sheet of the CD image, or null for no music.</summary>
    public string? CdImagePath { get; init; }

    private IReadOnlyList<CueTrack> CdMusicPass()
    {
        if (_cdMusicPass is not null) return _cdMusicPass;
        _cdMusicPass = [];
        if (CdImagePath is null) return _cdMusicPass;
        try
        {
            _cdMusicPass = CdMusic.Pass(CueSheet.Load(CdImagePath));
            RuntimeLog.Info($"CD music: {CdImagePath}, tracks {string.Join(' ', _cdMusicPass.Select(track => track.Number))}.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            _status = $"CD image error: {error.Message}";
        }
        return _cdMusicPass;
    }

    /// <summary>Gameplay setup (0x41EF4F): play from track 2 to the end of the disc.</summary>
    private void StartCdMusic()
    {
        var pass = CdMusicPass();
        if (pass.Count == 0) return;
        _cdMusic?.Dispose();
        _cdMusic = new CdAudioStream(pass);
        RuntimeLog.Info($"CD music: track {pass[0].Number} playing.");
    }

    /// <summary>The gameplay loop's drive poll: a stopped disc starts over at track 2.</summary>
    private void PollCdMusic()
    {
        if (!_cdMusicPoll.Due(Environment.TickCount64) || _cdMusic is { Finished: false }) return;
        if (_cdMusic is not null) RuntimeLog.Info("CD music: the disc stopped; starting over.");
        StartCdMusic();
    }

    private void StopCdMusic()
    {
        if (_cdMusic is not null) RuntimeLog.Info($"CD music: stopped after {_cdMusic.PlayedSeconds:0.0} s.");
        _cdMusic?.Dispose();
        _cdMusic = null;
    }
}
