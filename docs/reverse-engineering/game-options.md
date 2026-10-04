# In-game options

The OPTIONS button on the game options tab opens `intrface/lopte`, drawn
over `intrface/popp`. `0x432D50` opens it and `0x432B00` runs it. The game
does not update while the popup is open.

| Row | Buttons | Range | Effect |
| --- | --- | --- | --- |
| GAME SPEED | 40 / 41 | 10-200 %, steps of 10 | On commit, the world update interval becomes `0x19C8 / speed` ms (100 % = 66 ms, `0x432B47`). In a network game the new interval is sent to the other players (`0x4214E4`). Opening shows `0x19C8 / interval / 10 * 10` %. |
| SOUND VOLUME | 42 / 43 | 0-10 | Applied at once through `0x452B2C(v, 1)`. That function sets the mixer's wave-out volume control to `min + (max - min) / 10 * v`. |
| CDROM VOLUME | 67 / 68 | 0-10 | Applied at once through `0x452B2C(v, 0)`. That function sets `auxSetVolume(v * 0x1800)` on both channels of every CD-audio aux device (`0x452BA4`). |
| GAME DETAIL | 44 / 45 | LOW, MEDIUM, HIGH | `0x435CA0` sets two flags from it every frame: `0x4E686C` for MEDIUM and up, `0x4E686D` for HIGH. The world blitters (`0x4618C0`, `0x461D30`, `0x4621B0`) check them for their blended paths. |

Button 56 commits the values to the profile fields (`+0x4690` sound,
`+0x4694` CD, `+0x4698` detail). Button 55 closes without committing, but
volumes already applied stay. The defaults come from `0x478CC4`: detail 2,
sound 5, CD 5, interval 66.

The value fields (in_text 46, 47, 69 and 48) show `%d%%`, `%d`, `%d`, and
textmsg 10-12.

## Port

`GameOptions` reads the defaults from dc.exe and holds the stepping rules. `MainForm.Options.cs` draws the popup from `lopte`'s layout and `popp`'s frames, with its labels read from `lopte`.

| Setting | In the port |
| --- | --- |
| Game speed | Replaces the fixed-step clock with the committed interval. The simulation stays deterministic per update; only wall-clock pacing changes. |
| CD volume | Set as the CD stream's own waveOut volume, with the native word `v * 0x1800`. |
| Sound volume | Stored but not applied. The original changes the system mixer, which the port leaves alone, and the port's effect sounds play through `SoundPlayer`, which has no per-sound volume. |
| Game detail | Stored. The port has no detail-dependent blitter paths yet. |

The value fields have no `remap` in `lopte`. Their default colour was not
found, so the port draws them in colour 4, the labels' colour.
