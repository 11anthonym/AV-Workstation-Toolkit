using System.Text.Json;
using AVWorkstationToolkit.IntegrationTests;
using AVWorkstationToolkit.Domain.Parity;

if (args.Length == 1 && args[0] == "--live-readonly")
{
    Console.WriteLine(JsonSerializer.Serialize(await ProviderLiveChecks.RunAsync()));
    return 0;
}

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: AVWorkstationToolkit.IntegrationTests [--core|--providers|--presentation|--read-only-surfaces] <fixture.json> | --live-readonly");
    return 2;
}

var core = args.Length == 2 && args[0] == "--core";
var providers = args.Length == 2 && args[0] == "--providers";
var presentation = args.Length == 2 && args[0] == "--presentation";
var readOnlySurfaces = args.Length == 2 && args[0] == "--read-only-surfaces";
var path = Path.GetFullPath(args[^1]);
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
object result = readOnlySurfaces
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
