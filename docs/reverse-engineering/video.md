# Full-motion video

The original plays its videos through `avi.c` (`0x406A00`-`0x408E90`): AVIFile for
the streams, and the system Cinepak codec through `ICDecompress`. The disc carries
them in `dc/avi`: Cinepak, 320x180, 15 fps, with 8-bit mono PCM at 11025 Hz.

## When videos play

| Caller | What plays |
| --- | --- |
| Startup (`0x404FE4`) | `avi/intro.avi`, then the main menu (`0x404B1C`). |
| PLAY INTRO (`0x404DF8`, menu choice 0x10) | `avi/intro.avi`. |
| End of a campaign mission (`0x403A48`) | The scene-list record's first video after a victory (stat 0 equals the victory value), the second after a defeat. `0x403B06` picks the list: `hscene`, `gscene`, `htscene` or `gtscene`. |
| A won War (`0x404720`) | `avi/hvad1.avi` (Human) or `avi/avhd1.avi` (Gray). Neither is on the disc. |

`0x401028` builds the path and checks that the file opens (`"rb"`); a missing file
plays nothing. Data files load from the installation first and fall back to the
CD path (`0x405DE0`).

The user's installed `hscene.txt` and `gscene.txt` name `avi/pp.avi` for every
mission, with the original lists kept as `.orig`. The port follows the installed
lists, so their mission videos are skipped.

Scene list layout:
- 8 name lines;
- a mission count;
- 9 lines per mission:
  - scenario;
  - title;
  - location;
  - scenario path;
  - victory video;
  - defeat video;
  - three lines not decoded yet: three numbers, a flag, and a list ended by -1.

## Playback (`0x4088B4`)

**Threads.** Playback runs on three threads:
- the decoder, which fills a frame ring;
- the sound, through DirectSound;
- the display (`0x4086C4`).

The display shows frame k at `k * 1000 / 15` ms. The rate is hard-coded, not read from the AVI. Playback ends when the display has shown the last frame and the sound has finished. The main thread peeks only `WM_KEYDOWN` (`0x408C40`), so a key press ends playback early.

**Picture.** The preferred mode (`0x478CF0 = 1`, `0x407240`) locks the back buffer:
- The screen is cleared to black and flipped twice first (`0x401101`).
- Each 24-bit picture row is written to every other screen row from `240 - (height - 1)` (`0x407204`), which is 61 for 180-row videos.
- Each pixel becomes two (one dword).
- The bottom-up DIB is read from its top row for `height - 1` rows, so the picture's last row is not shown.
- The fallback mode (`0`, `0x407478`) converts into a 320x180 off-screen surface and `BltFast`s it to (160, 2 x 61).

## Port

| Piece | What it does |
| --- | --- |
| `AviFile` | Parses the RIFF. |
| `CinepakDecoder` | Decodes to RGB24. Every frame of `INTRO.AVI` (2233) and `HTRAN3.AVI` (89) that was compared matches `ffmpeg -pix_fmt rgb24` hash for hash. |
| `CdImageFiles` | Reads the ISO 9660 file system on the CD image's data track. Videos come from the installation's `avi/`, else the image's `dc/avi`. |
| `SceneList` | Parses the scene lists. |
| `MainForm.Video.cs` | Plays the video with the preferred mode's layout and the 15 fps schedule. The sound goes out through waveOut (`WaveOutStream`), and any key skips. Mouse input is ignored while a video plays. |

`--no-video` skips videos, and `tools/Run-Port.ps1` passes it and `--no-music` unless `-Media` is given.
