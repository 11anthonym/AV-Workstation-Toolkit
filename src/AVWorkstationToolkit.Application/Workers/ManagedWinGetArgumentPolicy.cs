using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Application.Workers;

/// <summary>
/// Constructs the complete reviewed WinGet mutation vector from typed,
/// already-authorized package input. No caller-supplied arguments are accepted.
/// </summary>
public static class ManagedWinGetArgumentPolicy
{
    public static IReadOnlyList<string> Create(PackageExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionRequestRules.ValidatePackageIds([request.Id]);
        if (!Enum.IsDefined(request.Action))
            throw new ArgumentOutOfRangeException(nameof(request), "Only Install and Update actions are supported.");
        if (!Enum.IsDefined(request.Risk))
            throw new ArgumentOutOfRangeException(nameof(request), "The package risk is unsupported.");
        if (!Enum.IsDefined(request.InstallerMode))
            throw new ArgumentOutOfRangeException(nameof(request), "The package installer mode is unsupported.");

        var arguments = new List<string>
        {
            request.Action == ManagedRequestAction.Install ? "install" : "upgrade",
            "--id", request.Id, "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements"
        };
        if (request.Risk == PackageRisk.None)
        {
            // --silent is what makes WinGet pass /quiet to the installer, including to
            // 'msiexec /x <ProductCode>' for a manifest that upgrades by uninstalling the previous
            // version. A machine-scope MSI uninstall cannot obtain elevation under /quiet from the
            // standard-user process AVWT requires, so msiexec returns 1603 and WinGet reports
            // 0x8A150030. A package whose installer needs its own elevation path is marked
            // InstallerDefault in the managed catalog and omits the flag.
            if (request.InstallerMode == InstallerExecutionMode.Silent) arguments.Add("--silent");
            // Always retained: this is what guarantees WinGet never waits on a prompt, and it is
            // independent of the installer's own UI mode.
            arguments.Add("--disable-interactivity");
        }
        return arguments.AsReadOnly();
    }
}
