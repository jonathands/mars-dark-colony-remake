# FIN draw layers

A FIN draw-layer record is 22 bytes:

| Field | Size |
| --- | --- |
| Sprite name | 8 |
| Sprite frame | u16 |
| X | i16 |
| Y | i16 |
| Trailing words | 4 × u16 |

The world blit places a layer's bottom row on its Y (`0x454751`; see
`AnimationDefinition.Compose`).

## The mirror word

The fourth trailing word mirrors the layer left to right. It is derived from
the shipped data; the blitter's own test has not been traced yet. Three
observations support it:

- **Only 0 and 1 occur.** In the files the parser reads, 11,871 of 56,605 layers set it, in 72 files.
- **Right-facing directions reuse left-facing frames.** They do so with the word set: `EXPLSTAND4` is expl frame 4 at FIN x -29, the frame that `EXPLSTAND12` draws at x -159.
- **One placement rule makes every pair exact mirrors.** A mirrored layer is drawn flipped with its left edge at X − 1, without the sprite frame's own X. `EXPLSTAND12` then spans x −26..30 and `EXPLSTAND4` −30..26, pixel for pixel mirrored. In `SCGMSTAND2`, where the engine-glow layers switch between mirrored and ordinary from frame to frame, the hull and both glows keep their places in all four frames.

An ordinary layer starts at its X plus the sprite frame's X.

Before this rule, the port ignored the word. Every mirrored direction of every
unit and effect was drawn unflipped and about 125 px to the right, which made
idle VTOLs and Exploiters jump sideways. The check *mirrored FIN layers draw
flipped at their X - 1, opposite their source direction* covers it.

## The draw type

The third trailing word, 0 to 5, is the layer's draw type. The sprite queue
(`0x435FCC`) stores it at entry `+0x17` (`0x4360B4`). The world blitter
(`0x45467C`) dispatches on it through the table at `0x454664`:

| Type | Routine |
| --- | --- |
| 0 | `0x46152C` / `0x461170` |
| 1 | `0x461D14` |
| 2 | `0x454BCD` |
| 3 | not drawn in the main pass (`0x454741` skips it; the table entry is the loop's end) |
| 4 | `0x462808` / `0x462468` |
| 5 | `0x4627E4` / `0x462444` |

Type 3 is a light. Before the main pass, `0x4546EA` sends each type-3 entry
to `0x4621A0`. That routine returns at once unless `0x4E686C` is set (GAME
DETAIL medium or high; see `game-options.md`). Otherwise it writes the
layer's opaque pixels into a per-tile buffer, not the screen. Each pixel
becomes `(source + destination) & mask` (`0x45C5DD`; a carry sets the top
five bits).

The type-3 layers are:

- the `spot` and `smsp` ellipses under explosions, napalm and vents;
- the `blaz` flash of Gray and marine fire;
- `llll` and `side` in the light animations.

The port draws no light map. It therefore leaves type-3 layers out of world
sprites (`DrawLayer.IsLight`, `AnimationDefinition.Compose` with
`bottomAnchored`). Before this, each explosion showed an opaque grey ellipse
over the ground.

## Draw types 1 and 2: shadows

Type 2 (`0x454BCD`) only calls `0x4618C0`, or `0x461D14` when `[0x4891DC]` is
set. That routine's span writer (`0x4603A7`) replaces each screen pixel under
an opaque sprite pixel with `table[0x48 * 256 + screen]` (`mov ah, 0x48` at
`0x460458`): row 0x48 of the first blend table below, colour 0 at brightness
9. The sprite's shape darkens the ground to 9/16; it is a shadow. Type 1
(`0x4549F6`) draws the same shadow and then the sprite, so a ground unit's
shadow is hidden under it.

Type-2 layers are the shadows of things in the air:

- the VTOL's (`scgm`) in its build animation `SCGMBUILD0`, whose last frame
  draws the craft at FIN y -35 and its shadow at y 37;
- the Gray saucer's in `ORTUBUILDSTAND0`;
- the Drop Ship's and the saucer transports';
- some others (`fuel`, `gray`, `scou`, `towr`).

The port drew them as ordinary sprites, so a VTOL leaving the factory
looked like two. It now draws type 2 through the same blend as type 4 with
that row. Type 1 still draws as an ordinary sprite: its shadow lies under the
sprite.

## Draw types 4 and 5: blend tables

Types 4 and 5 look up a table instead of writing the sprite pixel.
`0x462468` (type 4) writes `table[sprite * 256 + screen]`, where the table is
`[0x4891FC]`. `0x462444` (type 5) adds 0x10000 to that pointer (the
`inc word [0x4891FE]`), calls the same routine, and restores it. The pairs
in the dispatch table differ only in the direction they walk the sprite
(`push 1` / `push -1`).

`0x44F200` fills the tables. It reads `<name>.rmp` (0x30000 bytes, three 64
KB tables) and builds them itself when the file is missing. The world uses
the tileset's file, `jungle.rmp`, `desert.rmp` or `atlantis.rmp` beside
`dc.exe`, with the palette of the `.gif` of the same name.

- **The first table** is the colour/brightness remap of `interface-text.md`,
  laid out `brightness * 0x800 + colour * 0x100 + index`. A type-4 sprite
  pixel p is therefore brightness `p >> 3` and colour `p & 7` applied to the
  ground: the `haze` smoke (`spon`, `smae` and `nuke` layers of `haze.fin`)
  darkens it.
- **The second table** (type 5) has glow ramps in rows 32-79: 32 leaves the
  ground, 33-47 whiten it, 48-75 tint it red, then orange, then yellow, and
  76-79 make it white. Explosion fire (`nuke`, `gasy`, `spon`, `smae`),
  sparks, smoke, and muzzle and hit flashes are type 5.
- The third table scales the ground by row / 128 (128 leaves it). It is
  probably the light map's; the port does not use it.

The port draws in RGB, so `NativeBlendTables` turns each row into the
least-squares fit of `colour + m * ground` (one m for all channels) over the
256 palette colours, and the GPU applies it with `SpriteBlend.Modulate`
(`frame = sprite + frame * sprite alpha`). The fit leaves an RMS error of
about 10-25 levels per channel, partly the palette's own rounding. A frame's
type-4 and type-5 layers are composed into images of their own and drawn
after its other layers, so a translucent layer that the original drew under
an ordinary one now goes over it. Before this the port drew both types as
ordinary sprites: grey and white blobs instead of fire.

The first two trailing words remain unnamed.
