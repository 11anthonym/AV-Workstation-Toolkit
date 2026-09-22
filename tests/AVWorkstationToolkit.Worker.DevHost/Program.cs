using System.Security.Principal;
using AVWorkstationToolkit.Development;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Infrastructure.Windows.Files;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Worker.DevHost;

var testMode = args.Length == 5 && args[0] == "--test-mode" && args[1] == "--root" && args[3] == "--request";
var liveRehearsal = args.Length == 7 && args[0] == "--live-rehearsal" && args[1] == "--root" &&
    args[3] == "--request" && args[5] == "--repository-root";
var managedCatalogTest = args.Length == 5 && args[0] == "--managed-catalog-test" && args[1] == "--fixture" && args[3] == "--request";
if (!testMode && !liveRehearsal && !managedCatalogTest)
{
    Console.Error.WriteLine("The worker development host accepts only an exact test, managed-catalog test, or live-rehearsal invocation.");
    return 2;
}

try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        throw new InvalidOperationException("The worker development host must run as a standard user.");

    var managedOptions = managedCatalogTest ? ManagedCatalogWorkerOptions.Load(args[2]) : null;
    var dataRoot = testMode ? RequireFixtureRoot(args[2]) : managedCatalogTest
        ? managedOptions!.DataRoot
        : LiveRehearsalRootPolicy.RequireExisting(args[2]);
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
    else if (managedCatalogTest)
    {
        var services = ManagedCatalogWorkerComposition.Create(managedOptions!, request);
        orchestrator = new ActionWorkerOrchestrator(
            services.Plans,
            services.Executor,
            protocol,
            "ManagedCatalogDevWorker");
    }
    else
    {
        var repositoryRoot = RequireRepositoryRoot(args[6]);
        var services = ProductionWorkerComposition.CreateSourceCheckout(repositoryRoot);
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

static string RequireFixtureRoot(string value)
{
    var root = RequireAbsoluteNonRoot(value, "worker fixture root");
    if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        throw new DirectoryNotFoundException("The worker fixture root is missing or unsafe.");
    return root;
}

static string RequireRepositoryRoot(string value)
{
    var root = RequireAbsoluteNonRoot(value, "repository root");
    if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "VERSION")) ||
        !File.Exists(Path.Combine(root, "manifests", "managed-applications.json")) ||
        (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        throw new DirectoryNotFoundException("The live rehearsal repository root is missing or unsafe.");
    return root;
}

static string RequireAbsoluteNonRoot(string value, string label)
{
    if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        throw new IOException($"The {label} must be an absolute path.");
    var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var pathRoot = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (string.IsNullOrWhiteSpace(full) || string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase))
        throw new IOException($"The {label} cannot be a filesystem root.");
    return full;
}
