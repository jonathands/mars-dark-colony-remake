# CD music

The original game plays its soundtrack straight from the disc's Red Book audio tracks. It drives
the MCI `cdaudio` device through `mciSendCommandA` (wrappers at `0x4513A0` to `0x451AF0`).
The sound object exposes the music methods at `+0x16C`..`+0x180` (set up at
`0x42FAE9`), and the screen object copies them to `+0xB8`..`+0xCC`.

| Method | Address | Effect |
| --- | --- | --- |
| open | `0x42F7F0` | `MCI_OPEN` the drive. A failure marks the music off (`0x47962C = 1`). |
| play track (AL) | `0x42F844` | `0x451888`: `MCI_SET` TMSF time, `MCI_SEEK` to the track, `MCI_PLAY` with no end point. |
| stop | `0x42F878` | Stops the drive. |
| play from 2 | `0x42F894` | `0x451888(device, 2)`. |
| poll | `0x42F8B8` | `MCI_STATUS` mode, mapped by `0x451A80`: open 1, not ready 2, stopped 3, failure 4, otherwise 0. On 3 or 4 it closes and reopens the drive, then plays from track 2. |

## Callers

| Caller | Address | What it does |
| --- | --- | --- |
| Main menu | `0x404BA7` | Opens the drive only; nothing plays. |
| Gameplay setup | `0x41EF4F`, in the HUD setup that picks group 84/53 | Plays from track 2. Without an end point the disc runs through its last track. |
| Gameplay loop | `0x431EF6` | Polls once more than 5000 ms have passed since the last poll (`0x4D199C`, which starts at 0). A finished disc therefore starts over at track 2 within five seconds. |
| Quit | `0x404D4D` | Stops the music. |
| `0x401A5A` | | Stops the music, apparently before a video. |

Nothing stops the music when a mission ends. It plays on through the
debrief and menus, but it only restarts during gameplay.

## Port

`CueSheet` reads a BIN/CUE image: 2352-byte sectors, little-endian PCM.
`CdMusic.Pass` is the native schedule (tracks 2 through the last), and `CdMusicPoll` is the
5000 ms poll. The app streams the tracks through winmm `waveOut` (`CdAudioStream`), which
Windows mixes with the effect sounds.

The image is found in this order:

1. `--cd-image <cue>`;
2. `DARKCOLONY_CD_IMAGE`;
3. a CUE sheet in the installation folder or the folder above it.

`--no-music` turns the music off, and `tools/Run-Port.ps1` passes it unless `-Music` is given.

The user's `DCUK.bin` has data track 1 and audio tracks 2-5 (195, 80, 137 and 198 s).
A War game started the stream from track 2 and played it in real time: 18.8 s of audio in 19.35 s.

The original's music volume (`auxSetVolume` and the mixer imports) belongs to the options
screen and is not modelled.
