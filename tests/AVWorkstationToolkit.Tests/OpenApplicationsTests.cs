using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// An installer can refuse to run while its app is open, which is why ShareX 21.0.0's Inno Setup installer exited with
/// code 1 whenever ShareX was running. Open apps are named before a run; they are closed only with the technician's
/// consent, only by asking, and only just before their own installer. Apps are found by the program files they run,
/// located from their uninstall registration, never by process name.
/// </summary>
[TestClass]
public sealed class OpenApplicationsTests
{
    [TestMethod]
    public void TheQuestionNamesTheOpenAppsAndSaysNothingIsForced()
    {
        var prompt = OpenAppsPrompt.For(PackageAction.Update, 5,
            [("ShareX", (IReadOnlyList<string>)["ShareX"]), ("Git", ["Git Bash"])])!;

        Assert.AreEqual("Close open apps", prompt.Title);
        StringAssert.StartsWith(prompt.Message, "Update 5 apps? 2 of them are open, and an installer can fail while its app is running:");
        StringAssert.Contains(prompt.Message, "  •  ShareX" + Environment.NewLine);
        StringAssert.Contains(prompt.Message, "  •  Git (Git Bash)");
        StringAssert.Contains(prompt.Message, "just before its update, the way Windows does when you sign out. Nothing is forced");
        Assert.AreEqual("Close apps and continue", prompt.CloseLabel);
        Assert.AreEqual("Skip open apps", prompt.SkipLabel);

        var one = OpenAppsPrompt.For(PackageAction.Install, 1, [("ShareX", (IReadOnlyList<string>)["ShareX"])])!;
        StringAssert.StartsWith(one.Message, "Install 1 app? It's open, and an installer can fail while its app is running:");
        Assert.AreEqual("Close app and continue", one.CloseLabel);
        Assert.AreEqual("Skip this app", one.SkipLabel);

        Assert.IsNull(OpenAppsPrompt.For(PackageAction.Install, 2, [("ShareX", (IReadOnlyList<string>)[])]));
    }

    [TestMethod]
    public async Task ChoosingToCloseGivesConsentForExactlyTheOpenApps()
    {
        var open = new FixedOpenApplications(new() { ["WiresharkFoundation.Wireshark"] = ["Wireshark"] });
        var harness = await SystemImpactConfirmationTests.MainWindowHarness.CreateAsync(answer: true, openAppsAnswer: OpenAppsDecision.Close, openApplications: open);
        using var viewModel = harness.ViewModel;
        viewModel.Packages.Single(item => item.Id == "WiresharkFoundation.Wireshark").Selected = true;
        viewModel.Packages.Single(item => item.Id == "7zip.7zip").Selected = true;

        viewModel.InstallCommand.Execute(null);
        await SystemImpactConfirmationTests.Until(() => harness.Actions.Request is not null && viewModel.SelectedCount == 0);

        Assert.HasCount(1, harness.Confirmation.OpenAppsPrompts, "The open apps are named once, before the run.");
        CollectionAssert.AreEquivalent(new[] { "7zip.7zip", "WiresharkFoundation.Wireshark" }, harness.Actions.Request!.PackageIds.ToArray());
        CollectionAssert.AreEqual(new[] { "WiresharkFoundation.Wireshark" }, harness.Actions.Request.CloseOpenAppsFor.ToArray());
        Assert.HasCount(1, harness.Confirmation.Prompts, "The system-impact question still follows for the apps that run.");
    }

    [TestMethod]
    public async Task SkippingLeavesTheOpenAppsOutAndChangesTheRest()
    {
        var open = new FixedOpenApplications(new() { ["WiresharkFoundation.Wireshark"] = ["Wireshark"] });
        var harness = await SystemImpactConfirmationTests.MainWindowHarness.CreateAsync(answer: true, openAppsAnswer: OpenAppsDecision.Skip, openApplications: open);
        using var viewModel = harness.ViewModel;
        viewModel.Packages.Single(item => item.Id == "WiresharkFoundation.Wireshark").Selected = true;
        viewModel.Packages.Single(item => item.Id == "7zip.7zip").Selected = true;

        viewModel.InstallCommand.Execute(null);
        await SystemImpactConfirmationTests.Until(() => harness.Actions.Request is not null && viewModel.SelectedCount == 0);

        CollectionAssert.AreEqual(new[] { "7zip.7zip" }, harness.Actions.Request!.PackageIds.ToArray());
        Assert.IsEmpty(harness.Actions.Request.CloseOpenAppsFor);
        Assert.IsEmpty(harness.Confirmation.Prompts, "Only the skipped app had a system-level change, so nothing else needed confirming.");
        StringAssert.Contains(viewModel.ActivityText, "because it's open.");
    }

    [TestMethod]
    public async Task CancellingTheOpenAppsQuestionStartsNothing()
    {
        var open = new FixedOpenApplications(new() { ["7zip.7zip"] = ["7-Zip File Manager"] });
        var harness = await SystemImpactConfirmationTests.MainWindowHarness.CreateAsync(answer: true, openAppsAnswer: OpenAppsDecision.Cancel, openApplications: open);
        using var viewModel = harness.ViewModel;
        viewModel.Packages.Single(item => item.Id == "7zip.7zip").Selected = true;

        viewModel.InstallCommand.Execute(null);
        await SystemImpactConfirmationTests.Until(() => viewModel.ActivityText.Contains("Install cancelled.", StringComparison.Ordinal));

        Assert.IsNull(harness.Actions.Request);
        StringAssert.Contains(harness.Confirmation.OpenAppsPrompts.Single().Message, "  •  7-Zip (7-Zip File Manager)");
        Assert.AreEqual(1, viewModel.InstallCount);
    }

    [TestMethod]
    public async Task WithNoAppOpenNothingIsAskedAndNoConsentIsSent()
    {
        var harness = await SystemImpactConfirmationTests.MainWindowHarness.CreateAsync(answer: true, openAppsAnswer: OpenAppsDecision.Close,
            openApplications: new FixedOpenApplications([]));
        using var viewModel = harness.ViewModel;
        viewModel.Packages.Single(item => item.Id == "7zip.7zip").Selected = true;

        viewModel.InstallCommand.Execute(null);
        await SystemImpactConfirmationTests.Until(() => harness.Actions.Request is not null && viewModel.SelectedCount == 0);

        Assert.IsEmpty(harness.Confirmation.OpenAppsPrompts);
        Assert.IsEmpty(harness.Actions.Request!.CloseOpenAppsFor);
    }

    [TestMethod]
    public async Task TheMigrationWindowAsksTheSameQuestionAndPassesTheSameConsent()
    {
        var before = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
        var after = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03"), ("WiresharkFoundation.Wireshark", "4.4.0")));
        foreach (var decision in new[] { OpenAppsDecision.Close, OpenAppsDecision.Skip, OpenAppsDecision.Cancel })
        {
            var actions = new WorkstationMigrationTests.RecordingActionStore();
            var files = new MigrationPresentationTests.FakeFiles { OpenAppsAnswer = decision };
            var (viewModel, service, _) = MigrationPresentationTests.Create(new MigrationPresentationTests.QueuePlanning(after.Plan), files, actions,
                new FixedOpenApplications(new() { ["7zip.7zip"] = ["7-Zip File Manager"] }));
            service.StartFromInventory(WorkstationMigrationTests.Exported(WorkstationMigrationTests.SourceInventory()));
            await viewModel.InitializeAsync(before.Plan);

            await viewModel.InstallAsync(viewModel.VisibleItems.Where(item => item.CanInstall).ToArray());

            Assert.HasCount(1, files.OpenAppsPrompts, decision.ToString());
            switch (decision)
            {
                case OpenAppsDecision.Close:
                    CollectionAssert.AreEquivalent(new[] { "7zip.7zip", "WiresharkFoundation.Wireshark" }, actions.Request!.PackageIds.ToArray());
                    CollectionAssert.AreEqual(new[] { "7zip.7zip" }, actions.Request.CloseOpenAppsFor.ToArray());
                    break;
                case OpenAppsDecision.Skip:
                    CollectionAssert.AreEqual(new[] { "WiresharkFoundation.Wireshark" }, actions.Request!.PackageIds.ToArray());
                    Assert.IsEmpty(actions.Request.CloseOpenAppsFor);
                    StringAssert.Contains(viewModel.Status, "because it's open. Installer result:");
                    break;
                default:
                    Assert.IsNull(actions.Request);
                    Assert.AreEqual("Install cancelled. Nothing was installed.", viewModel.Status);
                    break;
            }
        }
    }

    [TestMethod]
    public void OnlyASpecificExistingLocalProgramFolderIsSearched()
    {
        var root = Path.Combine(Path.GetTempPath(), "avwt-open-apps-" + Guid.NewGuid().ToString("N"));
        var app = Directory.CreateDirectory(Path.Combine(root, "Vendor App")).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(app, "App.exe"), []);
            File.WriteAllBytes(Path.Combine(app, "Helper.exe"), []);
            File.WriteAllBytes(Path.Combine(app, "Library.dll"), []);
            Directory.CreateDirectory(Path.Combine(app, "tools"));
            File.WriteAllBytes(Path.Combine(app, "tools", "Nested.exe"), []);

            Assert.AreEqual(app, RegistryInstalledProgramLocator.ProgramFolder(app + @"\"));
            Assert.AreEqual(app, RegistryInstalledProgramLocator.ProgramFolder($"\"{app}\""));
            CollectionAssert.AreEquivalent(new[] { Path.Combine(app, "App.exe"), Path.Combine(app, "Helper.exe") },
                RegistryInstalledProgramLocator.ProgramsFrom(app, null).ToArray(), "Only the executables directly inside InstallLocation.");

            foreach (var rejected in new[]
            {
                null, "", "   ", @"relative\folder", @"\\server\share\App", @"\\?\C:\App", "C:\\", "C:",
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(root, "missing")
            })
                Assert.IsNull(RegistryInstalledProgramLocator.ProgramFolder(rejected), rejected ?? "(null)");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void TheDisplayIconNamesAProgramOnlyWhenItIsAnExistingLocalExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "avwt-open-apps-" + Guid.NewGuid().ToString("N"));
        var app = Directory.CreateDirectory(Path.Combine(root, "Vendor App")).FullName;
        try
        {
            var executable = Path.Combine(app, "App.exe");
            File.WriteAllBytes(executable, []);
            File.WriteAllBytes(Path.Combine(app, "App.ico"), []);

            Assert.AreEqual(executable, RegistryInstalledProgramLocator.IconProgram(executable));
            Assert.AreEqual(executable, RegistryInstalledProgramLocator.IconProgram($"\"{executable}\",0"));
            Assert.AreEqual(executable, RegistryInstalledProgramLocator.IconProgram($"{executable},-101"));
            foreach (var rejected in new[]
            {
                null, "", Path.Combine(app, "App.ico"), Path.Combine(app, "Missing.exe"), @"\\server\share\App.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "msiexec.exe")
            })
                Assert.IsNull(RegistryInstalledProgramLocator.IconProgram(rejected), rejected ?? "(null)");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void APackageWithoutARegisteredNameOrProgramsFindsNothingAndClosesNothing()
    {
        Assert.IsEmpty(new RegistryInstalledProgramLocator(new Dictionary<string, string>()).Locate("ShareX.ShareX"));

        var service = new RestartManagerOpenApplications(new FixedLocator([]));
        Assert.IsEmpty(service.FindOpen("ShareX.ShareX"));
        using var closure = service.Close("ShareX.ShareX");
        Assert.IsEmpty(closure.Closed);
        Assert.IsEmpty(closure.StillOpen);
        Assert.IsEmpty(closure.Reopen());
    }

    [TestMethod]
    public void AProgramNobodyIsRunningIsNotOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "avwt-open-apps-" + Guid.NewGuid().ToString("N"));
        var app = Directory.CreateDirectory(Path.Combine(root, "Vendor App")).FullName;
        try
        {
            var executable = Path.Combine(app, "App.exe");
            File.WriteAllBytes(executable, []);
            var service = new RestartManagerOpenApplications(new FixedLocator([executable]));
            Assert.IsEmpty(service.FindOpen("Vendor.App"), "Restart Manager finds nothing using a program no process runs.");
            using var closure = service.Close("Vendor.App");
            Assert.IsEmpty(closure.Closed);
            Assert.IsEmpty(closure.StillOpen);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void OpenAppNamesReadAsAPlainList()
    {
        Assert.AreEqual("ShareX", OpenApplicationText.Names(["ShareX", "sharex"]));
        Assert.AreEqual("ShareX and Git Bash", OpenApplicationText.Names(["ShareX", "Git Bash"]));
        Assert.AreEqual("A, B, and C", OpenApplicationText.Names(["A", "B", "C"]));
        Assert.IsFalse(OpenApplicationText.IsPlural(["ShareX", "SHAREX"]));
        Assert.IsTrue(OpenApplicationText.IsPlural(["A", "B"]));
    }

    internal sealed class FixedOpenApplications(Dictionary<string, string[]> open) : IOpenApplicationService
    {
        public IReadOnlyList<string> FindOpen(string packageId) => open.TryGetValue(packageId, out var names) ? names : [];

        public IOpenApplicationClosure Close(string packageId) =>
            throw new InvalidOperationException("The app never closes anything itself; only the worker does.");
    }

    private sealed class FixedLocator(IReadOnlyList<string> programs) : IInstalledProgramLocator
    {
        public IReadOnlyList<string> Locate(string packageId) => programs;
    }
}
