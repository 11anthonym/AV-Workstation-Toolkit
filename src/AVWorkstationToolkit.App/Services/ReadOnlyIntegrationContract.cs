using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Planning;

namespace AVWorkstationToolkit.App.Services;

/// <summary>
/// Classifies the provider results that a single <c>--read-only-check</c> refresh already produced.
/// It queries nothing and re-derives no quality: every value here comes from the one
/// <see cref="ProviderRefreshSummary"/> that refresh returned.
/// </summary>
internal static class ReadOnlyIntegrationContract
{
    internal enum ProviderClassification { Exercised, NotApplicable, Failed }

    internal sealed record ProviderOutcome(
        string Name,
        ProviderQuality Quality,
        ProviderFailureKind Failure,
        ProviderClassification Classification,
        string Detail)
    {
        internal string Report() => Classification == ProviderClassification.Exercised
            ? $"READONLY_PROVIDER name={Name} quality={Quality} classification=exercised"
            : $"READONLY_PROVIDER name={Name} quality={Quality} failure={Failure} " +
              $"classification={(Classification == ProviderClassification.NotApplicable ? "not-applicable" : "failed")} detail={Detail}";
    }

    /// <summary>
    /// Returns one outcome per provider the refresh exercised, in the order the warning surface lists them.
    /// </summary>
    internal static IReadOnlyList<ProviderOutcome> Evaluate(ProviderRefreshSummary providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return
        [
            Classify("WinGetInstalledInventory", providers.WinGetInventoryQuality, providers.WinGetInventoryFailure, providers.WinGetInventoryDetail),
            Classify("WinGetUpdateCheck", providers.WinGetUpdateQuality, providers.WinGetUpdateFailure, providers.WinGetUpdateDetail),
            Classify("ExternalApplicationInventory", providers.ExternalInventoryQuality, providers.ExternalInventoryFailure, providers.ExternalInventoryDetail),
            Classify("RestartDetection", providers.RebootQuality, providers.RebootFailure, providers.RebootDetail)
        ];
    }

    private static ProviderOutcome Classify(string name, ProviderQuality quality, ProviderFailureKind failure, string detail)
    {
        // TrustFailure reaches this summary only from the trusted-WinGet resolver, and only when the
        // workstation has no Microsoft Desktop App Installer WinGet that satisfies the trust policy.
        // The product treats that as a supported configuration rather than a defect: the plan degrades
        // to InventoryQuality.Unavailable and warns. The integration reports such a provider as not
        // applicable instead of requiring Complete from a provider that is not present to exercise.
        // Every other non-Complete result is a provider that was exercised and did not meet its
        // contract - Malformed output, an execution failure, a timeout, or a partial read.
        var classification = quality switch
        {
            ProviderQuality.Complete => ProviderClassification.Exercised,
            ProviderQuality.Unavailable when failure == ProviderFailureKind.TrustFailure => ProviderClassification.NotApplicable,
            _ => ProviderClassification.Failed
        };
        return new(name, quality, failure, classification, CleanDetail(detail));
    }

    private static string CleanDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "No additional detail was provided.";
        var clean = DiagnosticsRedactor.Sanitize(detail).ReplaceLineEndings(" ").Trim();
        return clean.Length > 240 ? clean[..240] + " [excerpt shortened]" : clean;
    }
}
