using System.Text;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Application.Diagnostics;

public sealed class ReadOnlyDiagnosticsService(
    IRuntimeDiagnosticsProvider runtimeProvider,
    ApplicationDiagnosticContext context) : IReadOnlyDiagnosticsService
{
    private readonly IRuntimeDiagnosticsProvider runtimeProvider = runtimeProvider ?? throw new ArgumentNullException(nameof(runtimeProvider));
    private readonly ApplicationDiagnosticContext context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<DiagnosticsSnapshot> ComposeAsync(WorkstationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RuntimeDiagnosticFacts runtime;
        try
        {
            runtime = await runtimeProvider.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            var detail = DiagnosticsRedactor.Sanitize(exception.Message);
            var failed = new DiagnosticValue(DiagnosticEvidenceState.Failed, "Unavailable", detail);
            runtime = new(failed, new(DiagnosticEvidenceState.Unknown, "Not loaded by the compiled application"), failed,
                new(DiagnosticEvidenceState.Unknown, "Unknown"), failed, failed, failed);
        }

        var providers = plan.Providers;
        var sources = (providers.ExternalSources ?? []).Select(source => new DiagnosticSourceSnapshot(
            source.Source.ToString(),
            source.Source switch
            {
                RegistryInventorySource.Hklm64 => "HKLM 64-bit uninstall inventory",
                RegistryInventorySource.Hklm32 => "HKLM 32-bit uninstall inventory",
                _ => "HKCU uninstall inventory"
            },
            source.Available ? DiagnosticEvidenceState.Available : DiagnosticEvidenceState.Failed,
            source.EntryCount,
            DiagnosticsRedactor.Sanitize(source.Detail))).ToArray();
        var issues = BuildIssues(plan, runtime, sources);
        var counts = new CatalogDiagnosticCounts(
            plan.Summary.Total,
            plan.Packages.Count(item => item.Package.HasManagedExecutionAuthority),
            plan.Packages.Count(item => item.Package.Authority == CatalogAuthority.OperationalExternal),
            plan.Summary.Awareness,
            plan.Summary.Current,
            plan.Summary.Missing,
            plan.Summary.Updates,
            plan.Summary.ManualActions,
            plan.Summary.InventoryWarnings,
            plan.Summary.Errors);
        var inventoryState = Map(providers.WinGetInventoryQuality);
        var updateState = Map(providers.WinGetUpdateQuality);
        var externalState = Map(providers.ExternalInventoryQuality);
        var rebootState = Map(providers.RebootQuality);
        var overall = issues.Any(issue => issue.Severity == DiagnosticSeverity.Error) ? DiagnosticEvidenceState.Failed
            : issues.Count > 0 ? DiagnosticEvidenceState.Warning : DiagnosticEvidenceState.Available;
        var provisional = new DiagnosticsSnapshot(1, SanitizeContext(context), runtime, inventoryState, updateState, externalState,
            rebootState, sources, plan.Reboot.Pending, plan.Reboot.Reasons, counts, issues, overall, string.Empty);
        return provisional with { Text = Format(provisional) };
    }

    public static DiagnosticEvidenceState Map(ProviderQuality quality) => quality switch
    {
        ProviderQuality.Complete => DiagnosticEvidenceState.Available,
        ProviderQuality.Partial => DiagnosticEvidenceState.Partial,
        ProviderQuality.Unavailable => DiagnosticEvidenceState.Unavailable,
        ProviderQuality.Malformed => DiagnosticEvidenceState.Failed,
        _ => DiagnosticEvidenceState.Unknown
    };

    private static IReadOnlyList<DiagnosticIssue> BuildIssues(
        WorkstationPlan plan,
        RuntimeDiagnosticFacts runtime,
        IReadOnlyList<DiagnosticSourceSnapshot> sources)
    {
        var issues = new List<DiagnosticIssue>();
        AddProviderIssue(plan.Providers.WinGetInventoryQuality, "WinGet inventory", plan.Providers.WinGetInventoryDetail);
        AddProviderIssue(plan.Providers.WinGetUpdateQuality, "WinGet updates", plan.Providers.WinGetUpdateDetail);
        AddProviderIssue(plan.Providers.ExternalInventoryQuality, "External inventory", plan.Providers.ExternalInventoryDetail);
        AddProviderIssue(plan.Providers.RebootQuality, "Reboot detection", plan.Providers.RebootDetail);
        if (sources.Any(source => source.State == DiagnosticEvidenceState.Failed))
            issues.Add(new(DiagnosticIssueCode.PartialRegistrySourceFailure, DiagnosticSeverity.Warning, "External inventory",
                $"{sources.Count(source => source.State == DiagnosticEvidenceState.Failed)} registry source(s) unavailable."));
        if (plan.Reboot.Pending)
            issues.Add(new(DiagnosticIssueCode.PendingReboot, DiagnosticSeverity.Warning, "Reboot", plan.Reboot.Summary));
        var quarantined = plan.Packages.Count(item => item.Package.MetadataDetails.MetadataQuarantined);
        if (quarantined > 0)
            issues.Add(new(DiagnosticIssueCode.QuarantinedMetadata, DiagnosticSeverity.Warning, "Catalog", $"{quarantined} catalog record(s) contain quarantined metadata."));
        var stale = plan.Packages.Count(item => item.Package.MetadataDetails.MetadataVerificationState == MetadataVerificationState.VerificationRequired);
        if (stale > 0)
            issues.Add(new(DiagnosticIssueCode.StaleVerification, DiagnosticSeverity.Warning, "Catalog", $"{stale} catalog record(s) require metadata verification."));
        if (runtime.WinGetPath.State is DiagnosticEvidenceState.Failed or DiagnosticEvidenceState.Unavailable)
            issues.Add(new(DiagnosticIssueCode.ProviderUnavailable, DiagnosticSeverity.Warning, "WinGet", runtime.WinGetPath.Detail));
        return issues;

        void AddProviderIssue(ProviderQuality quality, string source, string detail)
        {
            if (quality == ProviderQuality.Complete) return;
            var code = quality == ProviderQuality.Partial ? DiagnosticIssueCode.SourceWarning : DiagnosticIssueCode.InventoryUnavailable;
            var severity = quality == ProviderQuality.Malformed ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
            issues.Add(new(code, severity, source, DiagnosticsRedactor.Sanitize(detail)));
        }
    }

    private static ApplicationDiagnosticContext SanitizeContext(ApplicationDiagnosticContext value) => new(
        DiagnosticsRedactor.Sanitize(value.ProductVersion),
        DiagnosticsRedactor.Sanitize(value.ExecutionMode),
        DiagnosticsRedactor.SanitizePath(value.DataRoot),
        DiagnosticsRedactor.SanitizePath(value.LogsPath));

    private static string Format(DiagnosticsSnapshot value)
    {
        var text = new StringBuilder();
        text.AppendLine("AV Workstation Toolkit diagnostics");
        text.AppendLine();
        text.AppendLine("[AVWorkstationToolkit]");
        text.AppendLine($"Version: {value.Application.ProductVersion}");
        text.AppendLine($"Execution: {value.Application.ExecutionMode}");
        text.AppendLine($"Data root: {value.Application.DataRoot}");
        text.AppendLine($"Logs: {value.Application.LogsPath}");
        text.AppendLine();
        text.AppendLine("[Runtime]");
        text.AppendLine($"Windows: {value.Runtime.WindowsVersion.Value}");
        text.AppendLine($"Windows PowerShell: {value.Runtime.WindowsPowerShellVersion.Value}");
        text.AppendLine($"Compiled runtime: {value.Runtime.CompiledRuntimeVersion.Value}");
        text.AppendLine($"Process architecture: {value.Runtime.ProcessArchitecture.Value}");
        text.AppendLine($"Privilege: {value.Runtime.Privilege.Value}");
        text.AppendLine();
        text.AppendLine("[WinGet]");
        text.AppendLine($"Path: {DiagnosticsRedactor.SanitizePath(value.Runtime.WinGetPath.Value)}");
        text.AppendLine($"Version: {value.Runtime.WinGetVersion.Value}");
        text.AppendLine($"Installed inventory: {value.WinGetInventoryState}");
        text.AppendLine($"Update inventory: {value.WinGetUpdateState}");
        text.AppendLine();
        text.AppendLine("[Privilege and reboot]");
        text.AppendLine($"Pending reboot: {value.RebootPending}");
        text.AppendLine($"Windows Update: {value.RebootReasons.Contains(RebootReason.WindowsUpdate)}");
        text.AppendLine($"Component Based Servicing: {value.RebootReasons.Contains(RebootReason.ComponentBasedServicing)}");
        text.AppendLine($"Detection state: {value.RebootDetectionState}");
        text.AppendLine();
        text.AppendLine("[External inventory]");
        text.AppendLine($"Quality: {value.ExternalInventoryState}");
        foreach (var source in value.RegistrySources)
            text.AppendLine($"  {source.Label}: {source.State} ({source.EntryCount} entries) - {source.Detail}");
        text.AppendLine();
        text.AppendLine("[Catalog]");
        text.AppendLine($"Records: {value.Catalog.Total}; WinGet managed: {value.Catalog.WinGetManaged}; operational external: {value.Catalog.OperationalExternal}; awareness: {value.Catalog.Awareness}");
        text.AppendLine($"Current: {value.Catalog.Current}; missing: {value.Catalog.Missing}; updates: {value.Catalog.Updates}; manual: {value.Catalog.Manual}; inventory warnings: {value.Catalog.InventoryWarnings}; errors: {value.Catalog.Errors}");
        text.AppendLine();
        text.AppendLine("[Warnings and errors]");
        if (value.Issues.Count == 0) text.AppendLine("None");
        else foreach (var issue in value.Issues) text.AppendLine($"{issue.Severity}: {issue.Code} [{issue.Source}] - {issue.Detail}");
        return DiagnosticsRedactor.Sanitize(text.ToString().TrimEnd());
    }
}

public static partial class DiagnosticsRedactor
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = ControlCharacters().Replace(value, " ");
        text = CredentialValues().Replace(text, "${key}[REDACTED]");
        text = BearerValues().Replace(text, "${prefix}[REDACTED]");
        text = UriCredentials().Replace(text, "${prefix}[REDACTED]@");
        text = ReplaceRootAnywhere(text, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        return ReplaceRootAnywhere(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
    }

    public static string SanitizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var safe = Sanitize(value);
        return ReplaceRoot(safe, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%") is { } local && local != safe
            ? local
            : ReplaceRoot(safe, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
    }

    private static string ReplaceRoot(string value, string root, string token)
    {
        if (string.IsNullOrWhiteSpace(root)) return value;
        var normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (value.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return token;
        var prefix = normalized + Path.DirectorySeparatorChar;
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? token + Path.DirectorySeparatorChar + value[prefix.Length..] : value;
    }

    private static string ReplaceRootAnywhere(string value, string root, string token) =>
        string.IsNullOrWhiteSpace(root) ? value : value.Replace(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), token, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ControlCharacters();
    [GeneratedRegex("""(?i)(?<key>(?:password|passwd|pwd|token|secret|api[-_]?key|client[-_]?secret)\s*(?:=|:)\s*)(?:"[^"]*"|'[^']*'|[^\s,;]+)""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CredentialValues();
    [GeneratedRegex(@"(?i)(?<prefix>Authorization:\s*Bearer\s+)\S+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BearerValues();
    [GeneratedRegex(@"(?i)(?<prefix>://[^:/\s]+:)[^@\s]+@", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UriCredentials();
}
