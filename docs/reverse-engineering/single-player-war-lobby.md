# Single Player War lobby (`intrface/multie`)

Status: executable-confirmed for control ranges and identifiers; screen
geometry and labels are data-derived from the shipped `intrface/multie`
definition; gameplay application of every option remains to be recovered.

The original Single Player War setup screen is the `multie` definition over
`intrface/tcpwait`, not `shumane`. Its map list is at `(29,200,535,114)` and
presents the complete `scenario/mplayer` catalogue by SCN display title. The
captured first titles are `4 Kingdoms`, `Armageddon`, `Beon Bay`, and `Big
Crater`.

The compiled port passes a selected map through `SinglePlayerWarLaunch`. That
data-derived boundary resolves the selected Human or Gray faction to the
matching enabled `ScenarioTeam.TeamId`; an unavailable faction is rejected,
never substituted with another SCN team.

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
