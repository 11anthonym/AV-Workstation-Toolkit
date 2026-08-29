using System.Runtime.InteropServices;
using System.Security.Principal;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Infrastructure.Windows.Diagnostics;

public sealed class WindowsRuntimeDiagnosticsProvider(
    IWinGetResolver resolver,
    IWinGetReadOnlyProcessRunner runner) : IRuntimeDiagnosticsProvider
{
    private readonly IWinGetResolver resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IWinGetReadOnlyProcessRunner runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<RuntimeDiagnosticFacts> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticValue path;
        DiagnosticValue version;
        if (!resolved.Trusted)
        {
            path = new(DiagnosticEvidenceState.Unavailable, "Unavailable", DiagnosticText.Sanitize(resolved.Detail));
            version = new(DiagnosticEvidenceState.Unavailable, "Unavailable", "A trusted WinGet executable was not resolved.");
        }
        else
        {
            path = new(DiagnosticEvidenceState.Available, resolved.ExecutablePath, DiagnosticText.Sanitize(resolved.Detail));
            var result = await runner.RunAsync(WinGetReadOnlyOperation.Version, cancellationToken).ConfigureAwait(false);
            var versionText = FirstLine(result.StdOut);
            if (result.ExitCode == 0 && result.Failure == ProviderFailureKind.None && versionText.Length > 0)
                version = new(DiagnosticEvidenceState.Available, versionText);
            else
                version = new(DiagnosticEvidenceState.Failed, "Unavailable", DiagnosticText.Sanitize(
                    string.Join(" ", new[] { result.StdErr, result.StdOut }.Where(value => value.Length > 0))));
        }

        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return new(
            new(DiagnosticEvidenceState.Available, Environment.OSVersion.VersionString),
            new(DiagnosticEvidenceState.Unknown, "Not loaded by compiled app", "The shipping reference runtime uses Windows PowerShell 5.1."),
            new(DiagnosticEvidenceState.Available, RuntimeInformation.FrameworkDescription),
            new(DiagnosticEvidenceState.Available, RuntimeInformation.ProcessArchitecture.ToString()),
            new(DiagnosticEvidenceState.Available, elevated ? "Elevated" : "Standard user"),
            path,
            version);
    }

    private static string FirstLine(string value) => DiagnosticText.Sanitize(
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty);
}
