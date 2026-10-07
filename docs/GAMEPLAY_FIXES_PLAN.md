# Gameplay fixes from play testing (2026-10-05)

Branch `feature/gameplay-fixes` (from `feature/display-modes`). Each item is
fixed from executable evidence where there is some, and the evidence goes in
`docs/reverse-engineering/`.

| # | Report | Status |
| --- | --- | --- |
| 1 | Troops are created on click; they should queue on the button and leave on BUILD | done |
| 2 | Buildings can be bought more than once | not a bug (the user confirmed); the count still allows one |
| 3 | Upgrades cannot be bought | done: research needs no building |
| 4 | A pylon is missing in the built base (reference: a screenshot of the original) | done: city slot 5, TOWR (81), seeded with health 1 for every player with a city (0x41C1AA) |
| 5 | Buildings are not instant: a ship lowers them (Angel1 for Humans, the Gray disc) | done: command 19 delivery, one per player, the slot rising until the build animation ends. Unlike the original, a building killed during its delivery frees its player and slot |
| 6 | An options menu, in a corner for now: resolution, intro video off, show FPS | done: OPTIONS in the main menu's top-right corner |
| 7 | The land mine never attacks | done: the ring scan admits the eight neighbours by cell, not by a jitter-sensitive 8.8 distance |
| 8 | The dropship button and action do not work | done by item 10: the engine worked; a left click on the map cancelled the target mode. Lieutenants (69, 73) have no Drop Ship or Saucer, as in the original |
| 9 | A troop leaves its building with an animation (the marine from the barracks, the Gray from its hive) | done: the building plays the troop's <code>BUILDSTAND0/<code>BUILD0 once while it produces |
| 10 | Some commands, attack-move among them, do not work | done: Move & Attack is the original's checked pair, and the original mouse buttons are the default (MOUSE option) |
| 11 | A marine hit by a Gray beyond its own range does not respond | open: the plain case works (new Acquisition check: it fires back 4 updates later); needs the failing situation |
| 12 | Esc in a game should ask for confirmation | done: the original's lqce dialog, also for QUIT and Q |
| 13 | A Single Player War is never won after destroying everything | done: the native War end (0x40DEAC); units and empty positions' placed units counted until item 28 |
| 14 | P7 multipliers in Single Player War (and probably multiplayer) do not work as intended | done: all six lobby options reach players 1-6 stat 0 (0x40183B) |
| 15 | The Barrage's long-range attack does not animate correctly | done: the shell ends in its boom template's NUKE or GASY explosion, leaves the barrel at its muzzle frame, and the fire animation plays on the native clock |
| 16 | Is the Napalm attack working? | it fired and its research gate worked, but it hit once at the impact. Now it burns as in the original: 14 hits over ~211 updates, its empty cells blocked, the NAPALM animation looping (Disease alike) |
| 17 | Selecting units makes the game hang, and the later game hangs | done: box selection and the cursor no longer read pixels through GDI+ or compose every actor again; the frame is one sprite batch from atlas pages, paced by the swap chain instead of a WinForms timer; the per-frame garbage is a tenth (`docs/PERFORMANCE.md`) |
| 18 | Add an option to hide the debugging guides (P7 markers, path traces) | done: DEBUG GUIDES in the Video panel (`--guides on\|off`), off by default. It hides the P7 vent markers, the move path and target, the waypoints, the attacked-actor boxes and the drop label |
| 19 | The game has fog of war: units outside a friendly unit's line of sight should be hidden | done: another team's units outside the local player's current sight (rebuilt every 16 updates, as `dc.exe`) are no longer drawn, hovered or clicked, nor are shots, explosions and deaths on unseen cells; the port used to black out only unexplored ground. Structures stay drawn on explored ground |
| 20 | A War's map cannot be chosen | done: the list worked but drew its selection with GDI, which the GPU frame drops, and re-centred under the pointer. It is now `multie` list 27 as `list.c` draws it: a cyan row with black text, scroll bar 30, and arrows 28/29 that scroll the rows shown |
| 21 | Some menu elements are missing or wrong (the encyclopedia among them) | done: every menu with an intrface definition (new campaign, load, War lobby, encyclopedia, network, story) draws its push and check buttons from `knobe.spr` at the native brightness, builds them up with its `banim` and ACTIVE.WAV, plays its gadgets (the encyclopedia's globes, the decorations), labels and scroll bars, and clicks with BUTTON.WAV and HLIGHT.WAV. The encyclopedia's article layout and the main menu are unchanged |
| 22 | Choosing a race in the new campaign played an animation and a sound | done: the Human portrait rezzes in with REZIN.WAV once the buttons are built; HUMAN/GRAY rez one race out and the other in, then loop it; "Type in a name for your leader" pulses (`interface-widgets.md`) |
| 23 | Some shots and explosions ignore transparency | done: FIN draw types 4 and 5 are blend tables from the tileset's `.rmp` (haze darkens the ground; fire, sparks, smoke and flashes glow over it). The port fits each table row to `colour + m * ground` and blends it on the GPU; before, they drew as grey and white blobs (`fin-layers.md`) |
| 24 | A War's Storage Cells and Artifacts options do nothing | done: the loader scatters each position's FUEL/FILL cells (`0x41C6B4`), and the map's artifact sites open only with Artifacts on (`0x41C5C0`); the triggers already filled them |
| 25 | The VTOL's build animation shows two VTOLs for a moment | done: the second one was its shadow. FIN draw type 2 is a shadow (each opaque pixel darkens the ground to 9/16 through the first blend table, `0x4603A7`); the port drew it as a sprite. The Gray saucer, the Drop Ship and the transports had the same fault (`fin-layers.md`) |
| 26 | The fog of war hides things without a dark shadow, and hides things right in front of units | done: the terrain pass (0x453B94) shades ground out of sight to 10/16 and unexplored ground to black, blending each tile between corner means; the minimap draws ground out of sight at 2/3. Actors follow the native draw test (0x4395D4): city buildings by their slot's cells (and stay drawn once seen), anything else by the cell of its position, mines only once detected. Units are hidden when outside their sight radius, which is short for some (a Gray soldier sees 4 cells by day); the shading now shows where that ends (`vision.md`) |
| 27 | The Sarge's walk slides | done: the port had misread the directional fallback offsets at 0x47950C (3, -2, 3, -2 ... instead of 0, 1, -1, 2, -2 ...). Sector s showed suffix 11 - s, not 12 - s, so the Sarge walked the eight path directions with its one-frame in-between poses, and every 16-direction unit was turned a sector. Fire muzzles follow the direction, so the goldens changed (`fin-layers.md`) |
| 28 | A War should be won by destroying all buildings (the user's rule, replacing item 13's reading) | done: a player with a city is in the game only while a city building in slots 0-4 stands, so units alone no longer keep it in and empty positions are out from the start. This departs from 0x40DE20, which counts any live actor (`war-session.md`) |
| 29 | Day and night do not show | done: the ground greys at night through the tileset's `.rmp` (colour `lighting * 7 >> 8`, 0x40AC6F); FIN light layers (explosions, vents, muzzle flashes, lamps) brighten the ground up to 31/16 at GAME DETAIL medium or high; the HUD clock (`cloc.spr`) turns through the day and the night (0x43A9F8). Sprites keep their colours (`day-night.md`) |
| 30 | P7 vents do not show active or inactive; no steam | done: the port drew no vents at all, only the P7 debugging markers that DEBUG GUIDES hides. The vent update (`0x413490`) stops a vent with no rate or under a deployed harvester (animation mode 2), and the sprite pass draws nothing for a stopped animation (`0x4397A7`). An erupting vent now plays `VENTSTAND0` (steam, yellow glow, light) in painter's order. A dry vent also runs no countdown, so a harvester on it no longer deploys (`city-and-economy.md`) |
| 31 | Friendly units appear as enemies in some missions; some maps have stray enemy marines of a third team | done: the port read each SCN team's alliance flags from the row after `%TeamAllies` (the AI slots' fifteen zeros), not the row before it, which the loader reads by position (`0x41B864` skips `%` lines; `0x41BF63`). Every team started hostile, so allies such as human01's team 1 (idle marines) were enemies. Most campaign and training missions start with allies (`city-and-economy.md`). The minimap still marks every other team red; the original uses each owner's colour, which the port does not model yet |
| 32 | Resolution changes in OPTIONS do not apply | open |
| 33 | Vents erupt without the original's sound | open: not a FIN hotspot, and the vent update plays only the deploy sound; the trigger is not found yet |
| 34 | Units of every team look alike, and the minimap marks allies as enemies (found while tracing item 31) | open: the original draws sprites through the owner's colour row (`+0xC98`, `0x435FCC`), which recolours the team ramp 138-143; the minimap takes the owner's colour (`0x4391F6`, table `0x4F01CC`), white for vents. The port draws every sprite in its own palette |
| 35 | Some projectiles look different from the original | done: the weapon loader gives a projectile animation only to weapons with a `<sprite>BULLET0` (`0x43B84F`), and the drawing loop skips shots without one (`0x439DC3`). The port drew a yellow dot for the 38 weapons without, the marines' and warriors' guns among them; it now draws nothing for them, as the original does. Visible shots now step on the native clock from their creation (`0x442998`) instead of one frame per update (`combat-damage.md`). Not compared with a live capture of the original |
| 36 | Mission intros and briefings are much richer in the original: animations, insignias, story (reference video: https://www.youtube.com/watch?v=AuirwU_O4Ek) | open. The port shows the briefing and the debrief as plain text on `storye`. The original's screens, from `intrface` and the video: `storye` types `hstory.txt`/`astory.txt` out letter by letter before the campaign. `shumane` is the briefing for both races: a wireframe Mars globe (`globes.spr`/`globeg.spr`) turning to the mission's region, the commander's portrait (ACOM/HCOM), name, rank (RANKS) and medals (SMALLMEDALS), the mission title, the briefing typed out after "DOWNLOADING TRANSMISSION", the animated panels (LEVEL, WEATH, PLASMA, WIND, MISA-MISG), and TO BATTLE / ENCYCLOPEDIA / MENU. `wingamee` is the debrief: Victory or Defeat, awards, medals, ranks and debriefing text. Nothing traced in `dc.exe` yet |
| — | The cursor was drawn 37 px below the pointer | fixed (`b120e3a`) |

## 1-3: the catalog and BUILD (`dc.exe`)

- `0x433124`, event 4 (left click on a `count` gadget): the gadget's
  dependency record (`0x43812C`) gives the price (`0x438074`) and the kind
  (`0x4381CC`: 0 building, 1 troop, 2 research).
  - A troop count stops at 50.
  - A building or research count only rises from 0.
  - The price must not exceed P7 (`+0xBAC`). It is deducted at once, and the
    count goes up by one (`0x4277FC`).
- Event 5 (right click): a count above 0 goes down by one and refunds its
  price.
- BUILD is `pushb 19`. It and a key (`0x40A696`) call `0x437F3C`. For each
  dependency record in state 1 with a nonzero count, it clears the count and
  sends the record's command:
  - command 9 for a building (slot, variant);
  - command 10 for a troop, with the count;
  - command 12 for research.
- Command 12 (`0x41CA04`): bytes (type, entity, level, player), at a price of
  level × 1000. It sets the weapon (type 0, `0x4F18B0`) or armour (type 1,
  `0x4F18B8`) level at once, and refunds when the level is already set. No
  building is involved.
- `0x437BC4` gives every record a state, and `0x437EA0` shows only the
  gadgets in state 1:
  - 0, done: a building whose city slot holds that variant or a higher one,
    or research whose level is reached;
  - 2: a prerequisite that is not in state 0, a prerequisite building still
    rising (`+0xC10`), the item itself rising, or a disabled item
    (`+0x193C`);
  - 1: everything else.
  P7 plays no part, so an item the player cannot afford stays visible.
- `maine` draws the count at the gadget's `offset` (3,3 for buildings and
  research, 38,3 for troops) in font 0, on the `erase` colour.

## 15: the Barrage's shot (`dc.exe`)

The port had four faults; see `docs/reverse-engineering/combat-damage.md`.

- **No explosion.** Weapons 10-12 (sprite BARR) have no BARREXPLODE. Their
  boom template 1 names NUKE and GASY, and the weapon loader (`0x43B8F8`)
  makes those the weapon's explosions. Each impact picks one with a draw
  from the shared stream. The port looked only for `<sprite>EXPLODE`, so the
  shell landed without an explosion. It also never made that draw, so after
  any impact of a weapon with explosions its random stream drifted from the
  original's.
- **The shell left the tank's centre at once.** It leaves from BARR FIREA's
  muzzle hotspot (slot 7, frame 1), after frame 0's 2 ticks, and is not drawn
  while it waits. The Atril waits 10 ticks.
- **The fire animation ran twice as fast.** It plays on the ordinary clock
  (2 ticks per frame for delay 0), not one frame per update.
- **A grey ellipse under every explosion.** The NUKE and GASY `spot` layer
  has FIN draw type 3, a light. The original never draws it as a sprite: it
  only writes the layer to a light buffer at GAME DETAIL medium or high. The
  port drew it opaque. World sprites now leave type-3 layers out; that also
  covers the `blaz` flash of Gray and marine fire. See `fin-layers.md`.

GASY's `spon` layer no longer shows as a white blob: draw types 4 and 5 now
blend through the tileset's tables (item 23).

The goldens changed for three reasons:

- the fire event keeps the whole presentation draw (the variant is that
  value modulo the shooter's family count; SCYT has three families, so the
  low byte gave the wrong one);
- projectiles show their launch delay;
- the extra draw shifts the stream after an explosion (human15, alien06,
  atrain6).

