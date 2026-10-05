if (DeterminismCli.TryRun(args, out var determinismExitCode)) return determinismExitCode;

// --data <installation> names the original game; the default is ..\Dark Colony.
var dataIndex = Array.IndexOf(args, "--data");
var suite = new CheckSuite(Path.GetFullPath(dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : Path.Combine("..", "Dark Colony")));
WorldChecks.Register(suite);
DeterminismChecks.Register(suite);
AssetsChecks.Register(suite);
MovementChecks.Register(suite);
AcquisitionChecks.Register(suite);
CombatChecks.Register(suite);
SpecialsChecks.Register(suite);
VisionChecks.Register(suite);
EconomyChecks.Register(suite);
MissionsChecks.Register(suite);
ComputerPlayerChecks.Register(suite);
WarChecks.Register(suite);
ReplayAndNetworkChecks.Register(suite);
InterfaceChecks.Register(suite);
PresentationChecks.Register(suite);
return suite.Run(CheckOptions.Parse(args));
