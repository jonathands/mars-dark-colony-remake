# Combat damage trace

This note bounds the current collision/damage reconstruction. It separates the
proven base matrix path from the still-unresolved runtime modifiers so the port
does not mistake a displayed armor research level for an established damage
formula.

## Proven executable path

The runnable `dc16.exe` contains the shared damage helper at `0x441c80`.
The older `dc.exe` image contains its corresponding helper at `0x441930`.
Both images establish the same recovered fixed-point path.

- The helper reads the target actor's current entity/state bytes.
- It resolves the target entity's defense class through the runtime record
  rooted at `0x505d90` and the loaded weapon-class matrix selected by the
  caller.
- The result is combined through signed 8.8 fixed-point multiplies and then
  subtracted from the target runtime-health field. One caller-supplied flag
  takes a further three-quarter branch.

The branch is visible in the runnable `dc16.exe` helper (`0x441c80`) and in
ordinary projectile-area callers, but the call-site flag is not yet mapped to
an engine command or projectile record field. The port therefore keeps this
modifier unresolved rather than applying it to every impact.

`DamageMatrix.CalculateNativeDamage(..., reduceToThreeQuarters: true)` now
preserves that recovered branch as an explicit opt-in primitive. No current
command selects it; this keeps the arithmetic testable without inventing
ownership semantics at the projectile call sites.

`dc16.exe` `0x441c80` additionally confirms that the helper marks the target
as damaged and updates its bounded accumulated-damage byte before taking the
destruction path. Those writes are presentation/notification state separate
from the authoritative health subtraction.

## Recovered target multiplier lookup

The entity runtime records start at `0x505d90` and have a `0x118`-byte stride.
Within the damage helper, the target actor's exact team byte selects a byte at
runtime entity offset `+0x38`; that byte indexes signed 8.8 data beginning at
`+0x24`:

```text
selector   = entityRuntime[targetEntity].byte[0x38 + targetTeam]
multiplier = entityRuntime[targetEntity].dword[0x24 + 4 * selector]
damage     = fixed8_8(...matrix damage..., multiplier)
```

The object-definition load routine in `dc16.exe` (`0x41b950`) initializes the
neutral base entry at `+0x24` to `0x100` and clears the eight team selector
bytes. The same routine parses two authored, eight-team value sets into the
adjacent `+0x30` and `+0x38` areas of each `0x118`-byte entity record; the
damage helper consumes the latter area as selectors. This establishes the
exact neutral damage path and confirms that non-neutral selectors are
data-driven. The state-load routine `0x43c784` also restores exactly eight
bytes into each of `+0x30` and `+0x38` for every loaded entity record, proving
that these per-team values are persisted state rather than a damage-helper
temporary.

The compact state-update decoder begins at `0x41ca02` (field parsing reaches
`0x41ca34`) and is a direct live write path. It decodes four bytes as
`(kind, entity, value, team)`: kind `0` writes
`entityRecord[entity].byte[0x30 + team]`, and kind `1` writes
`entityRecord[entity].byte[0x38 + team]`. A local-team update calls
`0x437f24` afterwards, refreshing dependency availability. It also changes
player bookkeeping by `value * 1000`, but the producer and the meaning of its
two destination fields are not yet identified. The valid selector range and
the mapping from a completed `depend.txt` item to this packet remain unproved.

The port's matrix damage is therefore correctly grounded in the shipped
weapon-class/armor-class data. Projectile effect lifetime and some modifier
timing remain a deliberate port policy.

Projectile damage is resolved at collision, not launch. The projectile retains
the weapon's raw damage while in flight; the impact helper then reads the
armor class of the actor occupying the collision cell. This preserves the
native behavior when an intervening hostile intercepts a shot aimed at a
different unit.

## Armor-upgrade boundary

The multiplier table, team slot, and compact live update are now identified,
but the trace does not yet prove which completed `depend.txt` armor item emits
that update, which entity/selector it targets, or whether another state
transition also uses it.

Consequently, `ScenarioSimulation.ArmorUpgradeLevel` remains authoritative
technology state for UI and future rule recovery only. It must not change
damage until an executable dataflow connects the upgrade completion path to a
runtime collision operand.

## Next evidence needed

Trace a completed armor dependency to the producer of the `0x41ca34` compact
update, then re-enter `0x441c80` with a controlled before/after case. That
needs either a reliable native save/memory observation or the decoded
upgrade-completion call chain; neither has been recovered yet.

## Area-template port boundary

`weapstat.txt` column ten resolves to `boomstat.txt` template IDs. The compiled
port applies each template's authored radial percentage grid after base matrix
damage, to hostile actors around the impact cell. Native team filtering,
delayed effects, and the trailing 3×3 template grid remain untraced; hostile-
only splash is a deterministic port policy.

## Shared random consumption

The executable initializes a 256-dword table at `0x478e04..0x479203` and
increments the shared cursor at `0x479204` before each read. Common fire uses
that stream for both area-template aim selection and FIRE/FIREA/B/C variant
selection; blocked-route jitter consumes the same cursor. The compiled
simulation therefore uses one cursor for those three consumers. The table is
read from the user's `dc.exe` by `NativeRandomTable`; engine checks without an
installation use an explicitly synthetic stand-in. Its regression
case verifies that an ordinary area shot consumes table entry one for scatter
and entry two for its presentation roll. This avoids a deterministic-but-wrong
split PRNG whose outcomes diverge as soon as movement repair and combat are
interleaved.
