using System.IO;
using System.Windows;
using AVWorkstationToolkit.Infrastructure.Windows.Migration;
using Microsoft.Win32;

namespace AVWorkstationToolkit.App.Services;

/// <summary>File choices and confirmations for the migration window, kept behind an interface so the view model stays testable.</summary>
public interface IMigrationFileService : IActionConfirmation
{
    string? PickInventoryToSave(string suggestedName);
    string? PickInventoryToOpen();
    string? PickProfileToOpen();
    string? PickProfileToSave(string suggestedName);
    bool Confirm(string title, string message);

    /// <summary>
    /// Asks whether to keep the active checklist or replace it, with those two choices named. Returns true only when the
    /// technician chooses to replace it; keeping it is the default and the answer when the question is closed.
    /// </summary>
    bool ChooseToReplace(string title, string message, string keepLabel, string replaceLabel);

    byte[] Read(string path, int maximumBytes);
    void Write(string path, byte[] content);
}

public sealed class WpfMigrationFileService(string dataRoot, Window? owner = null) : IMigrationFileService
{
    private const string InventoryFilter = "Workstation inventory (*.json)|*.json";
    private const string ProfileFilter = "Workstation template (*.json)|*.json";
    private readonly string dataRoot = WorkstationDocumentFiles.RequireDataRoot(dataRoot);

    public Window? Owner { get; set; } = owner;

    public string? PickInventoryToSave(string suggestedName) => Save("Export workstation inventory", InventoryFilter,
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), suggestedName);

    public string? PickInventoryToOpen() => Open("Import a workstation inventory", InventoryFilter,
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    public string? PickProfileToOpen() => Open("Open a workstation template", ProfileFilter, WorkstationDocumentFiles.DataFolder(dataRoot, "templates"));

    public string? PickProfileToSave(string suggestedName) => Save("Save workstation template", ProfileFilter,
        WorkstationDocumentFiles.DataFolder(dataRoot, "templates"), suggestedName);

    public bool Confirm(string title, string message) =>
        (Owner is null ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes;

    // Keeping the checklist is the default button, the Escape key, and the answer when the dialog is closed.
    public bool ChooseToReplace(string title, string message, string keepLabel, string replaceLabel) =>
        ChoiceDialog.Ask(Owner, title, message, safeLabel: keepLabel, proceedLabel: replaceLabel);

    public bool ConfirmSystemImpact(SystemImpactPrompt prompt) => ChoiceDialog.ConfirmSystemImpact(Owner, prompt);

    public OpenAppsDecision ChooseForOpenApps(OpenAppsPrompt prompt) => ChoiceDialog.ChooseForOpenApps(Owner, prompt);

    public byte[] Read(string path, int maximumBytes) => WorkstationDocumentFiles.ReadBounded(path, maximumBytes);

    public void Write(string path, byte[] content)
    {
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Workstation documents must use a .json filename.");
        WorkstationDocumentFiles.WriteAtomic(path, content);
    }

    private string? Open(string title, string filter, string folder)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = folder, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(Owner) == true ? Path.GetFullPath(dialog.FileName) : null;
    }

    private string? Save(string title, string filter, string folder, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            AddExtension = true,
            DefaultExt = ".json",
            InitialDirectory = folder,
            FileName = suggestedName,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(Owner) == true ? Path.GetFullPath(dialog.FileName) : null;
    }
}
