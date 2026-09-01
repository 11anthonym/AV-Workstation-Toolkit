using System.Security.Principal;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Infrastructure.Windows.Files;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

var production = args.Length == 7 && args[0] == "--production" && args[1] == "--root" &&
    args[3] == "--request" && args[5] == "--application-root";
if (!production)
{
    Console.Error.WriteLine("The compiled worker accepts only the exact production invocation.");
    return 2;
}

try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        throw new InvalidOperationException("The compiled worker must run as a standard user.");

    var dataRoot = ProductionRuntimePolicy.RequireDataRoot(args[2]);
    var requestPath = Path.GetFullPath(args[4]);
    var requestName = Path.GetFileNameWithoutExtension(requestPath);
    var requestPolicy = new ActionRequestFilePolicy();
    requestPolicy.ValidateExistingRequestPath(dataRoot, requestPath);

    var store = new ActionProtocolStore(dataRoot);
    var request = await store.ReadRequestAsync(requestName);
    await using var protocol = new ActionWorkerFileProtocol(dataRoot, request.RequestId);
    var applicationRoot = ProductionRuntimePolicy.RequireApplicationRoot(dataRoot, args[6]);
    var services = ProductionWorkerComposition.Create(applicationRoot);
    var orchestrator = new ActionWorkerOrchestrator(services.Plans, services.Executor, protocol, Environment.MachineName);
    var result = await orchestrator.RunAsync(request);
    return result.ExitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
