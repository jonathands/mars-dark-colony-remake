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
over the ground. The other types still draw as ordinary sprites; what
their routines do differently is not decoded.

One lead for type 4 (`spon`, `smae`, some `nuke` layers): `0x462468` reads
the screen pixel and the sprite pixel and writes `table[sprite * 256 +
screen]`. The table is `[0x4891FC]`, which `0x44F21D` sets to the
colour/brightness remap of `interface-text.md`. As a remap, a `spon` pixel
(48-79) would darken the ground to 6/16-9/16 of its brightness. This is
unverified: no capture of the original shows it yet. So the port still draws
type 4 as an opaque sprite, which is why GASY shows a white blob.

The first two trailing words remain unnamed.
