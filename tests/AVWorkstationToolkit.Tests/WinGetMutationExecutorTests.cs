using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Workers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;
using AVWorkstationToolkit.Infrastructure.Windows.WinGet;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class WinGetMutationExecutorTests
{
    [TestMethod]
    public void ArgumentPolicyMatchesShippingInstallAndUpdateVectors()
    {
        var install = ManagedWinGetArgumentPolicy.Create(Request(ManagedRequestAction.Install, PackageRisk.None));
        CollectionAssert.AreEqual(new[]
        {
            "install", "--id", "Vendor.Tool", "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements", "--silent", "--disable-interactivity"
        }, install.ToArray());

        var update = ManagedWinGetArgumentPolicy.Create(Request(ManagedRequestAction.Update, PackageRisk.Service));
        CollectionAssert.AreEqual(new[]
        {
            "upgrade", "--id", "Vendor.Tool", "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements"
        }, update.ToArray());

        // Only a no-risk package may run unattended. Every risk-bearing class stays interactive for
        // both actions, and no vector may become bulk, an uninstall, or an import.
        foreach (var action in new[] { ManagedRequestAction.Install, ManagedRequestAction.Update })
        {
            var verb = action == ManagedRequestAction.Install ? "install" : "upgrade";
            var lowRisk = ManagedWinGetArgumentPolicy.Create(Request(action, PackageRisk.None));
            Assert.AreEqual(verb, lowRisk[0]);
            CollectionAssert.Contains(lowRisk.ToArray(), "--silent");
            CollectionAssert.Contains(lowRisk.ToArray(), "--disable-interactivity");

            foreach (var risk in new[] { PackageRisk.Driver, PackageRisk.Service, PackageRisk.Listener })
            {
                var risky = ManagedWinGetArgumentPolicy.Create(Request(action, risk)).ToArray();
                Assert.AreEqual(verb, risky[0]);
                CollectionAssert.DoesNotContain(risky, "--silent");
                CollectionAssert.DoesNotContain(risky, "--disable-interactivity");
                CollectionAssert.AreEqual(new[] { "--id", "Vendor.Tool", "--exact", "--source", "winget" }, risky[1..6]);
                StringAssert.DoesNotMatch(string.Join(' ', risky), new System.Text.RegularExpressions.Regex(@"(?i)\b(?:uninstall|import)\b|--all"));
            }
        }

        var combined = string.Join(' ', install.Concat(update));
        StringAssert.DoesNotMatch(combined, new System.Text.RegularExpressions.Regex(@"(?i)\b(?:uninstall|import)\b|--all"));
        Assert.Throws<ActionRequestValidationException>(() => ManagedWinGetArgumentPolicy.Create(Request(id: "bad id")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManagedWinGetArgumentPolicy.Create(Request((ManagedRequestAction)999)));
    }

    [TestMethod]
    public async Task RunnerUsesOnlyTrustedResolverPathAndReviewedArguments()
    {
        var trustedPath = ExpectedWinGetPath();
        var host = new CapturingHost(new(0, "ok", string.Empty, false, false, false, ProviderFailureKind.None));
        var runner = new WinGetMutationProcessRunner(
            new StubResolver(new(true, trustedPath, ProviderFailureKind.None, "trusted")),
            host,
            TimeSpan.FromMinutes(1),
            () => false);

        var result = await runner.RunAsync(Request());

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(trustedPath, host.Executable);
        CollectionAssert.AreEqual(ManagedWinGetArgumentPolicy.Create(Request()).ToArray(), host.Arguments.ToArray());
        Assert.AreEqual(TimeSpan.FromMinutes(1), host.Timeout);
    }

    [TestMethod]
    public async Task RunnerFailsClosedBeforeHostForLookalikeOrElevation()
    {
        var host = new CapturingHost(new(0, string.Empty, string.Empty, false, false, false, ProviderFailureKind.None));
        var lookalike = new WinGetMutationProcessRunner(
            new StubResolver(new(true, @"C:\Temp\winget.exe", ProviderFailureKind.None, "lookalike")),
            host,
            TimeSpan.FromMinutes(1),
            () => false);
        var trustFailure = await lookalike.RunAsync(Request());
        Assert.AreEqual(ProviderFailureKind.TrustFailure, trustFailure.Failure);
        Assert.AreEqual(0, host.CallCount);

        var elevated = new WinGetMutationProcessRunner(
            new StubResolver(new(true, ExpectedWinGetPath(), ProviderFailureKind.None, "trusted")),
            host,
            TimeSpan.FromMinutes(1),
            () => true);
        var elevationFailure = await elevated.RunAsync(Request());
        Assert.AreEqual(ProviderFailureKind.TrustFailure, elevationFailure.Failure);
        Assert.AreEqual(0, host.CallCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WinGetMutationProcessRunner(
            new StubResolver(new(false, string.Empty, ProviderFailureKind.TrustFailure, "none")),
            host,
            TimeSpan.FromMinutes(31),
            () => false));
    }

    [TestMethod]
    public async Task ExecutorMapsExitAndTimeoutWithoutLaunchingAProcess()
    {
        var runner = new StubMutationRunner(
            new(0, "installed", string.Empty, false, false, false, ProviderFailureKind.None),
            new(23, string.Empty, "failed", false, false, false, ProviderFailureKind.ExecutionFailed),
            new(-1, string.Empty, string.Empty, true, false, false, ProviderFailureKind.TimedOut));
        var executor = new WinGetPackageActionExecutor(runner);

        var success = await executor.ExecuteAsync(Request());
        var failure = await executor.ExecuteAsync(Request());
        var timeout = await executor.ExecuteAsync(Request());

        Assert.AreEqual(PackageExecutionDisposition.Succeeded, success.Disposition);
        Assert.AreEqual("installed", success.StandardOutput);
        Assert.AreEqual(PackageExecutionDisposition.Failed, failure.Disposition);
        Assert.AreEqual(23, failure.ExitCode);
        Assert.AreEqual(PackageExecutionDisposition.TimedOut, timeout.Disposition);
        Assert.AreEqual(-1, timeout.ExitCode);
    }

    private static PackageExecutionRequest Request(
        ManagedRequestAction action = ManagedRequestAction.Install,
        PackageRisk risk = PackageRisk.None,
        string id = "Vendor.Tool") => new(id, "Vendor Tool", action, risk);

    private static string ExpectedWinGetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "WindowsApps",
        "Microsoft.DesktopAppInstaller_1.29.0.0_x64__8wekyb3d8bbwe",
        "winget.exe");

    private sealed class StubResolver(TrustedWinGetResolution result) : IWinGetResolver
    {
        public Task<TrustedWinGetResolution> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class CapturingHost(WinGetMutationProcessResult result) : WinGetMutationProcessRunner.IWinGetMutationProcessHost
    {
        public int CallCount { get; private set; }
        public string Executable { get; private set; } = string.Empty;
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public TimeSpan Timeout { get; private set; }

        public Task<WinGetMutationProcessResult> RunAsync(
            string trustedExecutablePath,
            IReadOnlyList<string> reviewedArguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Executable = trustedExecutablePath;
            Arguments = reviewedArguments.ToArray();
            Timeout = timeout;
            return Task.FromResult(result);
        }
    }

    private sealed class StubMutationRunner(params WinGetMutationProcessResult[] results) : IWinGetMutationProcessRunner
    {
        private int index;

        public Task<WinGetMutationProcessResult> RunAsync(PackageExecutionRequest request, CancellationToken cancellationToken = default)
        {
            if (index >= results.Length) throw new InvalidOperationException("Mutation result sequence exhausted.");
            return Task.FromResult(results[index++]);
        }
    }
}
