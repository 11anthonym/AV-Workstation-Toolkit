using System.Security.Principal;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Infrastructure.Windows.Files;
using AVWorkstationToolkit.Worker;

var testMode = args.Length == 5 && args[0] == "--test-mode" && args[1] == "--root" && args[3] == "--request";
var liveRehearsal = args.Length == 7 && args[0] == "--live-rehearsal" && args[1] == "--root" &&
    args[3] == "--request" && args[5] == "--repository-root";
if (!testMode && !liveRehearsal)
{
    Console.Error.WriteLine("This non-shipping worker accepts only an exact test or live-rehearsal invocation.");
    return 2;
}

try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        throw new InvalidOperationException("The non-shipping compiled worker must run as a standard user.");

    var dataRoot = testMode ? Path.GetFullPath(args[2]) : LiveRehearsalRootPolicy.RequireExisting(args[2]);
    var requestPath = Path.GetFullPath(args[4]);
    var requestName = Path.GetFileNameWithoutExtension(requestPath);
    var requestPolicy = new ActionRequestFilePolicy();
    requestPolicy.ValidateExistingRequestPath(dataRoot, requestPath);

    var store = new ActionProtocolStore(dataRoot);
    var request = await store.ReadRequestAsync(requestName);
    await using var protocol = new ActionWorkerFileProtocol(dataRoot, request.RequestId);
    ActionWorkerOrchestrator orchestrator;
    if (testMode)
    {
        var fixture = WorkerFixtureLoader.Load(dataRoot);
        orchestrator = new ActionWorkerOrchestrator(
            new FixtureWorkerPlanProvider(fixture.Plans),
            new DeterministicFakePackageExecutor(fixture.Executions),
            protocol,
            fixture.Computer);
    }
    else
    {
        var services = LiveRehearsalWorkerComposition.Create(Path.GetFullPath(args[6]));
        orchestrator = new ActionWorkerOrchestrator(services.Plans, services.Executor, protocol, Environment.MachineName);
    }
    var result = await orchestrator.RunAsync(request);
    return result.ExitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
