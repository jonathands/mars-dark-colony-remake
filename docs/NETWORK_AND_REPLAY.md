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
This port's protocol is its own and does not talk to the original.

## Multi Player War in the app

MULTI PLAYER WAR opens `netopte`. Only TCP/IP is offered; IPX, modem and serial report that they are unsupported.

**Hosting.** ACT AS SERVER listens on port 47624, DirectPlay's TCP/IP port (`--net-port` overrides), and opens the `multie` lobby.

**Joining.** CONNECT TO SERVER opens `getsvre`, where the player types an address, optionally as `host:port`, then CONNECT.
- The client sends `LobbyJoin`.
- The host seats it on the first row no human owns and broadcasts `LobbyUpdate`.
- The client recognises its own player number by its nonce.

**In the lobby.**
- The host keeps the map list, the options and the computer rows (Type cycles Computer, Computer+ and None).
- Each player toggles its own race.
- A player who disconnects frees its row.

**Starting.** The host's READY sends `LobbyStart`. Every peer then runs the native War session start (reverse-engineering/war-session.md) on the same rows, with its own row as the local player, and plays in lockstep.
- Each clock step queues the player's orders into the session, then runs every update that is due and complete, catching up at most 8 per step.

**During the game.**
- The game cannot pause or be saved.
- The options popup does not change its speed.
- `--outcome-after` is ignored.
- A desync or a lost peer stops the game with a status line.
- Leaving gameplay closes the session.

**Live run, 2026-10-04 (`tools/Run-NetworkPair.ps1`).** Two app instances on one machine played `j4play01`:
- Both seated the host on team 4 and the client on team 2.
- The client moved its lieutenant.
- At update 300 both logged digest `4ad486a8c2ca8ea0` with 18 exchanges confirmed, and 26 by the end.
- Closing the host showed "Connection lost" on the client.

The check *a network War lobby seats a joining player and both peers start the same session in lockstep* covers the same protocol over loopback.
