using Microsoft.Win32;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Infrastructure.Windows.Reboot;

public sealed class WindowsRebootStateProvider : IRebootStateProvider
{
    private static readonly (RebootReason Reason, string Path)[] Signals =
    [
        (RebootReason.WindowsUpdate, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"),
        (RebootReason.ComponentBasedServicing, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
    ];

    public Task<RebootDetectionResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var reasons = new List<RebootReason>();
        var failures = new List<string>();
        foreach (var signal in Signals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(signal.Path, writable: false);
                if (key is not null) reasons.Add(signal.Reason);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                failures.Add(DiagnosticText.Sanitize(exception.Message));
            }
        }
        var quality = failures.Count == 0 ? ProviderQuality.Complete : failures.Count == Signals.Length ? ProviderQuality.Unavailable : ProviderQuality.Partial;
        var failure = quality switch
        {
            ProviderQuality.Complete => ProviderFailureKind.None,
            ProviderQuality.Partial => ProviderFailureKind.PartialInventory,
            _ => ProviderFailureKind.ProviderUnavailable
        };
        var reasonText = reasons.Select(reason => reason == RebootReason.WindowsUpdate ? "Windows Update" : "Component Based Servicing").ToArray();
        var detail = failures.Count > 0
            ? $"Reboot-state detection was incomplete for {failures.Count} of {Signals.Length} supported signals."
            : reasonText.Length > 0 ? string.Join("; ", reasonText) : "No pending reboot signals";
        return Task.FromResult(new RebootDetectionResult(reasons.Count > 0, reasons, quality, failure, detail));
    }
}
