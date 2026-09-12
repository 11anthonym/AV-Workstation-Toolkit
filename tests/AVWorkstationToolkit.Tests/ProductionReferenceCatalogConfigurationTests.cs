using AVWorkstationToolkit.Infrastructure.Windows.Catalog;

namespace AVWorkstationToolkit.Tests;

[TestClass]
public sealed class ProductionReferenceCatalogConfigurationTests
{
    [TestMethod]
    public void ProductionFeedUsesOnlyExactGithubPagesOrigin()
    {
        Assert.AreEqual("https://11anthonym.github.io/AVWT-Catalog/", ProductionReferenceCatalogConfiguration.FeedRoot.AbsoluteUri);
        Assert.AreEqual("https://11anthonym.github.io/AVWT-Catalog/stable/catalog-channel.json", ProductionReferenceCatalogConfiguration.MetadataUri.AbsoluteUri);
        Assert.AreEqual("https://11anthonym.github.io/AVWT-Catalog/stable/catalog-channel.sig", ProductionReferenceCatalogConfiguration.SignatureUri.AbsoluteUri);
        Assert.AreEqual(Uri.UriSchemeHttps, ProductionReferenceCatalogConfiguration.MetadataUri.Scheme);
        Assert.IsTrue(ProductionReferenceCatalogConfiguration.MetadataUri.IsDefaultPort);
        Assert.AreEqual(string.Empty, ProductionReferenceCatalogConfiguration.MetadataUri.UserInfo);
        Assert.AreNotEqual(ProductionReferenceCatalogConfiguration.MetadataUri, ProductionReferenceCatalogConfiguration.SignatureUri);
    }

    [TestMethod]
    public void ProductionChannelRemainsFailClosedUntilOwnerPublicKeyHandoff()
    {
        var services = ProductionReferenceCatalogConfiguration.Create("1.1.1");

        Assert.AreEqual(0, ProductionReferenceCatalogConfiguration.TrustedPublicKeyCount,
            "Adding a production public key requires an explicit owner handoff and review of this regression.");
        Assert.IsNull(services.ChannelClient);
        Assert.IsNotNull(services.BundleVerifier);
    }
}
