using System.Text.Json;
using AVWorkstationToolkit.IntegrationTests;
using AVWorkstationToolkit.Domain.Parity;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: AVWorkstationToolkit.IntegrationTests [--core] <fixture.json>");
    return 2;
}

var core = args.Length == 2 && args[0] == "--core";
var path = Path.GetFullPath(args[^1]);
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
object result = core
    ? CoreParityEvaluator.Evaluate(JsonSerializer.Deserialize<CoreParityFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("Core parity fixture is empty."))
    : PackageStateEvaluator.Evaluate(JsonSerializer.Deserialize<ParityFixture>(File.ReadAllText(path), options) ?? throw new InvalidDataException("Planning parity fixture is empty."));
Console.WriteLine(JsonSerializer.Serialize(result));
return 0;
