using System.Text.Json;
using AVWorkstationToolkit.IntegrationTests;

// Boundary harnesses for the production compiled runtime. Each mode exercises a real process or
// provider boundary that an in-process MSTest case cannot reach; deterministic behaviour is covered
// by the AVWorkstationToolkit.Tests suite instead.
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

if (args.Length == 2 && args[0] == "--compiled-action-flow")
{
    Console.WriteLine(JsonSerializer.Serialize(await CompiledActionFlowBoundary.RunAsync(args[1])));
    return 0;
}

if (args.Length == 2 && args[0] == "--live-rehearsal")
{
    Console.WriteLine(JsonSerializer.Serialize(await LiveRehearsalBoundary.RunAsync(args[1])));
    return 0;
}

Console.Error.WriteLine(
    "Usage: AVWorkstationToolkit.IntegrationTests --live-readonly | --worker-process <worker.exe> " +
    "| --compiled-action-flow <repository-root> | --live-rehearsal <repository-root>");
return 2;
