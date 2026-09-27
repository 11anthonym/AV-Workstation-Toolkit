using System.Globalization;
using System.Windows;
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
        var verb = action == PackageAction.Update ? "Update" : "Install";
        var count = apps.Count == 1 ? "1 app" : $"{apps.Count.ToString(CultureInfo.CurrentCulture)} apps";
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
}

/// <summary>Asks a <see cref="SystemImpactPrompt"/>, kept behind an interface so the view models stay testable.</summary>
public interface ISystemImpactConfirmation
{
    /// <summary>
    /// Returns true only when the technician chooses to go ahead. Cancel is the default button, the Escape key, and the
    /// answer when the question is closed.
    /// </summary>
    bool ConfirmSystemImpact(SystemImpactPrompt prompt);
}

public sealed class WpfSystemImpactConfirmation(Window? owner = null) : ISystemImpactConfirmation
{
    public Window? Owner { get; set; } = owner;

    public bool ConfirmSystemImpact(SystemImpactPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return ChoiceDialog.Ask(Owner, prompt.Title, prompt.Message, safeLabel: "Cancel", proceedLabel: prompt.ProceedLabel);
    }
}

/// <summary>
/// A question with both answers named, which a message box can't do. The safe answer is the default button, the Escape
/// key, and the answer when the dialog is closed.
/// </summary>
internal static class ChoiceDialog
{
    public static bool Ask(Window? owner, string title, string message, string safeLabel, string proceedLabel)
    {
        var proceed = false;
        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        var safeButton = new System.Windows.Controls.Button { Content = safeLabel, IsDefault = true, IsCancel = true, MinWidth = 180, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        var proceedButton = new System.Windows.Controls.Button { Content = proceedLabel, MinWidth = 180, Padding = new Thickness(12, 6, 12, 6) };
        safeButton.Click += (_, _) => dialog.Close();
        proceedButton.Click += (_, _) => { proceed = true; dialog.Close(); };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(proceedButton);
        buttons.Children.Add(safeButton);
        var content = new System.Windows.Controls.StackPanel { Margin = new Thickness(20), MaxWidth = 520 };
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.ShowDialog();
        return proceed;
    }
}
