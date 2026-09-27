using System.Globalization;
using System.Windows;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.App.Services;

/// <summary>
/// The question asked when an install or update includes apps that may install a driver, add a background service, or
/// accept network connections. It is asked once, when the technician starts the operation, and names those apps; the
/// answer covers that operation only, and the worker still checks it for every app.
/// </summary>
public sealed record SystemImpactPrompt(string Title, string Message, string ProceedLabel)
{
    private const int ListedApps = 8;

    /// <summary>Returns null when none of the apps has a system impact, because there is nothing to confirm.</summary>
    public static SystemImpactPrompt? For(PackageAction action, IReadOnlyCollection<(string Name, PackageRisk Risk)> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var risky = apps.Where(app => app.Risk != PackageRisk.None).ToArray();
        if (risky.Length == 0) return null;
        var verb = Verb(action);
        var count = AppCount(apps.Count);
        var which = risky.Length == apps.Count
            ? apps.Count == 1 ? "It makes" : "They make"
            : risky.Length == 1 ? "1 of them makes" : $"{risky.Length.ToString(CultureInfo.CurrentCulture)} of them make";
        var lines = risky.Take(ListedApps).Select(app => $"  •  {app.Name} — may {Effect(app.Risk)}").ToList();
        if (risky.Length > ListedApps) lines.Add($"  •  and {(risky.Length - ListedApps).ToString(CultureInfo.CurrentCulture)} more");
        var message = $"{verb} {count}? {which} system-level changes to this PC:{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, lines) +
            $"{Environment.NewLine}{Environment.NewLine}Each app is checked again before it starts.";
        return new SystemImpactPrompt("Confirm system changes", message, $"{verb} {count}");
    }

    public static string Effect(PackageRisk risk) => risk switch
    {
        PackageRisk.Driver => "install a driver",
        PackageRisk.Service => "add a background service",
        PackageRisk.Listener => "accept network connections",
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, "Only a system impact has an effect to describe.")
    };

    internal static string Verb(PackageAction action) => action == PackageAction.Update ? "Update" : "Install";

    internal static string AppCount(int count) => count == 1 ? "1 app" : $"{count.ToString(CultureInfo.CurrentCulture)} apps";
}

/// <summary>What the technician chose when some of the apps to change are open.</summary>
public enum OpenAppsDecision
{
    /// <summary>Change nothing.</summary>
    Cancel,
    /// <summary>Leave the open apps out of this run and change the rest.</summary>
    Skip,
    /// <summary>Ask the open apps to close, each just before its own installer runs.</summary>
    Close
}

/// <summary>
/// The question asked before an install or update when some of the selected apps are open, which is the Windows
/// installer convention for files in use: name the open apps and offer to close them, skip them, or stop.
/// </summary>
public sealed record OpenAppsPrompt(string Title, string Message, string CloseLabel, string SkipLabel)
{
    private const int ListedApps = 8;

    /// <summary>Returns null when none of the apps is open.</summary>
    public static OpenAppsPrompt? For(PackageAction action, int total, IReadOnlyCollection<(string Name, IReadOnlyList<string> OpenApps)> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        var listed = open.Where(app => app.OpenApps.Count > 0).ToArray();
        if (listed.Length == 0) return null;
        var verb = SystemImpactPrompt.Verb(action);
        var which = listed.Length == total
            ? total == 1 ? "It's open" : "They're open"
            : listed.Length == 1 ? "1 of them is open" : $"{listed.Length.ToString(CultureInfo.CurrentCulture)} of them are open";
        var lines = listed.Take(ListedApps).Select(app =>
        {
            var running = app.OpenApps.Where(name => !name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return running.Length == 0 ? $"  •  {app.Name}" : $"  •  {app.Name} ({OpenApplicationText.Names(running)})";
        }).ToList();
        if (listed.Length > ListedApps) lines.Add($"  •  and {(listed.Length - ListedApps).ToString(CultureInfo.CurrentCulture)} more");
        var plural = listed.Length != 1;
        var message = $"{verb} {SystemImpactPrompt.AppCount(total)}? {which}, and an installer can fail while its app is running:" +
            $"{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}{Environment.NewLine}{Environment.NewLine}" +
            $"AVWT can ask {(plural ? "each one" : "it")} to close just before {(plural ? "its" : "the")} {verb.ToLowerInvariant()}, the way Windows does when you sign out. " +
            $"Nothing is forced: an app with unsaved work can refuse, and is then skipped. Apps that support it reopen afterward.";
        return new OpenAppsPrompt("Close open apps", message, plural ? "Close apps and continue" : "Close app and continue",
            plural ? "Skip open apps" : "Skip this app");
    }
}

/// <summary>Asks the questions an install or update may need, kept behind an interface so the view models stay testable.</summary>
public interface IActionConfirmation
{
    /// <summary>
    /// Returns true only when the technician chooses to go ahead. Cancel is the default button, the Escape key, and the
    /// answer when the question is closed.
    /// </summary>
    bool ConfirmSystemImpact(SystemImpactPrompt prompt);

    /// <summary>Cancel is the default button, the Escape key, and the answer when the question is closed.</summary>
    OpenAppsDecision ChooseForOpenApps(OpenAppsPrompt prompt);
}

public sealed class WpfActionConfirmation(Window? owner = null) : IActionConfirmation
{
    public Window? Owner { get; set; } = owner;

    public bool ConfirmSystemImpact(SystemImpactPrompt prompt) => ChoiceDialog.ConfirmSystemImpact(Owner, prompt);

    public OpenAppsDecision ChooseForOpenApps(OpenAppsPrompt prompt) => ChoiceDialog.ChooseForOpenApps(Owner, prompt);
}

/// <summary>
/// A question with every answer named, which a message box can't do. The safe answer is the default button, the Escape
/// key, and the answer when the dialog is closed.
/// </summary>
internal static class ChoiceDialog
{
    public static bool Ask(Window? owner, string title, string message, string safeLabel, string proceedLabel) =>
        Choose(owner, title, message, safeLabel, proceedLabel) == 0;

    public static bool ConfirmSystemImpact(Window? owner, SystemImpactPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return Ask(owner, prompt.Title, prompt.Message, safeLabel: "Cancel", proceedLabel: prompt.ProceedLabel);
    }

    public static OpenAppsDecision ChooseForOpenApps(Window? owner, OpenAppsPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return Choose(owner, prompt.Title, prompt.Message, "Cancel", prompt.CloseLabel, prompt.SkipLabel) switch
        {
            0 => OpenAppsDecision.Close,
            1 => OpenAppsDecision.Skip,
            _ => OpenAppsDecision.Cancel
        };
    }

    /// <summary>Returns the index of the chosen action, or -1 for the safe answer.</summary>
    public static int Choose(Window? owner, string title, string message, string safeLabel, params string[] actionLabels)
    {
        var chosen = -1;
        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (var index = 0; index < actionLabels.Length; index++)
        {
            var answer = index;
            var button = new System.Windows.Controls.Button { Content = actionLabels[index], MinWidth = 160, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(index == 0 ? 0 : 8, 0, 0, 0) };
            button.Click += (_, _) => { chosen = answer; dialog.Close(); };
            buttons.Children.Add(button);
        }
        var safeButton = new System.Windows.Controls.Button { Content = safeLabel, IsDefault = true, IsCancel = true, MinWidth = 160, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        safeButton.Click += (_, _) => dialog.Close();
        buttons.Children.Add(safeButton);
        var content = new System.Windows.Controls.StackPanel { Margin = new Thickness(20), MaxWidth = 560 };
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.ShowDialog();
        return chosen;
    }
}
