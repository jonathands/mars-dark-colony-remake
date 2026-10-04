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

The other three trailing words remain unnamed. The third takes the values 0–5.
