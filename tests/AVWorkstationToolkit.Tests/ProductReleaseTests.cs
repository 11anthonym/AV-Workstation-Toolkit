using AVWorkstationToolkit.Application.Diagnostics;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ProductReleaseTests
{
    [TestMethod]
    [DataRow("1.1.1-beta.2+a1b2c3", "beta.2", "1.1.1-beta.2", "1.1.1 Beta 2")]
    [DataRow("1.1.1-alpha.1", "alpha.1", "1.1.1-alpha.1", "1.1.1 Alpha 1")]
    [DataRow("1.1.1-rc.10+a1b2c3", "rc.10", "1.1.1-rc.10", "1.1.1 RC 10")]
    [DataRow("1.1.1+a1b2c3", "", "1.1.1", "1.1.1")]
    [DataRow(null, "", "1.1.1", "1.1.1")]
    public void InformationalVersionYieldsOnlyALabelThatExtendsTheNumericVersion(string? informational, string prerelease, string semantic, string display)
    {
        var release = ProductRelease.FromInformationalVersion("1.1.1", informational);

        Assert.AreEqual("1.1.1", release.Version);
        Assert.AreEqual(prerelease, release.Prerelease);
        Assert.AreEqual(semantic, release.SemanticVersion);
        Assert.AreEqual(display, release.DisplayVersion);
    }

    [TestMethod]
    [DataRow("1.1.2-beta.1+a1b2c3")]
    [DataRow("1.1.1-beta+a1b2c3")]
    [DataRow("1.1.1-beta.0")]
    [DataRow("1.1.1-Beta.2")]
    [DataRow("1.1.1-preview.2")]
    [DataRow("1.1.1-beta.2.1")]
    [DataRow("1.1.1-beta.1000")]
    [DataRow("1.1.1--beta.2")]
    public void UnrecognizedOrMismatchedLabelsAreIgnored(string informational)
    {
        var release = ProductRelease.FromInformationalVersion("1.1.1", informational);

        Assert.AreEqual(string.Empty, release.Prerelease);
        Assert.AreEqual("1.1.1", release.DisplayVersion);
    }

    [TestMethod]
    [DataRow("beta")]
    [DataRow("beta.0")]
    [DataRow("nightly.1")]
    [DataRow("beta.2+a1b2c3")]
    public void AnInvalidExplicitLabelIsRejected(string prerelease) =>
        Assert.ThrowsExactly<ArgumentException>(() => new ProductRelease("1.1.1", prerelease));
}
