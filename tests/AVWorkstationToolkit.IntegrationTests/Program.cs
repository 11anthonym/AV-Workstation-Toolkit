using System.Text.Json;
using AVWorkstationToolkit.IntegrationTests;
using AVWorkstationToolkit.Domain.Parity;

if (args.Length == 1 && args[0] == "--live-readonly")
{
    Console.WriteLine(JsonSerializer.Serialize(await ProviderLiveChecks.RunAsync()));
    return 0;
}

if (args.Length == 2 && args[0] == "--worker-process")
{
    Console.WriteLine(JsonSerializer.Serialize(await WorkerProcessBoundary.RunAsync(args[1])));
    return 0;
}

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: AVWorkstationToolkit.IntegrationTests [--core|--providers|--presentation|--read-only-surfaces|--action-requests|--ipc-lifecycle] <fixture.json> | --live-readonly | --worker-process <worker.exe>");
    return 2;
}

var core = args.Length == 2 && args[0] == "--core";
var providers = args.Length == 2 && args[0] == "--providers";
var presentation = args.Length == 2 && args[0] == "--presentation";
var readOnlySurfaces = args.Length == 2 && args[0] == "--read-only-surfaces";
var actionRequests = args.Length == 2 && args[0] == "--action-requests";
var ipcLifecycle = args.Length == 2 && args[0] == "--ipc-lifecycle";
var path = Path.GetFullPath(args[^1]);
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
object result = ipcLifecycle
    ? await IpcLifecycleParityEvaluator.EvaluateAsync(JsonSerializer.Deserialize<IpcLifecycleFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("IPC lifecycle parity fixture is empty."))
    : actionRequests
    ? ActionRequestParityEvaluator.Evaluate(JsonSerializer.Deserialize<ActionRequestFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("Action-request parity fixture is empty."))
    : readOnlySurfaces
    ? await ReadOnlySurfacesParityEvaluator.EvaluateAsync(JsonSerializer.Deserialize<ReadOnlySurfacesFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("Read-only surface parity fixture is empty."))
    : core
    ? CoreParityEvaluator.Evaluate(JsonSerializer.Deserialize<CoreParityFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("Core parity fixture is empty."))
    : providers
        ? ProviderParityEvaluator.Evaluate(JsonSerializer.Deserialize<ProviderParityFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("Provider parity fixture is empty."))
    : presentation
        ? await PresentationParityEvaluator.EvaluateAsync(JsonSerializer.Deserialize<PresentationParityFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("Presentation parity fixture is empty."))
    : PackageStateEvaluator.Evaluate(JsonSerializer.Deserialize<ParityFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("Planning parity fixture is empty."));
Console.WriteLine(JsonSerializer.Serialize(result));
return 0;
