using DarkColony.App.Audio;
using DarkColony.App.Diagnostics;
using DarkColony.App.Rendering;
using DarkColony.Engine.Audio;
using DarkColony.Engine.Data;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Video;

namespace DarkColony.App;

/// <summary>
/// The original's full-motion video (<c>avi.c</c>). <c>0x401028</c> opens
/// <c>avi/&lt;name&gt;</c>; a file that does not exist plays nothing. A display
/// thread then shows frame k at <c>k * 1000 / 15</c> ms (<c>0x40874E</c>) while
/// the sound plays alongside. In the preferred mode (<c>0x478CF0 = 1</c>,
/// <c>0x407240</c>) each picture row goes to every other screen row from
/// y = 61 (<c>0x407204</c>: 240 - (height - 1)), each pixel doubled. Only
/// the top height - 1 rows are drawn, on a black screen. A key press
/// (<c>WM_KEYDOWN</c>, <c>0x408C40</c>) ends playback early.
/// </summary>
public sealed partial class MainForm
{
    private const int VideoFramesPerSecond = 15;
    private const int VideoScreenWidth = 640;

    private VideoPlayback? _video;
    private CdImageFiles? _cdFiles;
    private bool _cdFilesOpened;

    /// <summary>Skip every video (unattended runs).</summary>
    public bool NoVideo { get; init; }

    private sealed class VideoPlayback(AviFile avi, Action then)
    {
        public AviFile Avi { get; } = avi;
        public Action Then { get; } = then;
        public CinepakDecoder Decoder { get; } = new(avi.Width, avi.Height);
        public int Decoded { get; set; } = -1;
        public long StartedAt { get; set; }
        public WaveOutStream? Audio { get; set; }
        public byte[] Picture { get; } = new byte[VideoScreenWidth * Math.Max(1, 2 * (avi.Height - 1) - 1) * 4];
        public int PictureRows => Math.Max(1, 2 * (Avi.Height - 1) - 1);
        public int Top => 240 - (Avi.Height - 1);
    }

    /// <summary>Plays <paramref name="path"/> (like <c>avi/intro.avi</c>), then runs <paramref name="then"/>.</summary>
    private void PlayVideo(string path, Action then)
    {
        AviFile? avi = null;
        if (!NoVideo && _installation is not null)
        {
            try
            {
                using var stream = OpenVideo(path);
                if (stream is not null) avi = AviFile.Read(stream);
                else RuntimeLog.Info($"Video {path}: not in the installation or the CD image ({CdImagePath ?? "none"}).");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                RuntimeLog.Info($"Video {path} failed: {error.Message}");
            }
        }
        if (avi is null || !avi.VideoHandler.Equals("cvid", StringComparison.OrdinalIgnoreCase) || avi.VideoFrames.Count == 0 || avi.Height < 2)
        {
            then();
            return;
        }

        RuntimeLog.Info($"Video {path}: {avi.VideoFrames.Count} frames, {avi.Width}x{avi.Height}.");
        ClearTransientInputState();
        _video = new VideoPlayback(avi, then) { StartedAt = Environment.TickCount64 };
        if (avi.AudioFormat is { } format && avi.Audio.Length > 0)
            _video.Audio = WaveOutStream.ForPcm(format.Channels, format.SamplesPerSecond, format.BitsPerSample, avi.Audio);
        AdvanceVideo();
    }

    /// <summary>
    /// The end of a mission, before the debrief. A campaign mission
    /// (<c>0x403A48</c>) plays its scene list record's first video after a
    /// victory and the second after a defeat. A won War (<c>0x404720</c>)
    /// plays hvad1 (Human) or avhd1 (Gray), which the disc does not carry.
    /// </summary>
    private void PlayMissionEndVideo(MissionOutcome outcome, Action then)
    {
        if (_installation is null)
        {
            then();
            return;
        }
        if (_selectedScenario is not null)
        {
            if (outcome.Victory) PlayVideo(_grayRace ? "avi/avhd1.avi" : "avi/hvad1.avi", then);
            else then();
            return;
        }

        SceneMission? mission = null;
        try
        {
            var scenes = SceneList.Load(_installation.DataFile("gamestat", SceneList.FileName(_grayRace, _training)));
            var scenario = GameplayScenario();
            mission = scenes.Find($"{scenario.Directory}/{scenario.Name}.scn");
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            RuntimeLog.Info($"Scene list unavailable: {error.Message}");
        }
        if (mission is null) then();
        else PlayVideo(outcome.Victory ? mission.VictoryVideo : mission.DefeatVideo, then);
    }

    /// <summary>The installation's file, else the disc's <c>dc</c> folder in the CD image (as 0x405DE0 falls back to the CD).</summary>
    private Stream? OpenVideo(string path)
    {
        if (_installation is null) return null;
        var local = Path.Combine(_installation.RootPath, path.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(local)) return File.OpenRead(local);
        if (!_cdFilesOpened)
        {
            _cdFilesOpened = true;
            if (CdImagePath is not null)
            {
                try
                {
                    _cdFiles = CdImageFiles.Open(CueSheet.Load(CdImagePath));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
                {
                    RuntimeLog.Info($"CD image {CdImagePath} has no readable file system: {error.Message}");
                }
            }
        }
        return _cdFiles?.OpenFile($"dc/{path}");
    }

    /// <summary>Decodes up to the frame due now and ends playback once the pictures and the sound are done.</summary>
    private void AdvanceVideo()
    {
        if (_video is not { } video) return;
        var elapsed = Environment.TickCount64 - video.StartedAt;
        var frames = video.Avi.VideoFrames.Count;
        var due = (int)Math.Min(elapsed * VideoFramesPerSecond / 1000, frames - 1);
        var changed = false;
        while (video.Decoded < due)
        {
            video.Decoder.Decode(video.Avi.VideoFrames[++video.Decoded]);
            changed = true;
        }
        if (changed) ComposeVideoPicture(video);
        if (elapsed >= frames * 1000L / VideoFramesPerSecond && video.Audio is not { Finished: false }) EndVideo();
    }

    private static void ComposeVideoPicture(VideoPlayback video)
    {
        var source = video.Decoder.Frame;
        var width = Math.Min(video.Avi.Width, VideoScreenWidth / 2);
        for (var row = 0; row < video.Avi.Height - 1; row++)
        {
            var target = row * 2 * VideoScreenWidth * 4;
            for (var x = 0; x < width; x++)
            {
                var from = (row * video.Avi.Width + x) * 3;
                for (var copy = 0; copy < 2; copy++)
                {
                    var to = target + (x * 2 + copy) * 4;
                    video.Picture[to] = source[from];
                    video.Picture[to + 1] = source[from + 1];
                    video.Picture[to + 2] = source[from + 2];
                    video.Picture[to + 3] = 255;
                }
            }
        }
    }

    private void DrawVideo(GameCanvas canvas, VideoPlayback video)
    {
        canvas.Fill(new Rectangle(0, 0, 640, 480), Color.Black);
        canvas.Draw(new GpuImage(VideoScreenWidth, video.PictureRows, video.Picture, transient: true), 0, video.Top);
    }

    private void EndVideo()
    {
        if (_video is not { } video) return;
        _video = null;
        video.Audio?.Dispose();
        RuntimeLog.Info($"Video ended after {video.Decoded + 1} of {video.Avi.VideoFrames.Count} frames.");
        video.Then();
    }
}
