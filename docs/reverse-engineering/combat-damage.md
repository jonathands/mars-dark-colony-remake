# Combat damage trace

This note bounds the current collision/damage reconstruction. It separates the
proven base matrix path from the still-unresolved runtime modifiers so the port
does not mistake a displayed armor research level for an established damage
formula.

## Proven executable path

`dc.exe` contains a shared damage helper at `0x441930`, reached from the
projectile/collision callers at `0x44219b`, `0x4423b5`, and `0x442877`.

- The helper reads the target actor's current entity/state bytes.
- It resolves the target entity's defense class through the runtime catalog
  table rooted at `0x4f18c0`.
- It indexes the loaded `mbullet` matrix rooted at `0x4f98d0` using that defense
  class and the caller's weapon-class descriptor.
- The result is combined through signed 8.8 fixed-point multiplies and then
  subtracted from the target runtime-health field. One caller-supplied flag
  takes a further three-quarter branch.

The port's matrix damage is therefore correctly grounded in the shipped
weapon-class/armor-class data. Projectile effect lifetime and some modifier
timing remain a deliberate port policy.

## Armor-upgrade boundary

The traced helper reads actor-state and catalog-derived factors around the
matrix lookup, but no unambiguous player/team armor-upgrade index has been
identified in its inputs or direct callers. In particular, the trace does not
prove that a completed `depend.txt` armor item multiplies this damage, changes
the matrix column, or mutates a specific actor field.

Consequently, `ScenarioSimulation.ArmorUpgradeLevel` remains authoritative
technology state for UI and future rule recovery only. It must not change
damage until an executable dataflow connects the upgrade completion path to a
runtime collision operand.

## Next evidence needed

Trace a completed armor dependency from its player technology state into a
live actor record, then re-enter `0x441930` with a controlled before/after
case. That needs either a reliable native save/memory observation or a decoded
upgrade-completion call chain; neither has been recovered yet.

## Area-template port boundary

`weapstat.txt` column ten resolves to `boomstat.txt` template IDs. The compiled
port applies each template's authored radial percentage grid after base matrix
damage, to hostile actors around the impact cell. Native team filtering,
delayed effects, and the trailing 3×3 template grid remain untraced; hostile-
only splash is a deterministic port policy.
