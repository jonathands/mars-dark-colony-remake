using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Missions;
using DarkColony.Engine.Movement;
using DarkColony.Engine.World;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Commands;
using DarkColony.Engine.Interface;
using DarkColony.Engine.Audio;
using DarkColony.Engine.Video;
using DarkColony.Engine.Network;
using System.Buffers.Binary;
using static CheckHelpers;

/// <summary>Saved games, replays, and lockstep networking.</summary>
internal static class ReplayAndNetworkChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("ReplayAndNetwork", name, tags, action);

        Check("a saved game replays its command journal to the same state", () =>
        {
            // Scripted orders on human05, whose Krusty enemy acts too, saved at
            // 900 ticks and restored on a fresh simulation through the JSON format.
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var (simulation, path) = DeterminismHarness.Load(install, rules, "human/human05");
            var commander = new ScriptedCommander(simulation, path, rules, DeterminismHarness.StableSeed("human/human05"));
            var journal = new CommandJournal();
            for (ulong tick = 1; tick <= 900; tick++) journal.Step(simulation, commander.CommandsFor(tick));
            var json = journal.Save(simulation, "human", "human05", campaign: new SavedCampaign(false, false, 5)).ToJson();
            var saved = SavedGame.FromJson(json);
            Equal((900UL, 5, true), (saved.Ticks, saved.Campaign!.Mission, saved.Steps.Count > 10));
            var (restored, _) = DeterminismHarness.Load(install, rules, "human/human05");
            Equal(true, saved.Replay(restored));
            Equal(SimulationDigest.Describe(simulation), SimulationDigest.Describe(restored));
            // A journal missing a step does not reproduce the saved state.
            var (tampered, _) = DeterminismHarness.Load(install, rules, "human/human05");
            Equal(false, (saved with { Steps = saved.Steps.Skip(1).ToArray() }).Replay(tampered));
        }, CheckTags.Data);

        Check("a recorded War match replays bit for bit and points at a tampered step", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            const string scenario = "mplayer/d2play01";
            var (recorded, recordedPath) = DeterminismHarness.Load(install, rules, scenario);
            var commander = new ScriptedCommander(recorded, recordedPath, rules, DeterminismHarness.StableSeed(scenario));
            var journal = new CommandJournal(checkpointInterval: 50);
            for (var tick = 1UL; tick <= 400; tick++) journal.Step(recorded, commander.CommandsFor(tick));
            var saved = SavedGame.FromJson(journal.Save(recorded, "mplayer", "d2play01").ToJson());
            Equal(8, saved.Checkpoints?.Count ?? 0);
            Equal(true, saved.Steps.Count > 20);

            var check = saved.VerifyReplay(DeterminismHarness.Load(install, rules, scenario).Simulation);
            Equal(new ReplayCheck(true, null, 8), check);

            // Dropping one step's commands is caught at the next checkpoint.
            var dropped = saved.Steps.First(step => step.Tick > 120);
            var tampered = saved with { Steps = [.. saved.Steps.Where(step => step != dropped)] };
            var tamperedCheck = tampered.VerifyReplay(DeterminismHarness.Load(install, rules, scenario).Simulation);
            Equal(false, tamperedCheck.Matches);
            Equal((dropped.Tick + 49) / 50 * 50, tamperedCheck.FirstMismatchTick ?? 0);
        }, CheckTags.Data);

        Check("two lockstep peers over TCP stay in sync and a desync is caught", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            const string scenario = "mplayer/d2play01";
            int CommandTeam(ScenarioSimulation simulation, WorldCommand command) => command switch
            {
                PurchaseIntent purchase => purchase.TeamId,
                AllianceIntent alliance => alliance.Player,
                _ when command.GetType().GetProperty("EntityInstanceId")?.GetValue(command) is int id => simulation.Actor(id)?.Seed.Team ?? -1,
                _ when command.GetType().GetProperty("TeamId")?.GetValue(command) is int team => team,
                _ => -1,
            };

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var hosting = Task.Run(() => TcpLockstepTransport.Host(listener, clients: 1, TimeSpan.FromSeconds(10)));
            using var joined = TcpLockstepTransport.Join(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), TimeSpan.FromSeconds(10));
            using var hosted = hosting.Result;
            listener.Stop();

            var peers = new[] { (Transport: (ILockstepTransport)hosted, Player: 0), (Transport: joined, Player: 1) }.Select(peer =>
            {
                var (simulation, path) = DeterminismHarness.Load(install, rules, scenario);
                return (Simulation: simulation, Session: new LockstepSession(peer.Player, 2, peer.Transport),
                    Commander: new ScriptedCommander(simulation, path, rules, DeterminismHarness.StableSeed(scenario)), peer.Player);
            }).ToArray();

            const ulong ticks = 600;
            var deadline = DateTime.UtcNow.AddSeconds(120);
            var queued = 0;
            while (peers.Any(peer => peer.Simulation.TickCount < ticks))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Lockstep stalled at {string.Join('/', peers.Select(peer => peer.Simulation.TickCount))}.");
                var advanced = false;
                foreach (var peer in peers)
                {
                    if (peer.Simulation.TickCount >= ticks) continue;
                    foreach (var scheduled in peer.Commander.CommandsFor(peer.Simulation.TickCount + 1))
                    {
                        if (CommandTeam(peer.Simulation, scheduled.Command) != peer.Player) continue;
                        peer.Session.Queue(scheduled.Command);
                        queued++;
                    }
                    advanced |= peer.Session.TryAdvance(peer.Simulation) is not null;
                }
                if (!advanced) Thread.Sleep(1);
            }
            Equal(SimulationDigest.Hash(peers[0].Simulation), SimulationDigest.Hash(peers[1].Simulation));
            Equal(true, queued > 50);
            Equal(true, peers.All(peer => peer.Session.Desync is null && peer.Session.ConfirmedDigests >= (int)(ticks / 16) - 2));

            // A peer whose state changes outside the lockstep is caught at the next digest.
            var network = new LoopbackLockstepNetwork();
            var a = (Simulation: DeterminismHarness.Load(install, rules, scenario).Simulation, Session: new LockstepSession(0, 2, network.Connect()));
            var b = (Simulation: DeterminismHarness.Load(install, rules, scenario).Simulation, Session: new LockstepSession(1, 2, network.Connect()));
            while (a.Session.Desync is null && b.Session.Desync is null && a.Simulation.TickCount < 100)
            {
                a.Session.TryAdvance(a.Simulation);
                b.Session.TryAdvance(b.Simulation);
                if (b.Simulation.TickCount == 40 && b.Simulation.Outcome is null) b.Simulation.EndMission(0, 1);
            }
            var desync = a.Session.Desync ?? b.Session.Desync ?? throw new InvalidOperationException("The desync was not detected.");
            Equal(48UL, desync.Tick);
        }, CheckTags.Data | CheckTags.Serial);

        Check("a network War lobby seats a joining player and both peers start the same session in lockstep", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var catalog = SinglePlayerWarCatalog.Load(install);
            var network = new LoopbackLockstepNetwork();
            var hostLink = network.Connect();
            var clientLink = network.Connect();
            var lobby = new NetworkWarLobby("d2play01", "Host", 0, SinglePlayerWarSettings.Default);

            clientLink.Send(new LobbyJoin(-1, "nonce-b", "Guest", 1));
            Equal(true, hostLink.TryReceive(out var joinMessage));
            var member = lobby.Join((LobbyJoin)joinMessage) ?? throw new InvalidOperationException("No row for the guest.");
            Equal(1, member.Player);
            Equal(new WarLobbyRow(WarSeatKind.Human, 1, 1), lobby.Rows[1]);
            lobby.SetRace(1, 0);
            hostLink.Send(new LobbyStart(0, lobby.State));
            Equal(true, clientLink.TryReceive(out var startMessage));
            var started = ((LobbyStart)startMessage).State;
            Equal(1, started.Members.Single(candidate => candidate.Nonce == "nonce-b").Player);

            Equal(true, NetworkWarLobby.TryCreateLaunch(lobby.State, 0, catalog, rules.RandomTable, out var hostLaunch));
            Equal(true, NetworkWarLobby.TryCreateLaunch(started, 1, catalog, rules.RandomTable, out var clientLaunch));
            Equal(0, hostLaunch.LocalTeamId);
            Equal(1, clientLaunch.LocalTeamId);

            ScenarioSimulation Build(SinglePlayerWarLaunch launch)
            {
                var file = install.DataFile("scenario", "mplayer", "d2play01.scn");
                var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
                return ScenarioSimulation.Create(launch.ApplyTo(ScenarioDefinition.Load(file)),
                    PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height), rules, MissionScript.LoadForScenario(file), map);
            }
            var hostSimulation = Build(hostLaunch);
            var clientSimulation = Build(clientLaunch);
            Equal(SimulationDigest.Hash(hostSimulation), SimulationDigest.Hash(clientSimulation));
            Equal(0, hostSimulation.AiProfile(1));

            var hostSession = new LockstepSession(0, [0, 1], hostLink);
            var clientSession = new LockstepSession(1, [0, 1], clientLink);
            var hostUnit = hostSimulation.Actors.First(actor => actor.Seed.Team == 0 && actor.Definition.MovementSpeed > 0);
            var clientUnit = clientSimulation.Actors.First(actor => actor.Seed.Team == 1 && actor.Definition.MovementSpeed > 0);
            hostSession.Queue(new MoveIntent(hostUnit.Seed.InstanceId, new CellCoordinate(hostUnit.Movement.OccupiedCell.X + 3, hostUnit.Movement.OccupiedCell.Z)));
            clientSession.Queue(new MoveIntent(clientUnit.Seed.InstanceId, new CellCoordinate(clientUnit.Movement.OccupiedCell.X - 3, clientUnit.Movement.OccupiedCell.Z)));
            for (var round = 0; round < 400 && (hostSimulation.TickCount < 160 || clientSimulation.TickCount < 160); round++)
            {
                if (hostSimulation.TickCount < 160) hostSession.TryAdvance(hostSimulation);
                if (clientSimulation.TickCount < 160) clientSession.TryAdvance(clientSimulation);
            }
            Equal(160UL, hostSimulation.TickCount);
            Equal(160UL, clientSimulation.TickCount);
            Equal(SimulationDigest.Hash(hostSimulation), SimulationDigest.Hash(clientSimulation));
            Equal(true, hostSession.ConfirmedDigests >= 9 && hostSession.Desync is null);
            Equal(false, hostSimulation.Actor(clientUnit.Seed.InstanceId)!.Movement.OccupiedCell == clientUnit.Seed.Position.Cell);
        }, CheckTags.Data | CheckTags.Serial);
    }
}
