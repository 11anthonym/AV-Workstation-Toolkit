using AVWorkstationToolkit.App.Services;
using AVWorkstationToolkit.Infrastructure.Windows.Files;

namespace AVWorkstationToolkit.Tests;

/// <summary>
/// Package QA runs the packaged production smoke against a disposable data root. These tests pin the bound that root must
/// satisfy, and that production itself still accepts only the user's canonical data root.
/// </summary>
[TestClass]
public sealed class PackageQaIsolationTests
{
    private static string Temporary => Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    private static string Canonical => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductionRuntimePolicy.DataDirectoryName);

    [TestMethod]
    public void PackageQaDataRootIsADisposableFolderDirectlyBeneathTheTemporaryFolder()
    {
        var accepted = Path.Combine(Temporary, $"{ProductionRuntimePolicy.PackageQaDataRootPrefix}{Guid.NewGuid():N}");
        try
        {
            Assert.AreEqual(accepted, ProductionRuntimePolicy.RequirePackageQaDataRoot(accepted));
            Assert.IsTrue(Directory.Exists(accepted));
            Assert.Throws<IOException>(() => ProductionRuntimePolicy.RequireDataRoot(accepted), "Production actions still accept only the canonical root.");
        }
        finally
        {
            if (Directory.Exists(accepted)) Directory.Delete(accepted, recursive: true);
        }
    }

    [TestMethod]
    public void PackageQaDataRootRejectsTheUserProfileAndAnythingOutsideItsBound()
    {
        var insideProfile = Path.Combine(Canonical, $"{ProductionRuntimePolicy.PackageQaDataRootPrefix}{Guid.NewGuid():N}");
        var rejected = new[]
        {
            Canonical,
            insideProfile,
            Path.Combine(Temporary, ProductionRuntimePolicy.PackageQaDataRootPrefix + Guid.NewGuid().ToString("N").ToUpperInvariant()),
            Path.Combine(Temporary, $"{ProductionRuntimePolicy.PackageQaDataRootPrefix}{Guid.NewGuid():N}", "nested"),
            Path.Combine(Temporary, $"{ProductionRuntimePolicy.PackageQaDataRootPrefix}1234"),
            Path.Combine(Temporary, $"avwt-other-{Guid.NewGuid():N}"),
            $"{ProductionRuntimePolicy.PackageQaDataRootPrefix}{Guid.NewGuid():N}"
        };
        foreach (var path in rejected)
        {
            Assert.Throws<Exception>(() => ProductionRuntimePolicy.RequirePackageQaDataRoot(path), path);
            if (!string.Equals(path, Canonical, StringComparison.OrdinalIgnoreCase))
                Assert.IsFalse(Directory.Exists(path), $"A rejected root was created: {path}");
        }
    }

    [TestMethod]
    public void ProductionCompositionKeepsTheCanonicalRootAndTheSmokeKeepsItsIsolatedRoot()
    {
        var packageQaRoot = Path.Combine(Temporary, $"{ProductionRuntimePolicy.PackageQaDataRootPrefix}{Guid.NewGuid():N}");
        var applicationRoot = Path.Combine(packageQaRoot, "runtime", "1.1.3");
        try
        {
            // The ordinary packaged composition refuses a package QA root before it reads or writes anything.
            Assert.Throws<IOException>(() => CompiledAppComposition.CreateProduction(applicationRoot, packageQaRoot, "1.1.3", new string('A', 64)));
            Assert.IsFalse(Directory.Exists(packageQaRoot));
            // The production smoke refuses the user's profile.
            Assert.Throws<IOException>(() => CompiledAppComposition.CreateProduction(
                Path.Combine(Canonical, "runtime", "1.1.3"), Canonical, "1.1.3", new string('A', 64), packageQaSmoke: true));
        }
        finally
        {
            if (Directory.Exists(packageQaRoot)) Directory.Delete(packageQaRoot, recursive: true);
        }
    }
}
