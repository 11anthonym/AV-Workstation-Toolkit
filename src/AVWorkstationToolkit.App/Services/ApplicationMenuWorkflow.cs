using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Windows;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Domain.Catalog;
using Microsoft.Win32;

namespace AVWorkstationToolkit.App.Services;

public sealed record PlanExportOutcome(bool Completed, string Path, string Detail);

public interface IApplicationMenuWorkflow
{
    PlanExportOutcome ExportPlan(WorkstationPlan plan);
    string OpenLogs();
}

public sealed class ApplicationMenuWorkflow(
    string dataRoot,
    IValidatedUserHandoffService handoffs,
    TimeProvider? timeProvider = null) : IApplicationMenuWorkflow
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false, true);
    private readonly string dataRoot = RequireRoot(dataRoot);
    private readonly IValidatedUserHandoffService handoffs = handoffs ?? throw new ArgumentNullException(nameof(handoffs));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public PlanExportOutcome ExportPlan(WorkstationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var reports = Path.Combine(dataRoot, "reports");
        Directory.CreateDirectory(reports);
        RejectDirectoryReparse(dataRoot);
        RejectDirectoryReparse(reports);
        var stamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var dialog = new SaveFileDialog
        {
            Title = "Export application plan",
            Filter = "JSON report (*.json)|*.json",
            AddExtension = true,
            DefaultExt = ".json",
            InitialDirectory = reports,
            FileName = $"AppPlan-{stamp}.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) != true)
            return new(false, string.Empty, "Plan export cancelled.");
        var path = Path.GetFullPath(dialog.FileName);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The plan export must use a .json filename.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The selected plan export file is a reparse point.");
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("The selected plan export directory is unavailable.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The selected plan export directory is unavailable.");
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                writer.Write(PlanExportFormatter.Format(plan, timeProvider.GetUtcNow()));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return new(true, path, $"Exported application plan: {path}");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string OpenLogs()
    {
        handoffs.OpenLogs(dataRoot);
        return Path.Combine(dataRoot, "logs");
    }

    private static string RequireRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new IOException("The application data root must be absolute.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        var volume = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full, volume, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The application data root cannot be a filesystem root.");
        return full;
    }

    private static void RejectDirectoryReparse(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The application report path is unavailable or uses a reparse point.");
    }
}

public static class PlanExportFormatter
{
    public static string Format(WorkstationPlan plan, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var document = new PlanExportDocument(
            3,
            generatedAt,
            new(plan.Reboot.Pending, plan.Reboot.Reasons.Select(value => value.ToString()).ToArray(), Sanitize(plan.Reboot.Summary)),
            new(
                plan.Providers.WinGetInventoryQuality.ToString(),
                plan.Providers.WinGetUpdateQuality.ToString(),
                plan.Providers.ExternalInventoryQuality.ToString(),
                plan.Providers.RebootQuality.ToString(),
                plan.Providers.Warnings.Select(Sanitize).ToArray()),
            plan.Summary,
            plan.Packages.Select(state => new PlanExportPackage(
                state.Package.Profile.ToString(),
                state.Package.Priority.ToToken(),
                state.Package.Vendor,
                state.Package.Name,
                state.Package.Id,
                state.Package.Provider.ToString(),
                state.Package.Authority.ToString(),
                state.Package.Risk.ToString(),
                state.Package.DeliveryMode.ToString(),
                state.Status.ToString(),
                Sanitize(state.StatusDetail),
                state.InventoryQuality.ToString(),
                state.InstalledVersion,
                state.InstalledVersions,
                state.AvailableVersion,
                state.Action.ToString(),
                state.CanSelect && state.Package.HasManagedExecutionAuthority,
                Sanitize(state.Package.Note))).ToArray());
        return JsonSerializer.Serialize(document, PlanExportJsonContext.Default.PlanExportDocument);
    }

    private static string Sanitize(string value) => DiagnosticsRedactor.Sanitize(value);
}

public sealed record PlanExportDocument(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    PlanExportReboot Reboot,
    PlanExportProviders Providers,
    WorkstationPlanSummary Summary,
    IReadOnlyList<PlanExportPackage> Packages);

public sealed record PlanExportReboot(bool Pending, IReadOnlyList<string> Reasons, string Summary);

public sealed record PlanExportProviders(
    string WinGetInventoryQuality,
    string WinGetUpdateQuality,
    string ExternalInventoryQuality,
    string RebootQuality,
    IReadOnlyList<string> Warnings);

public sealed record PlanExportPackage(
    string Profile,
    string Priority,
    string Vendor,
    string Name,
    string Id,
    string Provider,
    string Authority,
    string Risk,
    string DeliveryMode,
    string Status,
    string StatusDetail,
    string InventoryQuality,
    string InstalledVersion,
    IReadOnlyList<string> InstalledVersions,
    string AvailableVersion,
    string Action,
    bool CanSelect,
    string Note);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PlanExportDocument))]
internal sealed partial class PlanExportJsonContext : JsonSerializerContext;
