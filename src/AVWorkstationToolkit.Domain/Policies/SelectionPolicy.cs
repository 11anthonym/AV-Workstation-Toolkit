using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;

namespace AVWorkstationToolkit.Domain.Policies;

public sealed class SelectionPolicy
{
    public PolicyDecision Evaluate(PackageState state, RebootState reboot, bool riskAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(reboot);

        if (state.Package.Authority != CatalogAuthority.ManagedWinGet || state.Package.Provider != ProviderKind.WinGet)
        {
            return new(PolicyDisposition.NotActionable, "NonManagedProvider", "External and awareness records cannot enter the managed worker.");
        }
        if (state.Status == PackageStatus.Held)
        {
            return new(PolicyDisposition.Held, "MaintenanceHold", "Automated maintenance is held by catalog policy.");
        }
        if (state.Action == PackageAction.Manual)
        {
            return new(PolicyDisposition.ManualOnly, "ManualOnly", "The package requires a manual workflow.");
        }
        if (state.Action is not (PackageAction.Install or PackageAction.Update))
        {
            return new(PolicyDisposition.NotActionable, "NoManagedAction", "No managed install or update is available.");
        }
        if (!state.Package.HasManagedExecutionAuthority)
        {
            return new(PolicyDisposition.Blocked, "ExecutionAuthorityDenied", "Catalog policy does not grant managed execution authority.");
        }
        if (reboot.Pending && state.Package.Risk != PackageRisk.None)
        {
            return new(PolicyDisposition.Blocked, "PendingRebootRiskBlocked", $"Risk-bearing package is blocked while reboot pending: {state.Package.Risk}.");
        }
        if (state.Package.Risk != PackageRisk.None && !riskAcknowledged)
        {
            return new(PolicyDisposition.RequiresAcknowledgement, "RiskAcknowledgementRequired", $"Explicit risk acknowledgement is required for {state.Package.Risk}.");
        }
        return new(PolicyDisposition.Allowed, "ManagedActionAllowed", "The exact catalogued package action is allowed by deterministic policy.");
    }
}
