using System.Security.Principal;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Infrastructure.Windows.Files;
using AVWorkstationToolkit.Worker;

if (args.Length != 5 || args[0] != "--test-mode" || args[1] != "--root" || args[3] != "--request")
{
    Console.Error.WriteLine("This non-shipping worker accepts only: --test-mode --root <isolated-root> --request <canonical-request-path>");
    return 2;
}

try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        throw new InvalidOperationException("The non-shipping compiled worker must run as a standard user.");

    var dataRoot = Path.GetFullPath(args[2]);
    var requestPath = Path.GetFullPath(args[4]);
    var requestName = Path.GetFileNameWithoutExtension(requestPath);
    var requestPolicy = new ActionRequestFilePolicy();
    requestPolicy.ValidateExistingRequestPath(dataRoot, requestPath);

    var store = new ActionProtocolStore(dataRoot);
    var request = await store.ReadRequestAsync(requestName);
    var fixture = WorkerFixtureLoader.Load(dataRoot);
    await using var protocol = new ActionWorkerFileProtocol(dataRoot, request.RequestId);
    var orchestrator = new ActionWorkerOrchestrator(
        new FixtureWorkerPlanProvider(fixture.Plans),
        new DeterministicFakePackageExecutor(fixture.Executions),
        protocol,
        fixture.Computer);
    var result = await orchestrator.RunAsync(request);
    return result.ExitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
