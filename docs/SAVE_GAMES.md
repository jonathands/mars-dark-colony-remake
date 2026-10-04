# Saved games

The port saves a game as its inputs, not as a memory image:

- the scenario (directory and name);
- the War launch (race, local team, lobby settings) or the campaign position
  (race, training, mission number);
- every command the simulation consumed, by step (`CommandJournal`);
- the tick count, and the state digest at that tick.

Loading recreates the scenario and replays the steps to the saved tick. The
simulation is deterministic, so the replayed state's digest equals the saved
one. A mismatch means the game data or the engine changed since the save, and
the port reports it. Files are JSON (`SavedGame.ToJson`), with the extension
`.dcsave`, in `%LOCALAPPDATA%\DarkColonyPort\saves`.

Every simulation input goes through `ScenarioSimulation.Step`, so the journal
is complete. The Allies panel used to set alliance bits directly. It now
sends `AllianceIntent`, the command form of the native Allies packet
(`0x41D7B3`), which takes effect at the next update's relation refresh like
any other command.

In the app:

- The HUD's SAVE GAME button (options tab) writes a save.
- LOAD GAME lists the newest saves. Click one, then LOAD.

The camera and selection are not saved.

The check "a saved game replays its command journal to the same state" runs
900 ticks of scripted orders on human05, saves, reloads through JSON on a
fresh simulation, and compares the full state description. It also checks
that a journal with a step removed does not match.

## The original's saves

The executable's loader (`0x41A978`) restores raw runtime blocks: actors, the
players' records, the trigger table and the Krusty states (the entity records
are restored by `0x43C784`, see `combat-damage.md`). Reading those files would
mean mapping native memory layouts onto the port's object model one field at a
time. The port does not read them, and the LOAD GAME screen lists only the
port's own saves.
