# Day and night on screen

How `dc.exe` shows the time of day. The clock itself (the SCN header, the
phase counter and the lighting ramp `world + 0x540`) is in `DayNightCycle`;
sight and damage use the lighting level too (`vision.md`, `combat-damage.md`).
Recovered on 2026-10-06, implemented in `Environment/DayNightPresentation.cs`,
`Terrain/FogShading.cs` and the app's terrain pass.

## The ground's brightness buffer

Each frame the terrain pass `0x453B94` fills a buffer with one byte per
view pixel, `brightness * 8 + colour`:

- **Brightness** (0-31, 16 leaves the ground) is the fog of war, ramped
  between tile corners (`vision.md`).
- **Colour** (0-7) is the time of day. `0x40AC6F` passes
  `(lighting * 7) >> 8`, which is 0 in full day and 7 at night, rising and
  falling over the SCN's transition ticks.

Light layers then add to it (below). The terrain blit draws each ground
pixel as `table[byte * 256 + pixel]`, the first table of the tileset's
`.rmp` (`fin-layers.md`). There, colour c moves a colour toward its grey,
about c/11 of the way (`interface-text.md`). Measured on `jungle.rmp` at
brightness 16, colour 7 keeps about 30 % of the saturation and the same
lightness. **Night greys the ground; it does not darken it.** dc16 builds
the same remap in RGB.

The sprite pass draws sprites through a single row each. `0x435FCC` stores
`16 * 8 +` the owner's colour (`+0xC98`) with every draw-list entry. As far
as traced, sprites do not read the brightness buffer, so units keep their
colours at night and under lights.

## Lights

FIN draw type 3 is a light (`fin-layers.md`). Before the main pass,
`0x4546EA` sends each type-3 layer of the draw list to `0x4621A0`, which
returns at once unless GAME DETAIL is medium or high (`0x4E686C`). It adds
the layer's pixels to the brightness buffer; a carry sets the top five bits
(`0x45C5DD`).

The shipped light sprites use multiples of 8, so each pixel adds
`pixel / 8` to the brightness, up to 31: nearly twice as bright.
- `spot` reaches 160, +20.
- `smsp`, `blaz`, `llll` and `side` are smaller.

They light:
- the ground under explosions (`NUKE`, `GASY`, `spon`), napalm and Petra-7
  vents (`VENTSTAND0`);
- trooper, Gray, Reaper and turret fire (`blaz`);
- the lamp animations (`light1f`, `light2f`, `smsp*.fin`).

Only what is drawn lights the ground, so a light in the fog shows nothing.

## The HUD clock

`0x43A982` loads `sprites/cloc.spr`: 36 frames of a dial whose hand goes
round once. `0x43A9F8` picks the frame:

- By day: `trunc(phase ticks / (cycle limit / 18))`, using `world + 0x530`
  and `+0x534`. The divisor is a float, and `0x42B63A` truncates.
- At night (`+0x53C` set): the same plus 18.

The last tick of a phase would reach the other half, so it is held one frame
back. The frame is drawn at (608, 450) over the dial painted in the HUD
background, only when it changes.

## Port status

| Rule | Status |
| --- | --- |
| Night tint of the ground | implemented. Terrain tiles are remapped through the tileset's `.rmp` at brightness 16 and the night colour, one tile set per colour (`TerrainNightColour`). The fog's brightness is applied over them, so the port's colour and brightness are separable, which the 8-bit table is only nearly |
| Lights | implemented at GAME DETAIL medium or high. World sprites keep their type-3 layers (`WorldLight`). Each frame lays the lights the previous frame drew on the ground, since sprites are drawn after it. The fog's tile overlay handles the rest. Where lights fall, the brightness of each pixel is drawn as a grey with `SpriteBlend.Light` (2 x source x frame) |
| HUD clock | implemented (`DrawGameplayClock`) |
| Sprites at night | unchanged, as traced |
| Lights on never-seen ground | partly: the port lights only explored ground. The original's buffer starts at 0 there and a light lifts it, but such lights are rare, because effects there are not drawn |
