using System.IO;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Diagnostics;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.App.Services;

/// <summary>
/// Before an install or update starts, finds which of its apps are open and asks the technician what to do about them.
/// The answer becomes the request's consent to close apps for exactly those packages; the worker checks again before
/// each package, closes only by asking, and skips a package whose apps are still open.
/// </summary>
internal static class OpenAppsCheck
{
    internal sealed record Outcome(bool Proceed, IReadOnlySet<string> Skipped, IReadOnlyList<string> CloseFor, string Problem)
    {
        public static Outcome NothingOpen(string problem = "") =>
            new(true, new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], problem);
    }

    public static async Task<Outcome> RunAsync(
        IOpenApplicationService openApplications,
        IActionConfirmation? confirmation,
        PackageAction action,
        IReadOnlyList<(string Id, string Name)> apps)
    {
        (string Id, string Name, IReadOnlyList<string> Open)[] open;
        try
        {
            // Restart Manager and the registry are read off the UI thread; nothing is changed by looking.
            open = await Task.Run(() => apps
                .Select(app => (app.Id, app.Name, Open: openApplications.FindOpen(app.Id)))
                .Where(app => app.Open.Count > 0)
                .ToArray()).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException or System.Text.RegularExpressions.RegexMatchTimeoutException or
                                              InvalidOperationException)
        {
            // Looking is only an aid: without it the run behaves as before, and the worker still checks each app itself.
            return Outcome.NothingOpen(DiagnosticsRedactor.Sanitize(exception.Message));
        }
        if (open.Length == 0) return Outcome.NothingOpen();

        var prompt = OpenAppsPrompt.For(action, apps.Count, open.Select(app => (app.Name, app.Open)).ToArray())!;
        var ids = open.Select(app => app.Id).ToArray();
        return (confirmation?.ChooseForOpenApps(prompt) ?? OpenAppsDecision.Cancel) switch
        {
            OpenAppsDecision.Close => new(true, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ids, string.Empty),
            OpenAppsDecision.Skip => new(true, ids.ToHashSet(StringComparer.OrdinalIgnoreCase), [], string.Empty),
            _ => new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], string.Empty)
        };
    }
}
