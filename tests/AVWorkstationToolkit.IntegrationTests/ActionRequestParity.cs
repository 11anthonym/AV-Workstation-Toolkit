using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.IntegrationTests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionRequestFixture(int SchemaVersion, string ScenarioId, IReadOnlyList<ActionRequestCase> Cases);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionRequestCase(
    string CaseId,
    string Kind,
    string Action,
    IReadOnlyList<string> PackageIds,
    bool RiskAcknowledged,
    bool DryRun,
    string Mutation,
    string PlanPackageId,
    string PlanAuthority,
    string PlanProvider,
    string PlanStatus,
    string PlanAction,
    string PlanRisk,
    bool RebootPending);

public static class ActionRequestParityEvaluator
{
    private const string RequestId = "request-20260829-142233-0123abcd";

    public static object Evaluate(ActionRequestFixture fixture)
    {
        if (fixture.SchemaVersion != 1) throw new InvalidDataException($"Unsupported action-request fixture schema: {fixture.SchemaVersion}.");
        return new
        {
            SchemaVersion = 1,
            fixture.ScenarioId,
            Cases = fixture.Cases.Select(EvaluateCase).ToArray()
        };
    }

    private static object EvaluateCase(ActionRequestCase input)
    {
        try
        {
            ActionRequest request;
            if (input.Kind == "Creation")
            {
                if (!Enum.TryParse<ManagedRequestAction>(input.Action, false, out var action))
                    throw new ActionRequestValidationException(ActionRequestFailure.UnsupportedAction, "Unsupported action.");
                request = new ActionRequestFactory().Create(action, input.PackageIds, input.RiskAcknowledged, input.DryRun);
            }
            else
            {
                request = new ActionRequestCodec().Parse(BuildPayload(input));
            }

            var authorizedIds = Array.Empty<string>();
            if (input.Kind == "Authorization")
            {
                authorizedIds = new ActionRequestAuthorizationService().Authorize(request, BuildPlan(input))
                    .Packages.Select(item => item.Package.Id).ToArray();
            }
            return Canonical(input.CaseId, true, request, authorizedIds);
        }
        catch (ActionRequestValidationException)
        {
            return Canonical(input.CaseId, false, null, []);
        }
    }

    private static byte[] BuildPayload(ActionRequestCase input)
    {
        if (input.Mutation == "MalformedJson") return "{"u8.ToArray();
        if (input.Mutation == "Oversized") return Encoding.UTF8.GetBytes(new string(' ', ActionRequestRules.MaximumPayloadBytes + 1));

        var action = Enum.TryParse<ManagedRequestAction>(input.Action, false, out var parsedAction) ? parsedAction : ManagedRequestAction.Install;
        var request = new ActionRequest(1, RequestId, action, input.PackageIds, input.RiskAcknowledged, input.DryRun);
        var node = JsonNode.Parse(new ActionRequestCodec().Serialize(request))?.AsObject()
            ?? throw new InvalidDataException("The deterministic request payload could not be created.");
        switch (input.Mutation)
        {
            case "None": break;
            case "MissingDryRun": node.Remove("DryRun"); break;
            case "ExtraField": node["Extra"] = true; break;
            case "UnsupportedSchema": node["SchemaVersion"] = 2; break;
            case "WrongRiskType": node["RiskAcknowledged"] = "true"; break;
            case "WrongPackageType": node["PackageIds"] = new JsonArray(123); break;
            case "NullPackageId": node["PackageIds"] = new JsonArray((JsonNode?)null); break;
            case "MalformedRequestId": node["RequestId"] = "request-invalid"; break;
            case "UnsupportedAction": node["Action"] = "Uninstall"; break;
            case "MalformedPackageId": node["PackageIds"] = new JsonArray("bad id"); break;
            case "EmptyIds": node["PackageIds"] = new JsonArray(); break;
            default: throw new InvalidDataException($"Unknown action-request mutation: {input.Mutation}.");
        }
        return Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static WorkstationPlan BuildPlan(ActionRequestCase input)
    {
        var authority = Enum.Parse<CatalogAuthority>(input.PlanAuthority, false);
        var provider = Enum.Parse<ProviderKind>(input.PlanProvider, false);
        var status = Enum.Parse<PackageStatus>(input.PlanStatus, false);
        var action = Enum.Parse<PackageAction>(input.PlanAction, false);
        var risk = Enum.Parse<PackageRisk>(input.PlanRisk, false);
        var package = new PackageDefinition(
            input.PlanPackageId, input.PlanPackageId, "Fixture", string.Empty, "Fixture", provider, authority,
            PackageProfile.Standard, PackagePriority.P2, risk,
            authority == CatalogAuthority.ManagedWinGet ? DeploymentPolicy.Allowlisted : DeploymentPolicy.ManualHold,
            authority == CatalogAuthority.ManagedWinGet ? MaintenancePolicy.Allowlisted : MaintenancePolicy.Hold,
            authority == CatalogAuthority.ManagedWinGet ? DeploymentClass.Managed : DeploymentClass.InventoryOnly,
            authority == CatalogAuthority.ManagedWinGet ? CatalogMaintenancePolicy.Managed : CatalogMaintenancePolicy.Manual,
            VersionRule.Latest, VersionCouplingMode.Independent, string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free],
            ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly, [InstallationForm.WinGet], [SupportedOperatingSystem.Windows],
            DeliveryMode.None, ReleaseMode.None, provider == ProviderKind.WinGet ? DetectionMode.WinGet : DetectionMode.Registry,
            DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null,
            risk == PackageRisk.Driver, risk == PackageRisk.Service, risk == PackageRisk.Listener, null, string.Empty, []);
        var state = new PackageState(package, false, string.Empty, [], string.Empty, false, status, status.ToString(), status.ToString(), action, InventoryQuality.Complete);
        return new WorkstationPlan(
            [state],
            new WorkstationPlanSummary(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            input.RebootPending ? new RebootState(true, [RebootReason.WindowsUpdate], "Windows Update reboot pending") : RebootState.Clear,
            new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []));
    }

    private static object Canonical(string caseId, bool accepted, ActionRequest? request, IReadOnlyList<string> authorizedIds) => new
    {
        CaseId = caseId,
        Accepted = accepted,
        Action = request?.Action.ToString() ?? string.Empty,
        PackageIds = request?.PackageIds ?? [],
        RiskAcknowledged = request?.RiskAcknowledged ?? false,
        DryRun = request?.DryRun ?? false,
        AuthorizedIds = authorizedIds
    };
}
