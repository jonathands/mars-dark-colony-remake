# Single Player War lobby (`intrface/multie`)

Status: executable-confirmed for control ranges and identifiers; screen
geometry and labels are data-derived from the shipped `intrface/multie`
definition; gameplay application of every option remains to be recovered.

The original Single Player War setup screen is the `multie` definition over
`intrface/tcpwait`, not `shumane`. Its map list is at `(29,200,535,114)` and
presents the complete `scenario/mplayer` catalogue by SCN display title. The
captured first titles are `4 Kingdoms`, `Armageddon`, `Beon Bay`, and `Big
Crater`.

`multie` also uses a distinct palette treatment for its `knobe` controls. The
same source sprites are green in the generic menu, while the captured War
screen maps their three control shades from `(12,36,0)`, `(28,77,0)`, and
`(48,117,0)` to `(7,7,7)`, `(65,8,0)`, and `(175,11,15)`. The port applies
that narrow indexed-source mapping only to War buttons, option selectors, and
arrows, preserving the sprite's black, grey, cyan, and glyph colors.

The same capture distinguishes font roles: map titles and multiplier values
remain cyan; ordinary War labels use `(91,203,0)`; the active Human type uses
`(79,7,7)`; and War button captions use `(159,19,19)`. These are presentation
states, not faction or gameplay rules.

`chaa.fin`'s `CHAB` range is not a static foreground frame. It is a 29-frame
dynamic state-mask range made of 607×20 row composites; displaying arbitrary
frames overwrites unrelated parts of the lobby. Continue to use `CHAA` as the
stable structural layer until the native CHAB state-selection function is
recovered from the executable.

The Ready column is independent `checkb` gadgets 16-23, each 27×17 at
`(610, 18 + 19×row)`. The port renders those data-derived bounds and binds
their selected state to the roster's Ready flag. Their exact native checkbox
skin remains a presentation-detail follow-up, separate from CHAB.
The refresh routine at `0x40f539-0x40f55b` confirms this mapping: it reads
eight Boolean values at `+0xa690 + 4×row`, normalizes each to zero/one, and
writes gadget IDs `0xb4-0xbb` (180-187).

The RaceFace event handler at `0x41145f-0x411521` cycles its packed row value
through `0 → 1 → 3 → 0`, explicitly skipping `2`. Only 0 (Human) and 1
(Gray) are supported by the recovered player labels and SCN faction data. The
port therefore deliberately offers those two playable launch choices and does
not assign an invented meaning to native value 3.

The compiled port passes a selected map through `SinglePlayerWarLaunch`. That
data-derived boundary resolves the selected Human or Gray faction to the
matching enabled `ScenarioTeam.TeamId`; an unavailable faction is rejected,
never substituted with another SCN team.

Free-War SCNs use shared Human starter slots, including on Gray teams. At
launch the port converts only the selected local team's basic troop/mech
placeholders (0/2 ↔ 8/10) and commander slot (Human 69-72 or Gray 73-76) for
the selected faction/rank. This is required before creating the
world/simulation; otherwise a correctly selected Gray team still controls
Human marine art.

In the port, click the first active player's **Race** label or face to switch
between Human and Gray. If the current map has no team of that race (for
example, the initial `4 Kingdoms` entry has only a Human team), the lobby
selects the first compatible map before `READY` can start it. The chosen
faction and resolved SCN team then become the gameplay selection and control
filter; they are not inferred from team zero.

It also retains a validated `SinglePlayerWarSettings` record (the native lobby
values) with that launch. This makes the values available at scenario start
without yet asserting unrecovered gameplay behavior.

## Option controls

The following IDs and ranges were traced in the `dc.exe` lobby event dispatcher
at `0x4112dd-0x4116f6`; the display refresh lives at `0x40f310-0x40f572`.

| Setting | Control IDs | Native state | Confirmed UI range |
|---|---:|---|---|
| Storage Cells | 105-108 | `+0xa670` | OFF, LOW, MED, HIGH (0-3) |
| Artifacts | 110-113 | `+0xa674` | OFF, LOW, MED, HIGH (0-3) |
| Erupting Vents | 115-116 | `+0xa678` | OFF, ON |
| Renewable Vents | 118-119 | `+0xa67c` | OFF, ON |
| P7 Quantity | 122/123 | `+0xa684` | 1-20, rendered as `state * 25%` (25%-500%) |
| P7 Flow | 126/127 | `+0xa680` | 1-20, rendered as `state * 25%` (25%-500%) |
| Commander Rank | 130/131 | `+0xa688` | 0-3 |

Rank text is selected from Human messages 30-33 (`LEUT.`, `CAPT.`, `MAJ.`,
`COL.`) or Gray messages 40-43 (`XIMAL.`, `IDRAC.`, `SITRUC.`, `REGLIA.`).

The current port mirrors those control choices in the UI state. It must not
claim that Storage, Artifacts, vents, P7, or rank affect simulation until the
launch packet/state-to-scenario handoff is separately traced and implemented.

## Verification against the installed corpus

Run the deterministic checks from the port directory with:

```powershell
dotnet run --project tests\DarkColony.Engine.Checks\DarkColony.Engine.Checks.csproj --no-build
```

With the supplied installation, the catalog contains 56 complete War maps:
54 expose a Human roster and 30 expose a Gray roster. Every one of those 84
valid faction/map selections resolves an SCN team with a mobile unit and
completes a local movement order. This verifies the selection-to-gameplay
boundary, not unrecovered AI, resource, or power-up rules.
