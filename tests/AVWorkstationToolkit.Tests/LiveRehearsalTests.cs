using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Application.Actions;
using AVWorkstationToolkit.Infrastructure.Windows.Catalog;
using AVWorkstationToolkit.Infrastructure.Windows.Files;
using AVWorkstationToolkit.Infrastructure.Windows.Processes;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class LiveRehearsalTests
{
    [TestMethod]
    public void LiveRootMustBeAnExplicitDirectTemporaryChild()
    {
        var ordinary = Path.Combine(Path.GetTempPath(), $"wrong-{Guid.NewGuid():N}");
        var nestedParent = Path.Combine(Path.GetTempPath(), $"{LiveRehearsalRootPolicy.DirectoryPrefix}{Guid.NewGuid():N}");
        var nested = Path.Combine(nestedParent, $"{LiveRehearsalRootPolicy.DirectoryPrefix}nested");
        Directory.CreateDirectory(ordinary);
        Directory.CreateDirectory(nested);
        try
        {
            Assert.Throws<IOException>(() => LiveRehearsalRootPolicy.RequireExisting(ordinary));
            Assert.Throws<IOException>(() => LiveRehearsalRootPolicy.RequireExisting(nested));
        }
        finally
        {
            if (Directory.Exists(ordinary)) Directory.Delete(ordinary, recursive: true);
            if (Directory.Exists(nestedParent)) Directory.Delete(nestedParent, recursive: true);
        }
    }

    [TestMethod]
    public void LiveLauncherUsesOnlyTheExactWorkerAndFixedArguments()
    {
        var root = CreateRoot();
        try
        {
            var requestId = "request-20260830-120000-a1b2c3d4";
            var paths = new ActionArtifactPathPolicy().GetPaths(root, requestId);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.RequestPath)!);
            File.WriteAllText(paths.RequestPath, "{}");
            var repository = RepositoryRootLocator.Find();
            var start = new CompiledLiveRehearsalWorkerLauncher(repository, root).CreateStartInfo(paths);

            Assert.AreEqual("AVWorkstationToolkit.Worker.exe", Path.GetFileName(start.FileName));
            Assert.IsFalse(start.UseShellExecute);
            Assert.IsTrue(start.CreateNoWindow);
            CollectionAssert.AreEqual(new[]
            {
                "--live-rehearsal", "--root", root, "--request", paths.RequestPath, "--repository-root", repository
            }, start.ArgumentList.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void NormalCompiledCompositionRemainsReadOnlyWhileLiveModeIsExplicit()
    {
        var root = CreateRoot();
        try
        {
            var repository = RepositoryRootLocator.Find();
            var normal = CompiledAppComposition.Create(repository);
            var live = CompiledAppComposition.CreateLiveRehearsal(repository, root);

            Assert.IsNull(normal.Actions);
            Assert.IsFalse(normal.IsLiveRehearsal);
            Assert.IsNotNull(live.Actions);
            Assert.IsTrue(live.IsLiveRehearsal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"{LiveRehearsalRootPolicy.DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
