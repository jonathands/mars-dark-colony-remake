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
| 13 | A Single Player War is never won after destroying everything | done: the native War end (0x40DEAC); units and empty positions' placed units count |
| 14 | P7 multipliers in Single Player War (and probably multiplayer) do not work as intended | done: all six lobby options reach players 1-6 stat 0 (0x40183B) |
| 15 | The Barrage's long-range attack does not animate correctly | done: the shell ends in its boom template's NUKE or GASY explosion, leaves the barrel at its muzzle frame, and the fire animation plays on the native clock |
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

Still different: GASY's `spon` layer (draw type 4) shows as a white blob. The
original draws type 4 through a remap table that is not verified yet.

The goldens changed for three reasons:

- the fire event keeps the whole presentation draw (the variant is that
  value modulo the shooter's family count; SCYT has three families, so the
  low byte gave the wrong one);
- projectiles show their launch delay;
- the extra draw shifts the stream after an explosion (human15, alien06,
  atrain6).

