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

        var arguments = new List<string>
        {
            request.Action == ManagedRequestAction.Install ? "install" : "upgrade",
            "--id", request.Id, "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements"
        };
        if (request.Risk == PackageRisk.None)
        {
            arguments.Add("--silent");
            arguments.Add("--disable-interactivity");
        }
        return arguments.AsReadOnly();
    }
}
