# Display modes plan

Goal: run the port fullscreen at the monitor's native DPI, with selectable
resolutions. Branch `feature/display-modes`, from `master` f969ac0. Written
2026-10-04. The goal prompt at the end can be passed to a fresh session, for
example with `/goal implement docs/DISPLAY_MODES_PLAN.md`.

This is an enhancement beyond the original, which only ran at 640x480. The
640x480 "classic" picture stays the default and stays exact. Nothing here may
change the simulation, so the determinism goldens must not change.

## Where the port stands

| Piece | Today |
| --- | --- |
| Window | `MainForm`: client 640x480, `FixedSingle`, no maximize (`MainForm.cs`) |
| DPI | `ApplicationConfiguration.Initialize()` with no `ApplicationHighDpiMode`, so WinForms runs system-aware. A 640x480 client is small on a 150% monitor, and moving to another monitor makes Windows stretch it |
| Swap chain | `Direct3DSurface`: back buffer fixed at 640x480, `SwapEffect.Discard`, windowed only. There is no `ResizeBuffers`, so a larger window would be stretched by DXGI with bilinear blur |
| Frame | Every sprite is drawn into a 640x480 `_nativeTarget`, then one quad copies it onto the back buffer with point sampling (`DrawTexture`). That is the right place to scale |
| Mouse | Every handler reads `MouseEventArgs.Location` as a 640x480 coordinate. There is no mapping |
| Cursor | Gameplay draws the `curs.fin` cursor itself. Menus use the OS hand cursor |
| Layout constants | The world view `516x458` appears 15 times each in the app, plus `GameplayViewport` (4,6,512,448), `GameplayMinimapBounds` and the `maine` rectangles |
| Engine | No dependence on the camera or screen pixels (PORT_CONTRACT forbids it). Checked: nothing in `src/DarkColony.Engine` reads a camera or viewport |
| Tools | `Send-PortInput.ps1` posts 640x480 client coordinates; `capture-port-window.ps1` uses `PrintWindow`; `Test-LiveConstruction.ps1` relies on both |

## Two kinds of resolution

1. **Output resolution:** the pixels on the monitor (window size, desktop
   size or a display mode). The game picture is scaled onto it.
2. **Logical resolution:** the size of the game picture.
   - Classic is 640x480 for every screen, as in the original.
   - Expanded applies to gameplay only. It is larger, so the map view grows
     and the HUD sits against the right and bottom edges. Menus, briefings,
     the encyclopedia and videos stay 640x480 pictures, scaled to fit.

The two combine. For example, a 1920x1080 monitor with expanded gameplay at
scale 2 gives a 960x540 logical picture, drawn pixel-doubled.

## Design

### Presentation transform

A pure `DisplayLayout` type, with no WinForms or Direct3D types:

- **Inputs:** logical size, output size and scaling mode.
- **Outputs:** the destination rectangle and the logical↔output point mapping.
- **Modes:**
  - **Integer** (default): the largest whole scale that fits, black bars,
    point sampling, pixel-exact.
  - **Fit:** keeps 4:3 and fills one axis. Uses sharp-bilinear: scale up by
    an integer with point sampling, then filter down to the target. This
    avoids the uneven pixels of nearest-neighbour at non-integer scales.
  - **Stretch:** fills the output and distorts. Optional; marked as not
    faithful.
- **Mapping:** points in the bars are clamped to the logical edge. In
  gameplay this keeps edge scrolling working when the pointer is in a bar.

### Input

- `Direct3DSurface` turns every mouse event into logical coordinates before
  raising it. Existing handlers keep working unchanged.
- `Cursor.Position` reads, if any, go through the same mapping.
- An optional cursor confinement (`ClipCursor`):
  - on by default in fullscreen during gameplay;
  - a setting in windowed mode, because the 3-pixel scroll edge
    (`gameplay-input.md`) is hard to hold in a window.
- Software cursor in menus when the scale is above 1, so the cursor scales
  with the picture.

### Swap chain and DPI

- `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` and
  `DpiChanged` handling. The back buffer always equals the client area in
  physical pixels, so Windows never stretches the window.
- Flip model (`FlipDiscard`, two or three buffers), with `ResizeBuffers` on
  every resize, and optional tearing/vsync.
- `MakeWindowAssociation(NoAltEnter)`: the port handles Alt+Enter itself.

### Display modes

| Mode | How |
| --- | --- |
| Windowed | Client = logical × window scale (1x, 2x, ..., the largest that fits the work area). Resizable; the picture is scaled by the presentation transform |
| Borderless fullscreen (default fullscreen) | `FormBorderStyle.None` over the bounds of the monitor holding the window; output = monitor resolution |
| Exclusive fullscreen | `SetFullscreenState` plus `ResizeTarget` to a mode chosen from `IDXGIOutput.GetDisplayModeList`: the "selectable resolutions" of the display (800x600, 1024x768, 1920x1080...). A new mode is kept only after a 15 s confirmation, otherwise it reverts |

Robustness:

- Losing focus in exclusive mode minimizes the window and stops presenting.
- Device loss (`DXGI_ERROR_DEVICE_REMOVED`) recreates the device and the GPU
  images.
- A mode that fails falls back to windowed and logs why.

### Expanded gameplay (logical size larger than 640x480)

- **HUD frame:** composed at run time from the user's `intrface.gif`; nothing
  is copied into the repo. The fixed parts are:
  - the top and left borders;
  - the right panel (x ≥ 516) with the minimap, tabs and command slots;
  - its lower block (BUILD, days, P7, dial);
  - the bottom message strip.

  These are anchored to the edges. The gaps are filled by repeating plain
  slices: the border, the empty command column, and the middle of the
  message strip.
- **Controls:** every `maine` control moves with its block. The right panel
  shifts by (W − 640). Its lower block and the bottom strip also shift by
  (H − 480). `GameplayHudLayout` owns this offset, so drawing and hit-testing
  cannot drift apart.
- **World view:** (4, 6, W − 128, H − 32), which is (4,6,512,448) at 640x480.
  It becomes one property used instead of every `516`/`458`:
  - the camera clamp;
  - fog;
  - selection;
  - the actor pick;
  - terrain cache sizes;
  - the minimap's camera rectangle;
  - minimap centring (the view centre instead of (260,230)).
- **Edge scrolling:** the 3-pixel edges of the logical picture.
- **Simulation:** unchanged; the lockstep digests do not see the view size.
  A larger view shows more explored map at once, so network games stay
  classic (decision 1).

### Settings

- `%LOCALAPPDATA%\DarkColonyPort\display.json`:
  - mode;
  - monitor;
  - display mode (W×H@Hz);
  - window scale;
  - scaling mode;
  - gameplay logical size or "classic";
  - vsync;
  - cursor confinement.
- Command-line overrides:
  - `--windowed`, `--fullscreen` (borderless), `--exclusive WxH[@Hz]`;
  - `--window-scale N`, `--scale-mode integer|fit|stretch`;
  - `--view classic|WxH`;
  - `--vsync on|off`.
- Automation (`Run-Port.ps1`, `Test-LiveConstruction.ps1`,
  `Run-NetworkPair.ps1`) passes `--windowed --window-scale 1 --view classic`,
  so 640x480 client coordinates keep working. The app logs a
  `Presentation:` line (output size, destination rectangle, logical size), so
  scripts can map coordinates when they test other modes.
- An in-game **Video** panel, port-only and labelled as such, drawn with the
  native widgets and fonts (`popp`/`knobe`, `mfonto*`):
  - **Main menu:** opened with F10.
  - **Game:** opened by an extra button in the OPTIONS popup.
  - **Hotkey:** Alt+Enter toggles windowed / fullscreen.

## Phases and done criteria

**Phase 0: groundwork** — done (2026-10-04)
- Branch and this plan.
- Instead of a separate WinForms check project, the presentation rules live
  in a new platform-free library, `src/DarkColony.Presentation` (net8.0, uses
  only `System.Drawing` primitives). The app and the engine checks reference
  it, so its checks (group `Presentation`) run in the normal check command.
  First contents: `DisplayLayout` (integer / fit / stretch placement and the
  output↔logical mapping) and `DisplaySettings` (flags, JSON).
- Inventory of screen geometry in the app (2026-10-04, `master` f969ac0):

  | What | Where |
  | --- | --- |
  | 640x480 client and target | `MainForm.cs` (`ClientSize`, `MinimumSize`/`MaximumSize`, background draw), `Direct3DSurface.NativeWidth/Height`, `MainForm.Video.cs` (`VideoScreenWidth`, black fill), `MainForm.Assets.cs` (two 640x480 bitmaps), `MainForm.Hud.cs` (HUD draw and black fills) |
  | World view 516x458 | `MainForm.Commands.cs` (order guard), `MainForm.Input.cs` (selection/middle drag start, `SelectGameplayActor`, `FindGameplayActorAt`, box selection viewport), `MainForm.World.cs` (terrain cache cells, region overlay, vents, fog, placement, actor clip, cursor test, `ClampGameplayCamera`, minimap camera rectangle `516d/458d`) |
  | Native view (4,6,512,448) | `MainForm.Hud.cs` `GameplayViewport`; minimap centring (260,230) in `MainForm.Input.cs` |
  | Edge scroll 3-pixel edges | `MainForm.Input.cs` (`x > 637`, `y > 477`) |
  | Right panel | `GameplayMinimapBounds` (519,6,96,84) in `MainForm.cs`; troop/building/research slot tables in `MainForm.cs`; `HandleGameplayHudClick` command area (518..638, 112..399); `AllianceSlots`; days counter (604,427) in `MainForm.Hud.cs`; every `maine` rectangle through `GameplayHudLayout` |
  | Popups over gameplay | the OPTIONS popup (`MainForm.Options.cs`) uses its own 640x480 coordinates |

**Phase 1: native DPI and scaled windowed presentation** — done (2026-10-04; see `docs/DISPLAY.md`, "Verified")
- `DisplayLayout` (integer / fit with sharp-bilinear / stretch).
- PerMonitorV2, flip-model swap chain with `ResizeBuffers`.
- Logical mouse mapping in `Direct3DSurface`; resizable window with scale
  presets; `DpiChanged`.
- *Done when:*
  - at 1x the picture is byte-identical to today's (screenshot diff);
  - at 2x and 3x on 100% and 150% DPI it is crisp, with no OS stretching;
  - the presentation-math checks pass: round-trip mapping, bars, clamping;
  - `Test-LiveConstruction` passes at 1x and with `-WindowScale 2`. The
    script learns to map clicks through the `Presentation:` log line.

**Phase 2: fullscreen** — done (2026-10-04). Exclusive fullscreen changes
the display mode (`ChangeDisplaySettingsEx`, temporary) under a borderless
window. DXGI's exclusive state refuses the port's child-window swap chain.
See `docs/DISPLAY.md`.
- Borderless fullscreen (default) and exclusive with the display-mode list.
- Alt+Enter, focus loss and alt-tab, device-loss recovery, cursor
  confinement, multi-monitor.
- *Done when:*
  - 20 toggles windowed ↔ borderless ↔ exclusive leave no leak (GPU objects
    counted in the log) and no crash;
  - edge scrolling works at the monitor edges;
  - a failed mode falls back to windowed;
  - live test in borderless fullscreen.

**Phase 3: settings** — done (2026-10-04). `tools/Test-VideoPanel.ps1`
drives the panel; `--display-settings <file>` keeps tests away from the
player's file.
- `DisplaySettings` with JSON persistence, command-line overrides and safe
  defaults for invalid values.
- The Video panel, with the 15 s confirm/revert for exclusive modes.
- Automation flags in the tools.
- *Done when:*
  - settings survive a restart;
  - a corrupted file falls back to defaults with a log line;
  - the panel is driven by posted input in a live test.

**Phase 4: expanded gameplay view** (decision 1: not in network games)
- `GameplayHudLayout` anchoring; the HUD frame composed from `intrface.gif`.
- A `GameplayView` property replacing every `516`/`458`.
- Logical sizes: classic, 800x600, 1024x768, 1280x720, 1280x800, 1366x768,
  1600x900, 1920x1080, and "monitor ÷ scale".
- Menus and videos stay 640x480 and scaled.
- *Done when:*
  - a Single Player War and a campaign mission play at 1280x720 and
    1920x1080 (scale 1 and 2): HUD clicks, minimap, selection, edge scrolling
    and fog all right;
  - `Test-LiveConstruction -View 1280x720` passes;
  - the checks cover HUD anchoring at three sizes;
  - the goldens are unchanged;
  - a Multi Player War started from an expanded setting plays classic
    (`Run-NetworkPair.ps1`).

**Phase 5: documentation and merge**
- `docs/DISPLAY.md`: modes, flags, file format, what is faithful and what is
  port-only.
- README flags; PORT_CONTRACT (`Rendering` gains the display layer).
- Memory.
- Full suite and both live tests; merge to `master` only when the user asks.

## Decisions (answered 2026-10-04)

1. **Expanded gameplay view:** yes, outside network games. Single player,
   campaigns and Single Player War may use a larger view. A Multi Player War
   always plays classic 640x480 (scaled), so no player sees more of the map
   than another. The lobby forces it, and the Video panel says so.
2. **Default fullscreen:** borderless. Exclusive fullscreen stays available
   for choosing a display mode.
3. **Video settings:** an in-game Video panel, opened with F10 on the main
   menu and by an extra button in the in-game OPTIONS popup. Also Alt+Enter,
   the command-line flags and `display.json`.

## Goal prompt

````text
Você vai implementar, de forma autônoma e até o fim, o plano docs/DISPLAY_MODES_PLAN.md
do port .NET de Dark Colony: tela cheia com DPI nativo e resoluções selecionáveis.
Responda ao usuário sempre em português. Não pare para pedir confirmação: as decisões
do usuário já estão na seção "Decisions" do plano.

## Contexto
- Repo: C:\Users\LY\Desktop\darkcolony\dc-port-dotnet. O remote é
  git@github.com:jonathands/mars-dark-colony-remake.git (PÚBLICO).
- Branch de trabalho: feature/display-modes (a partir do master f969ac0). Não faça merge
  na master sem pedido.
- Instalação original: C:\Users\LY\Desktop\darkcolony\Dark Colony.
- Leia antes: AGENTS.md, docs/PORT_CONTRACT.md, docs/TESTING.md, docs/DISPLAY_MODES_PLAN.md,
  docs/reverse-engineering/gameplay-hud.md e gameplay-input.md, e a memória em
  C:\Users\LY\.claude\projects\C--Users-LY-Desktop-darkcolony\memory\.

## Regras inegociáveis
- O modo clássico (640x480, escala 1) continua idêntico ao de hoje, pixel a pixel.
- Nada muda na simulação. Os goldens (`--verify-goldens`) não podem mudar.
- Nunca copie assets do jogo para o repo. A moldura expandida do HUD é composta em
  tempo de execução a partir do intrface.gif do usuário.
- Zero warnings. Checks acompanham cada contrato novo. Nunca desligue um check.
- Não mova o mouse real do usuário. Automação só por PostMessage (tools/Send-PortInput.ps1).
- Sem CI. O runner de checks continua sem dependências.
- Commits pequenos, com push ao fim de cada fase. A mensagem termina com:
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: <link da sessão atual>
- Seja honesto: diga o que não foi verificado e o que ficou provisório.

## Ferramentas
- dotnet no Bash: "/c/Program Files/dotnet/dotnet.exe". Feche o DarkColony.App antes de
  compilar.
- Checks: `dotnet run --project tests/DarkColony.Engine.Checks` (cerca de 40 s);
  `-- --verify-goldens`.
- Teste live: `pwsh -File tools/Test-LiveConstruction.ps1 -Race Human|Gray`.
- Passe arrays ao Run-Port com
  `pwsh -NoProfile -Command "& ./tools/Run-Port.ps1 -ExtraArguments @('a','b') ..."`.
- O READY do War fica em 574,463.
- Heredocs do Bash colapsam barras invertidas: escreva scripts com a ferramenta Write.

## Ciclo de cada fase
1. Implemente a fase do plano.
2. Escreva os checks e rode a suíte completa, os goldens e os dois testes live.
3. Confira as capturas com os próprios olhos.
4. Atualize o plano, marcando a fase como concluída e anotando o que mudou, e atualize
   a documentação e a memória.
5. Commite, dê push e mande ao usuário um resumo curto.
````
