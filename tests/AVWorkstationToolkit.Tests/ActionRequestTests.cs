using System.Text;
using System.Text.Json;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ActionRequestTests
{
    private const string RequestId = "request-20260829-142233-0123abcd";

    [TestMethod]
    public void FactoryCreatesOnlyTheApprovedSchemaAndPreservesPackageOrder()
    {
        var factory = new ActionRequestFactory(new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 14, 22, 33, TimeSpan.Zero)), () => "0123abcd");
        var request = factory.Create(ManagedRequestAction.Update, ["Vendor.Second", "Vendor.First", "Vendor.Second"], true, false);
        var payload = new ActionRequestCodec().Serialize(request);

        Assert.AreEqual(RequestId, request.RequestId);
        CollectionAssert.AreEqual(new[] { "Vendor.Second", "Vendor.First", "Vendor.Second" }, request.PackageIds.ToArray());
        using var document = JsonDocument.Parse(payload);
        CollectionAssert.AreEqual(ActionRequestRules.Properties.ToArray(), document.RootElement.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.AreEqual(JsonValueKind.Number, document.RootElement.GetProperty("SchemaVersion").ValueKind);
        Assert.AreEqual(JsonValueKind.String, document.RootElement.GetProperty("Action").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, document.RootElement.GetProperty("PackageIds").ValueKind);
        Assert.AreEqual(JsonValueKind.True, document.RootElement.GetProperty("RiskAcknowledged").ValueKind);
        Assert.AreEqual(JsonValueKind.False, document.RootElement.GetProperty("DryRun").ValueKind);
        Assert.AreEqual(0L, document.RootElement.GetProperty("ManagedCatalogRevision").GetInt64());

        var original = new[] { "Vendor.One" };
        var immutableRequest = factory.Create(ManagedRequestAction.Install, original, false, false);
        original[0] = "bad id";
        Assert.AreEqual("Vendor.One", immutableRequest.PackageIds[0]);
    }

    [TestMethod]
    public void CodecRoundTripsBothApprovedActionsAndBooleanStates()
    {
        var codec = new ActionRequestCodec();
        foreach (var request in new[]
        {
            Request(ManagedRequestAction.Install, ["Vendor.One"], false, false),
            Request(ManagedRequestAction.Update, ["Vendor.One", "Vendor.Two"], true, true)
        })
        {
            var parsed = codec.Parse(codec.Serialize(request), RequestId);
            Assert.AreEqual(request.Action, parsed.Action);
            Assert.AreEqual(request.RiskAcknowledged, parsed.RiskAcknowledged);
            Assert.AreEqual(request.DryRun, parsed.DryRun);
            CollectionAssert.AreEqual(request.PackageIds.ToArray(), parsed.PackageIds.ToArray());
        }
    }

    [TestMethod]
    public void CodecRejectsMalformedMissingUnknownDuplicateAndWrongTypedFields()
    {
        var cases = new (string Json, ActionRequestFailure Failure)[]
        {
            ("{", ActionRequestFailure.MalformedJson),
            (ValidJson().Replace(",\"DryRun\":false", string.Empty, StringComparison.Ordinal), ActionRequestFailure.MissingField),
            (ValidJson().Replace("}", ",\"Extra\":true}", StringComparison.Ordinal), ActionRequestFailure.UnknownField),
            (ValidJson().Replace("\"DryRun\":false", "\"DryRun\":false,\"DryRun\":true", StringComparison.Ordinal), ActionRequestFailure.DuplicateField),
            (ValidJson().Replace("\"SchemaVersion\":2", "\"SchemaVersion\":\"2\"", StringComparison.Ordinal), ActionRequestFailure.WrongType),
            (ValidJson().Replace("\"RiskAcknowledged\":false", "\"RiskAcknowledged\":\"true\"", StringComparison.Ordinal), ActionRequestFailure.WrongType),
            (ValidJson().Replace("\"DryRun\":false", "\"DryRun\":1", StringComparison.Ordinal), ActionRequestFailure.WrongType),
            (ValidJson().Replace("\"ManagedCatalogRevision\":0", "\"ManagedCatalogRevision\":[]", StringComparison.Ordinal), ActionRequestFailure.WrongType),
            (ValidJson().Replace("\"Vendor.One\"", "123", StringComparison.Ordinal), ActionRequestFailure.WrongType),
            (ValidJson().Replace("\"Vendor.One\"", "null", StringComparison.Ordinal), ActionRequestFailure.WrongType)
        };

        var codec = new ActionRequestCodec();
        foreach (var item in cases)
        {
            var exception = Assert.ThrowsExactly<ActionRequestValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(item.Json), RequestId));
            Assert.AreEqual(item.Failure, exception.Failure, item.Json);
        }
    }

    [TestMethod]
    public void CodecRejectsUnsupportedSchemaActionRequestIdAndPackageIds()
    {
        var cases = new (string Json, ActionRequestFailure Failure)[]
        {
            (ValidJson().Replace("\"SchemaVersion\":2", "\"SchemaVersion\":3", StringComparison.Ordinal), ActionRequestFailure.UnsupportedSchema),
            (ValidJson().Replace("\"Action\":\"Install\"", "\"Action\":\"Uninstall\"", StringComparison.Ordinal), ActionRequestFailure.UnsupportedAction),
            (ValidJson().Replace(RequestId, "request-invalid", StringComparison.Ordinal), ActionRequestFailure.InvalidRequestId),
            (ValidJson().Replace("\"Vendor.One\"", "\"\"", StringComparison.Ordinal), ActionRequestFailure.InvalidPackageId),
            (ValidJson().Replace("\"Vendor.One\"", "\"bad id\"", StringComparison.Ordinal), ActionRequestFailure.InvalidPackageId),
            (ValidJson().Replace("[\"Vendor.One\"]", "[]", StringComparison.Ordinal), ActionRequestFailure.InvalidPackageId)
        };

        var codec = new ActionRequestCodec();
        foreach (var item in cases)
        {
            var exception = Assert.ThrowsExactly<ActionRequestValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(item.Json)));
            Assert.AreEqual(item.Failure, exception.Failure, item.Json);
        }
    }

    [TestMethod]
    public void CodecRejectsFilenameMismatchAndOversizedPayload()
    {
        var codec = new ActionRequestCodec();
        var mismatch = Assert.ThrowsExactly<ActionRequestValidationException>(() => codec.Parse(Encoding.UTF8.GetBytes(ValidJson()), "request-20260829-142233-deadbeef"));
        Assert.AreEqual(ActionRequestFailure.InvalidRequestId, mismatch.Failure);

        var oversized = new byte[ActionRequestRules.MaximumPayloadBytes + 1];
        var tooLarge = Assert.ThrowsExactly<ActionRequestValidationException>(() => codec.Parse(oversized));
        Assert.AreEqual(ActionRequestFailure.Oversized, tooLarge.Failure);
    }

    [TestMethod]
    public void AuthorizationAcceptsOnlyExactManagedPlanActionsAndUsesLegacyOrdering()
    {
        var second = State("Vendor.Second", PackageAction.Install);
        var first = State("Vendor.First", PackageAction.Install);
        var plan = Plan([second, first]);
        var request = Request(ManagedRequestAction.Install, ["Vendor.Second", "vendor.first", "Vendor.Second"], false, false);

        var authorized = new ActionRequestAuthorizationService().Authorize(request, plan);
        CollectionAssert.AreEqual(new[] { "Vendor.First", "Vendor.Second" }, authorized.Packages.Select(item => item.Package.Id).ToArray());
    }

    [TestMethod]
    public void AuthorizationRejectsWrongActionAndUnknownPackage()
    {
        var plan = Plan([State("Vendor.One", PackageAction.Install)]);
        var service = new ActionRequestAuthorizationService();
        Assert.AreEqual(ActionRequestFailure.ActionMismatch,
            Assert.ThrowsExactly<ActionRequestValidationException>(() => service.Authorize(Request(ManagedRequestAction.Update, ["Vendor.One"]), plan)).Failure);
        Assert.AreEqual(ActionRequestFailure.PackageNotInPlan,
            Assert.ThrowsExactly<ActionRequestValidationException>(() => service.Authorize(Request(ManagedRequestAction.Install, ["Vendor.Other"]), plan)).Failure);
    }

    [TestMethod]
    public void AuthorizationRejectsExternalAwarenessHeldInventoryAndRiskPolicyViolations()
    {
        var service = new ActionRequestAuthorizationService();
        var cases = new[]
        {
            State("External.One", PackageAction.Install, CatalogAuthority.OperationalExternal, ProviderKind.External),
            State("Awareness.One", PackageAction.Install, CatalogAuthority.AwarenessOnly, ProviderKind.External),
            State("Held.One", PackageAction.Install, status: PackageStatus.Held),
            State("Inventory.One", PackageAction.Install, CatalogAuthority.OperationalExternal, ProviderKind.External, PackageStatus.Inventory)
        };
        foreach (var state in cases)
        {
            var exception = Assert.ThrowsExactly<ActionRequestValidationException>(() => service.Authorize(Request(ManagedRequestAction.Install, [state.Package.Id]), Plan([state])));
            Assert.AreEqual(ActionRequestFailure.PackageNotEligible, exception.Failure, state.Package.Id);
        }

        var driver = State("Managed.Driver", PackageAction.Install, risk: PackageRisk.Driver);
        Assert.AreEqual(ActionRequestFailure.PackageNotEligible,
            Assert.ThrowsExactly<ActionRequestValidationException>(() => service.Authorize(Request(ManagedRequestAction.Install, [driver.Package.Id]), Plan([driver]))).Failure);
        Assert.AreEqual(ActionRequestFailure.PackageNotEligible,
            Assert.ThrowsExactly<ActionRequestValidationException>(() => service.Authorize(Request(ManagedRequestAction.Install, [driver.Package.Id], true), Plan([driver], pending: true))).Failure);
        Assert.HasCount(1, service.Authorize(Request(ManagedRequestAction.Install, [driver.Package.Id], true), Plan([driver])).Packages);
    }

    [TestMethod]
    public async Task FilePolicyReadsOnlyAValidatedDirectChildRequest()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var requests = Directory.CreateDirectory(Path.Combine(temporaryRoot, "logs", "requests")).FullName;
            var request = Request(ManagedRequestAction.Install, ["Vendor.One"]);
            var path = Path.Combine(requests, $"{RequestId}.json");
            await File.WriteAllBytesAsync(path, new ActionRequestCodec().Serialize(request));

            var parsed = await new ActionRequestFilePolicy().ReadValidatedAsync(temporaryRoot, path, new ActionRequestCodec());
            Assert.AreEqual(RequestId, parsed.RequestId);
        }
        finally
        {
            Directory.Delete(temporaryRoot, true);
        }
    }

    [TestMethod]
    public void FilePolicyRejectsTraversalIndirectChildrenAndOversizedFiles()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var requests = Directory.CreateDirectory(Path.Combine(temporaryRoot, "logs", "requests")).FullName;
            var policy = new ActionRequestFilePolicy();
            var outside = Path.Combine(temporaryRoot, $"{RequestId}.json");
            File.WriteAllText(outside, ValidJson());
            Assert.ThrowsExactly<ActionRequestValidationException>(() => policy.ValidateExistingRequestPath(temporaryRoot, outside));

            var nested = Directory.CreateDirectory(Path.Combine(requests, "nested"));
            var nestedPath = Path.Combine(nested.FullName, $"{RequestId}.json");
            File.WriteAllText(nestedPath, ValidJson());
            Assert.ThrowsExactly<ActionRequestValidationException>(() => policy.ValidateExistingRequestPath(temporaryRoot, nestedPath));
            Assert.ThrowsExactly<IOException>(() => policy.GetRequestsRoot("relative-data-root"));

            var badName = Path.Combine(requests, "request-invalid.json");
            File.WriteAllText(badName, ValidJson());
            Assert.ThrowsExactly<ActionRequestValidationException>(() => policy.ValidateExistingRequestPath(temporaryRoot, badName));

            var oversized = Path.Combine(requests, $"{RequestId}.json");
            File.WriteAllBytes(oversized, new byte[ActionRequestRules.MaximumPayloadBytes + 1]);
            Assert.AreEqual(ActionRequestFailure.Oversized,
                Assert.ThrowsExactly<ActionRequestValidationException>(() => policy.ValidateExistingRequestPath(temporaryRoot, oversized)).Failure);
        }
        finally
        {
            Directory.Delete(temporaryRoot, true);
        }
    }

    [TestMethod]
    public void FilePolicyRejectsAReparsePointDataRootDeterministically()
    {
        var parent = CreateTemporaryRoot();
        try
        {
            var actual = Directory.CreateDirectory(Path.Combine(parent, "data"));
            var requests = Directory.CreateDirectory(Path.Combine(actual.FullName, "logs", "requests"));
            var file = Path.Combine(requests.FullName, $"{RequestId}.json");
            File.WriteAllText(file, ValidJson());
            var policy = new ActionRequestFilePolicy(
                path => string.Equals(Path.GetFullPath(path), actual.FullName, StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | FileAttributes.ReparsePoint
                    : File.GetAttributes(path),
                File.Exists,
                path => new FileInfo(path).Length);

            Assert.ThrowsExactly<IOException>(() => policy.ValidateExistingRequestPath(actual.FullName, file));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    private static ActionRequest Request(
        ManagedRequestAction action,
        IReadOnlyList<string> ids,
        bool riskAcknowledged = false,
        bool dryRun = false) =>
        new(ActionRequestRules.CurrentSchemaVersion, RequestId, action, ids, riskAcknowledged, dryRun);

    private static string ValidJson() =>
        "{\"SchemaVersion\":2,\"RequestId\":\"request-20260829-142233-0123abcd\",\"Action\":\"Install\",\"PackageIds\":[\"Vendor.One\"],\"RiskAcknowledged\":false,\"DryRun\":false,\"ManagedCatalogRevision\":0}";

    private static PackageState State(
        string id,
        PackageAction action,
        CatalogAuthority authority = CatalogAuthority.ManagedWinGet,
        ProviderKind provider = ProviderKind.WinGet,
        PackageStatus status = PackageStatus.Missing,
        PackageRisk risk = PackageRisk.None)
    {
        var definition = new PackageDefinition(
            id, id, "Example", string.Empty, "Test", provider, authority, PackageProfile.Standard, PackagePriority.P2, risk,
            authority == CatalogAuthority.ManagedWinGet ? DeploymentPolicy.Allowlisted : DeploymentPolicy.ManualHold,
            authority == CatalogAuthority.ManagedWinGet ? MaintenancePolicy.Allowlisted : MaintenancePolicy.Hold,
            authority == CatalogAuthority.ManagedWinGet ? DeploymentClass.Managed : DeploymentClass.InventoryOnly,
            authority == CatalogAuthority.ManagedWinGet ? CatalogMaintenancePolicy.Managed : CatalogMaintenancePolicy.Manual,
            VersionRule.Latest, VersionCouplingMode.Independent, string.Empty, Lifecycle.Current, [], [], [], [LicensingModel.Free],
            ["PUBLIC-DL"], DistributionPolicy.PackageManagerOnly, [InstallationForm.WinGet], [SupportedOperatingSystem.Windows],
            DeliveryMode.None, ReleaseMode.None, provider == ProviderKind.WinGet ? DetectionMode.WinGet : DetectionMode.Registry,
            DetectionVersionPolicy.None, "Stable", string.Empty, string.Empty, string.Empty, [], null, null, null, null, null,
            risk == PackageRisk.Driver, risk == PackageRisk.Service, risk == PackageRisk.Listener, null, string.Empty, []);
        return new PackageState(definition, false, string.Empty, [], string.Empty, false, status, status.ToString(), status.ToString(), action, InventoryQuality.Complete);
    }

    private static WorkstationPlan Plan(IReadOnlyList<PackageState> states, bool pending = false) =>
        new(
            states,
            new WorkstationPlanSummary(states.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            pending ? new RebootState(true, [RebootReason.WindowsUpdate], "Windows Update reboot pending") : RebootState.Clear,
            new ProviderRefreshSummary(ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, ProviderQuality.Complete, []));

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"awt-action-request-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
