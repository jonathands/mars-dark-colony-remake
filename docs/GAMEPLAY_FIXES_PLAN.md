# Gameplay fixes from play testing (2026-10-05)

Branch `feature/gameplay-fixes` (from `feature/display-modes`). Each item is
fixed from executable evidence where there is some, and the evidence goes in
`docs/reverse-engineering/`.

| # | Report | Status |
| --- | --- | --- |
| 1 | Troops are created on click; they should queue on the button and leave on BUILD | done |
| 2 | Buildings can be bought more than once | not a bug (the user confirmed); the count still allows one |
| 3 | Upgrades cannot be bought | done: research needs no building |
| 4 | A pylon is missing in the built base (reference: a screenshot of the original) | open |
| 5 | Buildings are not instant: a ship lowers them (Angel1 for Humans, the Gray disc) | open |
| 6 | An options menu, in a corner for now: resolution, intro video off, show FPS | done: OPTIONS in the main menu's top-right corner |
| 7 | The land mine never attacks | done: the ring scan admits the eight neighbours by cell, not by a jitter-sensitive 8.8 distance |
| 8 | The dropship button and action do not work | open |
| 9 | A troop leaves its building with an animation (the marine from the barracks, the Gray from its hive) | open |
| 10 | Some commands, attack-move among them, do not work | done: Move & Attack is the original's checked pair, and the original mouse buttons are the default (MOUSE option) |
| 11 | A marine hit by a Gray beyond its own range does not respond | open: the plain case works (new Acquisition check: it fires back 4 updates later); needs the failing situation |
| 12 | Esc in a game should ask for confirmation | done: the original's lqce dialog, also for QUIT and Q |
| 13 | A Single Player War is never won after destroying everything | done: the native War end (0x40DEAC); units and empty positions' placed units count |
| 14 | P7 multipliers in Single Player War (and probably multiplayer) do not work as intended | done: all six lobby options reach players 1-6 stat 0 (0x40183B) |
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
