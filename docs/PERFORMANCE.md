# Performance

How the port's frame is built, how to measure it, and what the October 2026
pass changed. The simulation was never the bottleneck: the app around it
was.

## Measuring

| Tool | What it gives |
| --- | --- |
| `--perf` (app), or `DARKCOLONY_PERF=1` | One `perf:` log line every 2 s (see below). |
| `--load <file.dcsave>` (app) | Opens a save at its last update. A save with no steps and a large `ticks` opens a late game at once: the computer players have played that long. |
| `sweep:X1,Y1,X2,Y2,MS` (`tools/Send-PortInput.ps1`) | Holds the left button and moves every 4 ms for `MS` ms, like a hand dragging a selection box. |
| `--timing <ticks>` (checks) | The simulation alone, every installed scenario: ms per update. |
| `dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time,dotnet-common --format speedscope` | Where the UI thread's time goes. `--profile gc-verbose` samples allocations instead. |

A late-game save is the War header of any save (scenario, `war` block) with
`"ticks": 18000, "steps": []`. For example, `mplayer/d8play01` at update
18000 has 346 actors.

A `perf:` line reads:

```
perf: 60 fps, frame worst 4.3 | simulation 0.41/2.9 | build 1.08/2.0 | submit 0.33/0.7 | present 0.12/2.7
  | steps 15.5/s, loop 60/s gap worst 16.8 | wait 14.21 (frame 120, input 0, timeout 0)
  | sprites 3247/frame in 27 draws, uploads 128 (2081 KB) | alloc 5.5 MB/s, gc 2/0/0, heap 34 MB
```

- **simulation, build, submit, present:** milliseconds per frame, average/worst.
  - *build* walks the game into sprite commands.
  - *submit* is the D3D work, texture uploads included.
- **gap worst:** the longest time between two frames. At 60 Hz, anything
  over 16.7 ms is a dropped frame.
- **wait:** time spent waiting for the swap chain, and how each wait ended.

## The frame

1. **Game loop** (`Rendering/GameLoop.cs`). The frame runs whenever the UI
   thread has no messages.
   - It replaces a WinForms timer. That timer fired on the 15.6 ms system
     tick, after every paint and input message, and dropped a frame every
     couple of seconds.
   - With vsync, each frame first waits on the swap chain's frame latency
     object, with a maximum latency of one frame. So the frame starts right
     after a refresh, reads the latest input, and its present does not block.
   - Without vsync, a high-resolution waitable timer caps the loop at 240 fps.
   - Every wait wakes for input. None lasts more than 100 ms, so the
     fixed-step clock keeps its pace while the window is minimized.
   - The wait must outlast a refresh. A wait that times out while a frame is
     still queued leaves that frame's signal for later. From then on every
     wait passes at once, and the present blocks a frame late.
   - The fixed-step clock and movement interpolation read this loop's
     high-resolution clock. `Environment.TickCount64` advances in 15.6 ms
     steps, which made interpolated movement stutter.
2. **Build** (`MainForm.RenderFrame`).
   - World frames are `WorldSprite`s: a GPU image composed straight from the
     FIN, its origin, and the box of its opaque pixels. There is no GDI+
     bitmap, no `GetPixel`, and no string key per actor per frame.
   - The actor visuals of a frame are kept, in painter's order
     (`ActorVisualsInPaintersOrder`). The cursor, clicks and box selection
     reuse them while the update and the camera are the same.
   - Hit tests read the alpha of the image's own pixels, inside the opaque
     box only.
3. **Submit** (`Direct3DSurface.DrawBatch`). The frame is one sprite batch.
   - Images up to 256 pixels on a side share 2048x2048 atlas pages
     (`AtlasPacker`, shelf packing with a 1-pixel transparent gutter). Larger
     images keep a texture of their own.
   - All quads go into one dynamic vertex buffer.
   - Each run of quads from the same texture is one draw call.
   - Textures are RGBA, so pixels upload as they are.
   - Lines are row spans of solid fills (`PixelLine`), never textures.
4. **Present.** It returns at once, because the loop waited before the frame.

The simulation side:
- `EntityAnimationCatalog` keeps each entity's 16-sector selection per
  animation family, and per fire variant. Before, they were regrouped with
  LINQ for every actor, every frame: 70% of all allocation.
- `IsWithinWeaponRings` reads the few cells the target holds and looks up
  their ring (`NativeTargetRings.RingOf`). Before, it walked every cell of
  rings 0 through the range. The result is the same, and the determinism
  goldens did not change.
- `CellOccupancy` keeps each owner's cells. So releasing an actor no longer
  scans the grid, and the range test can find the target's cells.

## Results

War on `mplayer/j4play01` at update 9000 (136 actors), 1920x1080 view,
same machine:

| | Before | After |
| --- | --- | --- |
| Build (Release) | 4.3 ms | 0.7 ms |
| Build (Debug) | 5–8 ms, worst 92 ms | 1.6 ms |
| Submit | 1.2 ms, 2533 draw calls | 0.25 ms, 11 draw calls |
| Allocation | 53 MB/s, ~25 gen-0 collections/s | 5.5 MB/s, ~2/s |
| Frame gap, worst | 31–37 ms every couple of seconds (Debug: 228 ms) | 16.9 ms |
| Present | blocks ~15 ms | 0.1 ms |
| Texture uploads after a move order | ~180 a second (route line) | none |

Box selection read every pixel of the box's overlap with each candidate
through `Bitmap.GetPixel`, at about a microsecond a call. The cursor also
composed every actor two or three times a frame while units were selected.
Both now reuse the frame's visuals.

A pixel comparison of the old and new builds is identical: 0 pixels differ
on a paused `human15` with selection ellipses, health bars, the HUD and a
route line.

## Still costly

- **The first frame of a scenario** composes everything in view: about
  0.4 s at 1920x1080. New frames seen later (a unit turning or firing, an
  explosion) are composed when first drawn, at about 0.5 ms each.
- **A blocked unit's local search** (`DiagnosticLocalPathfinder.Find`) can
  take a few milliseconds. On `d8play01` late, single updates still take up
  to about 10 ms, within a 16.7 ms frame.
- The **Debug** build (what `run-debug.cmd` and `run-display` start) runs
  the same code without JIT optimizations, about twice as slow, but now
  well inside the frame.
