# Replay and lockstep

Both rest on the simulation being deterministic per update (DETERMINISM.md).
The same scenario plus the same commands at the same updates always gives
the same state, and `SimulationDigest.Hash` fingerprints that state.

## Replay

`CommandJournal` records the commands each update consumes. With a
checkpoint interval it also stores the state digest after every Nth update.
The app uses 64, so `.dcsave` files carry checkpoints (`SavedCheckpoint`).

| Piece | What it does |
| --- | --- |
| `SavedGame.VerifyReplay` | Replays from a fresh scenario and compares every checkpoint and the final digest. It returns a `ReplayCheck` naming the first update that differs. |
| `ReplayPlayer` | Does the same one update at a time. |
| `--replay <file.dcsave>` (app) | Opens the saved scenario and plays it from update 0 at normal speed, using only the recorded commands; player input is ignored. At the end it reports in the status line and the log whether every checkpoint and the end state matched, then pauses. |

The check *a recorded War match replays bit for bit* proves both properties:
- 400 updates of a scripted match on Dead Man's Wharf replay with all 8 checkpoints matching.
- Dropping one recorded step is caught at the next checkpoint.

## LAN lockstep

`LockstepSession` (`Engine/Network`) runs one peer:
- **Turns.** Every peer runs the same simulation. A local command goes into this peer's turn two updates ahead (`InputDelay`). An update runs only once every player's turn for it has arrived, so peers stay within that delay of each other.
- **Order.** Each update's commands are ordered by player and queue order, and every peer gives them the same sequence numbers.
- **Digests.** Every 16 updates (`DigestInterval`) the peers exchange digests. The first mismatch stops the session with a `LockstepDesync` (update, player, both digests).

Messages (`LockstepTurn`, `LockstepDigest`) are JSON lines:
- `TcpLockstepTransport` hosts or joins over TCP. The host relays between its clients.
- `LoopbackLockstepNetwork` connects peers in one process.

Evidence:
- **In one process.** The check *two lockstep peers over TCP stay in sync and a desync is caught* connects two sessions over loopback TCP. Each scripted commander orders only its own team. After 600 updates both states have the same digest, with every exchange confirmed. A state change made outside the lockstep is caught at update 48, the first digest exchange after it.
- **Across processes.** Run in two terminals:

  ```text
  dotnet run --project tests/DarkColony.Engine.Checks -- --lockstep host 47123 1000
  dotnet run --project tests/DarkColony.Engine.Checks -- --lockstep join 47123 1000
  ```

  Recorded run, 2026-10-04: both processes printed identical digests every 100 updates, then "1000 ticks in sync, 62 digests confirmed" after 7.7 s.

## The original

The original's network code is DirectPlay (`dplay.c`, `net.c`, `sync.c`; packet
table at `0x500F80`). Its opcode 1 runs N world updates, so it is lockstep too.
This port's protocol is its own and does not talk to the original. The app
has no multiplayer lobby yet: MULTI PLAYER WAR still shows the original
`netopte` choices without a session behind them.
