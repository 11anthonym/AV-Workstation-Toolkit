using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.App.ViewModels;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// Driver, service, and listener changes are confirmed once, when the technician starts an install or update, in a
/// question that names those apps. Nothing is ticked beforehand, so nothing clears itself while a run goes on.
/// </summary>
[TestClass]
public sealed class SystemImpactConfirmationTests
{
    [TestMethod]
    public void NothingIsAskedWhenNoAppMakesSystemLevelChanges()
    {
        Assert.IsNull(SystemImpactPrompt.For(PackageAction.Install, [("7-Zip", PackageRisk.None), ("Notepad++", PackageRisk.None)]));
        Assert.IsNull(SystemImpactPrompt.For(PackageAction.Update, []));
    }

    [TestMethod]
    public void TheQuestionNamesEachAppWithASystemLevelChangeAndWhatItMayDo()
    {
        var prompt = SystemImpactPrompt.For(PackageAction.Update,
            [("7-Zip", PackageRisk.None), ("Wireshark", PackageRisk.Driver), ("Dante Controller", PackageRisk.Service), ("Tftpd64", PackageRisk.Listener)])!;

        Assert.AreEqual("Confirm system changes", prompt.Title);
        Assert.AreEqual("Update 4 apps", prompt.ProceedLabel);
        StringAssert.StartsWith(prompt.Message, "Update 4 apps? 3 of them make system-level changes to this PC:");
        StringAssert.Contains(prompt.Message, "Wireshark — may install a driver");
        StringAssert.Contains(prompt.Message, "Dante Controller — may add a background service");
        StringAssert.Contains(prompt.Message, "Tftpd64 — may accept network connections");
        Assert.DoesNotContain("7-Zip", prompt.Message);
        StringAssert.EndsWith(prompt.Message, "Each app is checked again before it starts.");
    }

    [TestMethod]
    public void TheQuestionReadsNaturallyForOneAppOrWhenEveryAppHasAChange()
    {
        var one = SystemImpactPrompt.For(PackageAction.Install, [("Wireshark", PackageRisk.Driver)])!;
        StringAssert.StartsWith(one.Message, "Install 1 app? It makes system-level changes to this PC:");
        Assert.AreEqual("Install 1 app", one.ProceedLabel);

        var all = SystemImpactPrompt.For(PackageAction.Install, [("Wireshark", PackageRisk.Driver), ("Tftpd64", PackageRisk.Listener)])!;
        StringAssert.StartsWith(all.Message, "Install 2 apps? They make system-level changes to this PC:");

        var single = SystemImpactPrompt.For(PackageAction.Install, [("7-Zip", PackageRisk.None), ("Wireshark", PackageRisk.Driver)])!;
        StringAssert.StartsWith(single.Message, "Install 2 apps? 1 of them makes system-level changes to this PC:");
    }

    [TestMethod]
    public void AMassUpdateListsTheFirstEightAppsAndCountsTheRest()
    {
        var apps = Enumerable.Range(1, 11).Select(index => ($"Driver app {index:00}", PackageRisk.Driver)).ToArray();
        var prompt = SystemImpactPrompt.For(PackageAction.Update, apps)!;

        StringAssert.Contains(prompt.Message, "Driver app 08 — may install a driver");
        Assert.DoesNotContain("Driver app 09", prompt.Message);
        StringAssert.Contains(prompt.Message, "and 3 more");
        Assert.AreEqual("Update 11 apps", prompt.ProceedLabel);
    }

    [TestMethod]
    public async Task TheMainWindowAsksOnceWhenTheInstallStartsAndPassesTheAnswerForThatRunOnly()
    {
        var harness = await MainWindowHarness.CreateAsync(answer: true);
        using var viewModel = harness.ViewModel;
        var wireshark = viewModel.Packages.Single(item => item.Id == "WiresharkFoundation.Wireshark");
        var sevenZip = viewModel.Packages.Single(item => item.Id == "7zip.7zip");
        wireshark.Selected = true;
        sevenZip.Selected = true;

        Assert.IsTrue(viewModel.CanInstall, "Nothing has to be ticked before Install is available.");
        Assert.IsTrue(viewModel.SystemImpactNoteVisible);
        Assert.AreEqual("1 selected app makes system-level changes. You'll be asked to confirm when you start.", viewModel.SystemImpactNote);

        var running = new TaskCompletionSource();
        harness.Actions.Running = running;
        viewModel.InstallCommand.Execute(null);
        await Until(() => harness.Actions.Request is not null && !viewModel.CanInstall);

        // While the run goes on there is nothing to tick again, and no second question.
        Assert.IsFalse(viewModel.SystemImpactNoteVisible);
        Assert.HasCount(1, harness.Confirmation.Prompts);
        var prompt = harness.Confirmation.Prompts[0];
        StringAssert.StartsWith(prompt.Message, "Install 2 apps? 1 of them makes system-level changes to this PC:");
        StringAssert.Contains(prompt.Message, $"{wireshark.Name} — may install a driver");
        Assert.AreEqual("Install 2 apps", prompt.ProceedLabel);
        CollectionAssert.AreEquivalent(new[] { "7zip.7zip", "WiresharkFoundation.Wireshark" }, harness.Actions.Request!.PackageIds.ToArray());
        Assert.IsTrue(harness.Actions.Request.RiskAcknowledged);

        running.SetResult();
        await Until(() => viewModel.SelectedCount == 0 && viewModel.ActionSnapshot.State is not (CompiledActionState.Preparing or CompiledActionState.Running));
        Assert.HasCount(1, harness.Confirmation.Prompts);
        Assert.IsFalse(viewModel.SystemImpactNoteVisible);
    }

    [TestMethod]
    public async Task CancellingTheQuestionStartsNothingAndKeepsTheSelection()
    {
        var harness = await MainWindowHarness.CreateAsync(answer: false);
        using var viewModel = harness.ViewModel;
        viewModel.Packages.Single(item => item.Id == "WiresharkFoundation.Wireshark").Selected = true;

        viewModel.InstallCommand.Execute(null);
        await Until(() => viewModel.ActivityText.Contains("Install cancelled.", StringComparison.Ordinal));

        Assert.HasCount(1, harness.Confirmation.Prompts);
        Assert.IsNull(harness.Actions.Request, "A cancelled confirmation reaches no worker.");
        StringAssert.Contains(viewModel.ActivityText, "Install cancelled. Nothing was changed.");
        Assert.AreEqual(1, viewModel.InstallCount);
        Assert.IsTrue(viewModel.CanInstall, "The technician can start again and will be asked again.");
    }

    [TestMethod]
    public async Task WithoutAWayToAskARiskBearingInstallDoesNotStart()
    {
        var harness = await MainWindowHarness.CreateAsync(answer: true, withConfirmation: false);
        using var viewModel = harness.ViewModel;
        viewModel.Packages.Single(item => item.Id == "WiresharkFoundation.Wireshark").Selected = true;

        viewModel.InstallCommand.Execute(null);
        await Until(() => viewModel.ActivityText.Contains("Install cancelled.", StringComparison.Ordinal));

        Assert.IsNull(harness.Actions.Request);
    }

    [TestMethod]
    public async Task InstallingOnlyAppsWithoutSystemChangesAsksNothingAndClaimsNoAcknowledgement()
    {
        var harness = await MainWindowHarness.CreateAsync(answer: false);
        using var viewModel = harness.ViewModel;
        viewModel.Packages.Single(item => item.Id == "7zip.7zip").Selected = true;
        Assert.IsFalse(viewModel.SystemImpactNoteVisible);

        viewModel.InstallCommand.Execute(null);
        await Until(() => harness.Actions.Request is not null && viewModel.SelectedCount == 0);

        Assert.IsEmpty(harness.Confirmation.Prompts);
        Assert.IsFalse(harness.Actions.Request!.RiskAcknowledged);
    }

    internal sealed class RecordingConfirmation(bool answer, OpenAppsDecision openAppsAnswer = OpenAppsDecision.Cancel) : IActionConfirmation
    {
        public List<SystemImpactPrompt> Prompts { get; } = [];
        public List<OpenAppsPrompt> OpenAppsPrompts { get; } = [];
        public bool ConfirmSystemImpact(SystemImpactPrompt prompt)
        {
            Prompts.Add(prompt);
            return answer;
        }

        public OpenAppsDecision ChooseForOpenApps(OpenAppsPrompt prompt)
        {
            OpenAppsPrompts.Add(prompt);
            return openAppsAnswer;
        }
    }

    internal sealed record MainWindowHarness(MainWindowViewModel ViewModel, RecordingConfirmation Confirmation, WorkstationMigrationTests.RecordingActionStore Actions)
    {
        public static async Task<MainWindowHarness> CreateAsync(bool answer, bool withConfirmation = true,
            OpenAppsDecision openAppsAnswer = OpenAppsDecision.Cancel, IOpenApplicationService? openApplications = null)
        {
            var before = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult());
            var after = await WorkstationMigrationTests.Target([], MigrationFixtures.WinGetResult(("7zip.7zip", "26.03"), ("WiresharkFoundation.Wireshark", "4.4.0")));
            var planning = new MigrationPresentationTests.QueuePlanning(before.Plan, after.Plan);
            var actions = new WorkstationMigrationTests.RecordingActionStore();
            var confirmation = new RecordingConfirmation(answer, openAppsAnswer);
            var viewModel = new MainWindowViewModel(planning, actionCoordinator: Coordinator(actions, planning), searchDebounce: TimeSpan.Zero,
                actionConfirmation: withConfirmation ? confirmation : null, openApplications: openApplications);
            await viewModel.RefreshAsync();
            return new MainWindowHarness(viewModel, confirmation, actions);
        }
    }

    private static CompiledActionCoordinator Coordinator(WorkstationMigrationTests.RecordingActionStore actions, AVWorkstationToolkit.Application.Planning.IWorkstationPlanningCoordinator planning) =>
        new(actions, new WorkstationMigrationTests.ImmediateLauncher(), planning, pollInterval: TimeSpan.FromMilliseconds(1), resultTimeout: TimeSpan.FromSeconds(10));

    internal static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Assert.IsTrue(condition(), "The expected state was not reached.");
    }
}
