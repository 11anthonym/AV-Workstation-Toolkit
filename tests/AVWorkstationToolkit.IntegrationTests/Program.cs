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

if (args.Length == 2 && args[0] == "--managed-app-host")
{
    Console.WriteLine(JsonSerializer.Serialize(await ManagedCatalogBinaryUpdateBoundary.RunAppHostAsync(args[1])));
    return 0;
}

if (args.Length == 3 && args[0] == "--managed-catalog-binary-update")
{
    Console.WriteLine(JsonSerializer.Serialize(await ManagedCatalogBinaryUpdateBoundary.RunAsync(args[1], args[2])));
    return 0;
}

Console.Error.WriteLine(
    "Usage: AVWorkstationToolkit.IntegrationTests --live-readonly | --worker-process <worker.exe> " +
    "| --compiled-action-flow <repository-root> | --live-rehearsal <repository-root> " +
    "| --managed-app-host <fixture.json> | --managed-catalog-binary-update <repository-root> <worker-devhost.exe>");
return 2;
