using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Workstation;
using AVWorkstationToolkit.Domain.Workstation;
using AVWorkstationToolkit.Infrastructure.Windows.Migration;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// The migration checklist and deployment profiles are the feature's only persistent state. These tests keep every write
/// under a temporary root and pin what a failed or damaged file does: the last valid file survives, damage is reported
/// rather than read as "nothing saved", and temporary files never pass for saved state.
/// </summary>
[TestClass]
public sealed class MigrationPersistenceTests
{
    private static readonly string CanonicalSession = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVWorkstationToolkit", "migration", "session.json");

    [TestMethod]
    public async Task ChecklistLifecycleStaysInsideTheSuppliedDataRoot()
    {
        var canonicalBefore = FileState(CanonicalSession);
        var root = NewRoot();
        try
        {
            // The composition wires the checklist store to the data root it is given, as the read-only QA check relies on.
            var composition = CompiledAppComposition.Create(MigrationFixtures.RepositoryRoot(), root);
            Assert.AreEqual(Path.GetFullPath(root), composition.Migration.DataRoot);
            var sessionPath = Path.Combine(root, "migration", "session.json");

            var service = composition.Migration.Service;
            var started = service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
            Assert.IsTrue(File.Exists(sessionPath));

            // A restart reads the same file through a new composition, then compares it with this PC.
            var reopened = CompiledAppComposition.Create(MigrationFixtures.RepositoryRoot(), root).Migration.Service;
            Assert.AreEqual(started.SessionId, reopened.LoadSaved()!.SessionId);
            var target = await WorkstationMigrationTests.Target(
                [new RegistryUninstallRecord(RegistryInventorySource.Hklm32, "Crestron Toolbox 3.1390.0008.3", "3.1390.0008.3", "Crestron Electronics Inc.", "{1B52}_is1")],
                MigrationFixtures.WinGetResult(("7zip.7zip", "26.03")));
            Assert.IsTrue(reopened.Accept(target.Plan));
            var checklist = reopened.Checklist!;
            Assert.AreEqual(ChecklistStatus.Installed, checklist.Items.Single(item => item.Desired.Spec.DisplayName.StartsWith("Crestron Toolbox", StringComparison.Ordinal)).Status);

            var widget = checklist.Items.Single(item => item.Desired.Spec.DisplayName == "Vendor Widget Configuration Tool").ItemId;
            reopened.SetIncluded(widget, false);
            Assert.IsFalse(new MigrationSessionFileStore(root).Load()!.Items.Single(item => item.ItemId == widget).Included);

            reopened.Finish();
            Assert.IsFalse(File.Exists(sessionPath));
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "migration")).Any(), "No temporary file is left behind.");
            Assert.AreEqual(canonicalBefore, FileState(CanonicalSession), "The user's own saved checklist was touched.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void AFailedSaveKeepsTheLastValidChecklistAndLeavesNoTemporaryFile()
    {
        var root = NewRoot();
        var store = new MigrationSessionFileStore(root);
        var original = Session("original");
        store.Save(original);
        var saved = File.ReadAllBytes(store.SessionPath);
        var folder = Path.GetDirectoryName(store.SessionPath)!;
        try
        {
            // The destination can't be replaced while another process holds it.
            using (new FileStream(store.SessionPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                AssertRefused(() => store.Save(Session("replacement")));
            AssertIntact();

            // The folder can't be written at all.
            var user = WindowsIdentity.GetCurrent().User!;
            var deny = new FileSystemAccessRule(user, FileSystemRights.CreateFiles | FileSystemRights.WriteData, AccessControlType.Deny);
            var directory = new DirectoryInfo(folder);
            var security = directory.GetAccessControl();
            security.AddAccessRule(deny);
            directory.SetAccessControl(security);
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => store.Save(Session("replacement")));
            }
            finally
            {
                security = directory.GetAccessControl();
                security.RemoveAccessRule(deny);
                directory.SetAccessControl(security);
            }
            AssertIntact();
        }
        finally
        {
            DeleteRoot(root);
        }

        void AssertIntact()
        {
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(store.SessionPath), "A failed save changed the saved checklist.");
            Assert.AreEqual("original", store.Load()!.Source.Label);
            Assert.IsFalse(Directory.EnumerateFiles(folder, "*.tmp").Any(), "A failed save left a temporary file.");
        }
    }

    [TestMethod]
    public void DamagedOrUnsupportedChecklistsAreReportedNotReadAsEmpty()
    {
        var root = NewRoot();
        var store = new MigrationSessionFileStore(root);
        store.Save(Session("original"));
        var valid = File.ReadAllText(store.SessionPath);
        try
        {
            foreach (var (name, content) in new[]
            {
                ("empty", string.Empty),
                ("malformed", "{ \"schemaVersion\": 1, "),
                ("truncated", valid[..(valid.Length / 2)]),
                ("newer schema", valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)),
                ("wrong document", valid.Replace("\"migration-session\"", "\"workstation-profile\"", StringComparison.Ordinal))
            })
            {
                Assert.AreNotEqual(valid, content, name);
                File.WriteAllText(store.SessionPath, content);
                Assert.Throws<WorkstationDocumentException>(() => store.Load(), name);
                Assert.AreEqual(content, File.ReadAllText(store.SessionPath), $"Loading a {name} checklist changed the file.");
            }

            // The window reports the problem, keeps the file for the technician to discard, and starts with no checklist.
            var (viewModel, _) = ViewModelFor(store);
            viewModel.InitializeAsync(null).GetAwaiter().GetResult();
            Assert.IsFalse(viewModel.HasSession);
            StringAssert.Contains(viewModel.SavedSessionProblem, "couldn't be opened");
            Assert.IsTrue(viewModel.DiscardSavedCommand.CanExecute(null));
            Assert.IsTrue(File.Exists(store.SessionPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void AnInterruptedWriteNeverPassesForASavedChecklist()
    {
        var root = NewRoot();
        var store = new MigrationSessionFileStore(root);
        try
        {
            // A crash between writing the temporary file and replacing the checklist leaves only the temporary file.
            var folder = Path.Combine(root, "migration");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $".session.json.{Guid.NewGuid():N}.tmp"), MigrationSessionCodec.Serialize(Session("interrupted")));
            Assert.IsNull(store.Load());
            store.Save(Session("next"));
            Assert.AreEqual("next", store.Load()!.Source.Label);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void AFailedProfileSaveKeepsThePreviousProfileAndDamagedProfilesAreRejected()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "jump-pc.json");
        Directory.CreateDirectory(root);
        try
        {
            var files = new WpfMigrationFileService(root);
            files.Write(path, DeploymentProfileCodec.Serialize(WorkstationMigrationTests.JumpPc(3)));
            var saved = File.ReadAllBytes(path);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                AssertRefused(() => files.Write(path, DeploymentProfileCodec.Serialize(WorkstationMigrationTests.JumpPc(4))));
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(path));
            Assert.AreEqual(3, DeploymentProfileCodec.Parse(files.Read(path, DeploymentProfileCodec.MaximumBytes)).ProfileVersion);
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.tmp").Any());

            var text = Encoding.UTF8.GetString(saved);
            foreach (var damaged in new[] { text[..(text.Length / 2)], text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9", StringComparison.Ordinal), "[]" })
            {
                Assert.AreNotEqual(text, damaged);
                Assert.Throws<WorkstationDocumentException>(() => DeploymentProfileCodec.Parse(Encoding.UTF8.GetBytes(damaged)));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static MigrationSession Session(string label)
    {
        var session = MigrationSession.FromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()), $"migration-{label}", MigrationFixtures.Now);
        return session with { Source = session.Source with { Label = label } };
    }

    private static (MigrationViewModel ViewModel, WorkstationMigrationService Service) ViewModelFor(IMigrationSessionStore store)
    {
        var target = WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult()).GetAwaiter().GetResult();
        var service = new WorkstationMigrationService(new WorkstationMigrationTests.FixedPlanning(target.Plan), MigrationFixtures.InventoryService(), store,
            timeProvider: new WorkstationMigrationTests.FixedTime(MigrationFixtures.Now));
        return (new MigrationViewModel(service, new MigrationPresentationTests.FakeFiles(), "1.1.3"), service);
    }

    // Windows refuses to replace a file another process holds open with an access or sharing violation.
    private static void AssertRefused(Action write)
    {
        var failure = Assert.Throws<Exception>(write);
        Assert.IsTrue(failure is IOException or UnauthorizedAccessException, failure.ToString());
    }

    private static string FileState(string path) => File.Exists(path)
        ? $"{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"
        : "absent";

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"avwt-migration-store-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
