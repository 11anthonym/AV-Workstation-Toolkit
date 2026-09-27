using System.Diagnostics;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.IntegrationTests;

/// <summary>
/// Exercises Windows Restart Manager for real, which no in-process test can. The non-shipping open-app fixture stands in
/// for a desktop app, and the production <see cref="RestartManagerOpenApplications"/> must find it by the program file it
/// runs, ask it to close without forcing it, and leave it running when it refuses. The fixture keeps a visible window, only
/// a hidden window as a tray app such as ShareX does, or refuses to close as an app with unsaved work does.
/// </summary>
internal static class OpenApplicationBoundary
{
    public static IReadOnlyList<Observation> Run(string fixture)
    {
        var program = Path.GetFullPath(fixture);
        if (!File.Exists(program)) throw new FileNotFoundException("The open-app fixture was not built.", program);
        var observations = new[] { Exercise(program, "visible"), Exercise(program, "hidden"), Exercise(program, "refuse") };
        foreach (var closes in observations.Where(item => item.Mode != "refuse"))
            if (closes.Found.Count == 0 || closes.StillOpen.Count != 0 || !closes.Exited || closes.ExitCode != 0 || closes.Closed.Count == 0)
                throw new InvalidOperationException($"Restart Manager did not find and close the {closes.Mode} app without force: " +
                                                    System.Text.Json.JsonSerializer.Serialize(closes));
        var refuses = observations[2];
        if (refuses.Found.Count == 0 || refuses.StillOpen.Count == 0 || refuses.Exited || refuses.Closed.Count != 0)
            throw new InvalidOperationException("An app that refused to close was closed or not reported as still open: " +
                                                System.Text.Json.JsonSerializer.Serialize(refuses));
        Console.WriteLine("OPEN_APP_CLOSE_OK " + string.Join(' ', observations.Select(item =>
            $"{item.Mode}={item.Kind}:{(item.Exited ? "closed" : "still-open")}")));
        return observations;
    }

    private static Observation Exercise(string program, string mode)
    {
        var ready = Path.Combine(Path.GetTempPath(), $"avwt-open-app-{Guid.NewGuid():N}.ready");
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(ready);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The open-app fixture did not start.");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(ready) && !child.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(50);
            if (!File.Exists(ready)) throw new InvalidOperationException($"The {mode} open-app fixture never became ready.");

            var kind = string.Join(",", RestartManager.AppsUsing([program])
                .Where(app => app.Process.ProcessId == child.Id).Select(app => app.Kind));
            var service = new RestartManagerOpenApplications(new FixedLocator([program]));
            var found = service.FindOpen("Fixture.OpenApp");
            using var closure = service.Close("Fixture.OpenApp");
            var exited = child.WaitForExit(mode == "refuse" ? 2_000 : 30_000);
            var reopened = closure.Reopen();
            return new(mode, kind, found, closure.Closed, closure.StillOpen, exited, exited ? child.ExitCode : null, reopened);
        }
        finally
        {
            // Cleanup of the fixture this boundary started; the product itself never forces an app to close.
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit();
            }
            File.Delete(ready);
        }
    }

    internal sealed record Observation(
        string Mode,
        string Kind,
        IReadOnlyList<string> Found,
        IReadOnlyList<string> Closed,
        IReadOnlyList<string> StillOpen,
        bool Exited,
        int? ExitCode,
        IReadOnlyList<string> Reopened);

    private sealed class FixedLocator(IReadOnlyList<string> programs) : IInstalledProgramLocator
    {
        public IReadOnlyList<string> Locate(string packageId) => programs;
    }
}
