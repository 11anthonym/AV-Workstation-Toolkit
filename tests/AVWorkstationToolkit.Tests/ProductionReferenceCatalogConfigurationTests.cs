using System.Security.Cryptography;
using System.Text.Json;
using AVWorkstationToolkit.Domain.Catalog;
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
    public void ProductionPublicKeyIsTheExactOwnerSuppliedP256TrustAnchor()
    {
        var services = ProductionReferenceCatalogConfiguration.Create("1.1.1");
        var anchors = ProductionReferenceCatalogTrustAnchors.All;

        Assert.AreEqual(1, ProductionReferenceCatalogConfiguration.TrustedPublicKeyCount);
        Assert.HasCount(1, anchors);
        Assert.IsTrue(anchors.TryGetValue(ProductionReferenceCatalogConfiguration.PrimarySigningKeyId, out var pem));
        Assert.IsFalse(anchors.ContainsKey("test-2026-a"));

        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        Assert.AreEqual(256, key.KeySize);
        Assert.AreEqual("1.2.840.10045.3.1.7", key.ExportParameters(false).Curve.Oid.Value);
        Assert.AreEqual(
            "D4A0306618232F9D2218CA4B6679E9FA0B00F417FEAB609F64222984574CB31C",
            Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())),
            "The production SPKI fingerprint changed without an explicit owner public-key handoff.");

        Assert.IsNotNull(services.ChannelClient);
        Assert.IsNotNull(services.BundleVerifier);
    }

    [TestMethod]
    public void ProductionVerifierRejectsUnrelatedTestKeyAndSignature()
    {
        using var unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            CatalogId = ReferenceCatalogBundleNames.CatalogId,
            SchemaVersion = 1,
            CatalogVersion = "2026.9.13.1",
            Revision = 1,
            PreviousRevision = 0,
            MinimumAppVersion = "1.1.1",
            CreatedUtc = now.AddMinutes(-1),
            ExpiresUtc = now.AddDays(7),
            SigningKeyId = ProductionReferenceCatalogConfiguration.PrimarySigningKeyId,
            BundleUri = "https://11anthonym.github.io/AVWT-Catalog/catalogs/1/AVWT-Catalog-2026.9.13.1.avwtcatalog",
            BundleSha256 = new string('A', 64)
        });
        var signature = unrelatedKey.SignData(
            metadata,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var verifier = new ReferenceCatalogChannelVerifier(ProductionReferenceCatalogTrustAnchors.All);

        Assert.ThrowsExactly<CatalogValidationException>(() => verifier.Verify(metadata, signature, now));

        var wrongIdMetadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            CatalogId = ReferenceCatalogBundleNames.CatalogId,
            SchemaVersion = 1,
            CatalogVersion = "2026.9.13.1",
            Revision = 1,
            PreviousRevision = 0,
            MinimumAppVersion = "1.1.1",
            CreatedUtc = now.AddMinutes(-1),
            ExpiresUtc = now.AddDays(7),
            SigningKeyId = "test-2026-a",
            BundleUri = "https://11anthonym.github.io/AVWT-Catalog/catalogs/1/AVWT-Catalog-2026.9.13.1.avwtcatalog",
            BundleSha256 = new string('A', 64)
        });
        var wrongIdSignature = unrelatedKey.SignData(
            wrongIdMetadata,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.ThrowsExactly<CatalogValidationException>(() => verifier.Verify(wrongIdMetadata, wrongIdSignature, now));
    }
}
