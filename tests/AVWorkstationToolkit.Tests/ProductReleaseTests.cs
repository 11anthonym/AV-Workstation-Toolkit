using AVWorkstationToolkit.Application.Diagnostics;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ProductReleaseTests
{
    [TestMethod]
    [DataRow("1.1.3-rc.1+a1b2c3", "rc.1", "1.1.3-rc.1", "1.1.3 RC 1")]
    [DataRow("1.1.3-rc.10+a1b2c3", "rc.10", "1.1.3-rc.10", "1.1.3 RC 10")]
    [DataRow("1.1.3-rc.2", "rc.2", "1.1.3-rc.2", "1.1.3 RC 2")]
    [DataRow("1.1.3+a1b2c3", "", "1.1.3", "1.1.3")]
    [DataRow(null, "", "1.1.3", "1.1.3")]
    public void InformationalVersionYieldsOnlyALabelThatExtendsTheNumericVersion(string? informational, string prerelease, string semantic, string display)
    {
        var release = ProductRelease.FromInformationalVersion("1.1.3", informational);

        Assert.AreEqual("1.1.3", release.Version);
        Assert.AreEqual(prerelease, release.Prerelease);
        Assert.AreEqual(semantic, release.SemanticVersion);
        Assert.AreEqual(display, release.DisplayVersion);
    }

    [TestMethod]
    [DataRow("1.1.4-rc.1+a1b2c3")]
    [DataRow("1.1.3-rc+a1b2c3")]
    [DataRow("1.1.3-rc.0")]
    [DataRow("1.1.3-RC.2")]
    [DataRow("1.1.3-preview.2")]
    [DataRow("1.1.3-rc.2.1")]
    [DataRow("1.1.3-rc.1000")]
    [DataRow("1.1.3--rc.2")]
    // The historical 1.1.1 betas were built by their own tagged sources; this source builds only release candidates.
    [DataRow("1.1.3-beta.2+a1b2c3")]
    [DataRow("1.1.3-alpha.1")]
    public void UnrecognizedOrMismatchedLabelsAreIgnored(string informational)
    {
        var release = ProductRelease.FromInformationalVersion("1.1.3", informational);

        Assert.AreEqual(string.Empty, release.Prerelease);
        Assert.AreEqual("1.1.3", release.DisplayVersion);
    }

    [TestMethod]
    [DataRow("rc")]
    [DataRow("rc.0")]
    [DataRow("nightly.1")]
    [DataRow("rc.2+a1b2c3")]
    [DataRow("beta.2")]
    [DataRow("alpha.1")]
    public void AnInvalidExplicitLabelIsRejected(string prerelease) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ProductRelease("1.1.3", prerelease));
}
