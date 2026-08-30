using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.IntegrationTests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionParityFixture(int SchemaVersion, string ScenarioId, IReadOnlyList<ExecutionParityCase> Cases);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionParityCase(string CaseId, string Action, string PackageId, string Risk);

public static class ExecutionParityEvaluator
{
    public static object Evaluate(ExecutionParityFixture fixture)
    {
        if (fixture.SchemaVersion != 1) throw new InvalidDataException($"Unsupported execution parity schema: {fixture.SchemaVersion}.");
        return new
        {
            SchemaVersion = 1,
            fixture.ScenarioId,
            Cases = fixture.Cases.Select(item =>
            {
                var action = Enum.Parse<ManagedRequestAction>(item.Action, false);
                var risk = Enum.Parse<PackageRisk>(item.Risk, false);
                var arguments = ManagedWinGetArgumentPolicy.Create(new(item.PackageId, item.PackageId, action, risk));
                return new { item.CaseId, Arguments = arguments };
            }).ToArray()
        };
    }
}
