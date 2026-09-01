using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using AVWorkstationToolkit.Application.Inventory;
using AVWorkstationToolkit.Application.Providers;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Infrastructure.Windows.Vendors;

internal interface IVendorReleaseContentClient
{
    Task<string> ReadAsync(Uri cataloguedUri, CancellationToken cancellationToken);
}

/// <summary>
/// Reads only catalog-authorized vendor release sources and returns evidence;
/// it grants no package execution authority.
/// </summary>
public sealed class VendorExternalReleaseInventory : IExternalReleaseInventory, IDisposable
{
    private readonly PackageCatalog catalog;
    private readonly IVendorReleaseContentClient content;
    private readonly bool ownsContent;

    public VendorExternalReleaseInventory(PackageCatalog catalog)
        : this(catalog, new BoundedVendorReleaseContentClient(), true) { }

    internal VendorExternalReleaseInventory(PackageCatalog catalog, IVendorReleaseContentClient content)
        : this(catalog, content, false) { }

    private VendorExternalReleaseInventory(PackageCatalog catalog, IVendorReleaseContentClient content, bool ownsContent)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.ownsContent = ownsContent;
    }

    public async Task<ExternalReleaseInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var external = catalog.Items.Where(item => item.Provider == ProviderKind.External).ToArray();
        var lookup = external.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var contentCache = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        var productCache = new Dictionary<string, IReadOnlyList<VendorCatalogProduct>>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ExternalReleaseEvidence>(external.Length);
        var failures = 0;

        foreach (var package in external)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var known = package.KnownVersion;
            if (package.ReleaseMode == ReleaseMode.ParentCatalog)
            {
                try
                {
                    var provider = RequireParent(package, lookup);
                    var products = await ReadProductsAsync(provider, contentCache, productCache, cancellationToken).ConfigureAwait(false);
                    var product = products.Single(item => item.ProductId.Equals(package.DeliveryProductId, StringComparison.Ordinal));
                    results.Add(Evidence(package, product.Version, product.Version, true, true,
                        provider.DeliveryPolicy!.CatalogUri, string.Empty,
                        $"Parent provider catalog reports {product.Version} for product {product.ProductId}.", [product]));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    results.Add(Evidence(package, known, string.Empty, true, false,
                        ParentCatalogUri(package, lookup), string.Empty,
                        FailureDetail("Parent-provider version check unavailable", exception, known), []));
                }
                continue;
            }

            if (package.DeliveryMode == DeliveryMode.AuthenticatedSftp)
            {
                try
                {
                    var products = await ReadProductsAsync(package, contentCache, productCache, cancellationToken).ConfigureAwait(false);
                    results.Add(Evidence(package, string.Empty, string.Empty, true, true,
                        package.DeliveryPolicy!.CatalogUri, string.Empty,
                        $"Validated parent provider catalog contains {products.Count} allowlisted products.", products));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    results.Add(Evidence(package, string.Empty, string.Empty, true, false,
                        package.DeliveryPolicy?.CatalogUri ?? string.Empty, string.Empty,
                        FailureDetail("Parent-provider catalog unavailable", exception, string.Empty), []));
                }
                continue;
            }

            if (package.ReleaseMode != ReleaseMode.VendorPage)
            {
                results.Add(Evidence(package, known, string.Empty, false, false, string.Empty, string.Empty,
                    "Inventory-only provider; no online version comparison is performed.", []));
                continue;
            }

            try
            {
                var uri = RequireCataloguedHttps(package.MetadataDetails.ReleaseUri, "vendor release URI");
                var page = await CachedReadAsync(uri, contentCache, cancellationToken).ConfigureAwait(false);
                var observed = ParseHighestVersion(package, page);
                var available = VersionValue.Parse(observed).CompareTo(VersionValue.Parse(known)) > 0 ? observed : known;
                var download = package.DeliveryMode == DeliveryMode.DirectDownload
                    ? ParseDownloadUri(package, page, observed)
                    : string.Empty;
                var detail = $"Vendor {package.ReleaseChannel} page reports {observed}.";
                if (package.DeliveryMode == DeliveryMode.DirectDownload && download.Length == 0)
                    detail += " Direct installer link unavailable; vendor-page handoff remains available.";
                results.Add(Evidence(package, available, observed, true, true, uri.AbsoluteUri, download, detail, []));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                results.Add(Evidence(package, known, string.Empty, true, false, package.MetadataDetails.ReleaseUri, string.Empty,
                    FailureDetail("Online version check unavailable", exception, known), []));
            }
        }

        var quality = failures == 0 ? ProviderQuality.Complete : failures == external.Length ? ProviderQuality.Unavailable : ProviderQuality.Partial;
        var failure = failures == 0 ? ProviderFailureKind.None : ProviderFailureKind.PartialInventory;
        return new(results, quality, failure, failures == 0
            ? "External release evidence was obtained from the catalog-authorized sources."
            : $"{failures} external release source(s) were unavailable; per-package fallback evidence remains explicit.");
    }

    private async Task<IReadOnlyList<VendorCatalogProduct>> ReadProductsAsync(
        PackageDefinition provider,
        Dictionary<string, Task<string>> contentCache,
        Dictionary<string, IReadOnlyList<VendorCatalogProduct>> productCache,
        CancellationToken cancellationToken)
    {
        if (productCache.TryGetValue(provider.Id, out var cached)) return cached;
        if (provider.Provider != ProviderKind.External || provider.Authority != CatalogAuthority.OperationalExternal ||
            provider.DeliveryMode != DeliveryMode.AuthenticatedSftp || provider.DeliveryPolicy is null)
            throw new InvalidDataException("The parent provider does not grant authenticated catalog authority.");
        var uri = RequireCataloguedHttps(provider.DeliveryPolicy.CatalogUri, "parent provider catalog URI");
        var parsed = ParseProductCatalog(provider, await CachedReadAsync(uri, contentCache, cancellationToken).ConfigureAwait(false));
        productCache.Add(provider.Id, parsed);
        return parsed;
    }

    private Task<string> CachedReadAsync(Uri uri, Dictionary<string, Task<string>> cache, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(uri.AbsoluteUri, out var pending))
        {
            pending = content.ReadAsync(uri, cancellationToken);
            cache.Add(uri.AbsoluteUri, pending);
        }
        return pending;
    }

    internal static IReadOnlyList<VendorCatalogProduct> ParseProductCatalog(PackageDefinition provider, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || Encoding.UTF8.GetByteCount(xml) > BoundedVendorReleaseContentClient.MaximumBytes)
            throw new InvalidDataException("SFTP product catalog is empty or exceeds the 2 MiB limit.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(xml), settings)) document.Load(reader);
        if (!string.Equals(document.DocumentElement?.Name, "UpdateInformation", StringComparison.Ordinal))
            throw new InvalidDataException("SFTP product catalog has an unexpected root element.");
        var policy = provider.DeliveryPolicy ?? throw new InvalidDataException("SFTP catalog policy is unavailable.");
        var allowed = policy.AllowedProductIds.ToHashSet(StringComparer.Ordinal);
        var products = new List<VendorCatalogProduct>();
        foreach (XmlElement node in document.SelectNodes("/UpdateInformation/Product") ?? throw new InvalidDataException("SFTP product catalog is malformed."))
        {
            var id = node.GetAttribute("Id");
            if (!allowed.Contains(id)) continue;
            var version = node.GetAttribute("Version");
            _ = VersionValue.Parse(version);
            var name = node["Name"]?.InnerText ?? string.Empty;
            var remotePath = node["Download"]?.InnerText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
                throw new InvalidDataException($"SFTP product {id} has an invalid name.");
            var root = policy.RemoteRoot.TrimEnd('/');
            if (!remotePath.StartsWith(root + '/', StringComparison.Ordinal) || remotePath.Contains('\\') ||
                remotePath.Split('/').Any(segment => segment is "." or "..") ||
                !Path.GetExtension(remotePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                !remotePath.Contains(version, StringComparison.Ordinal))
                throw new InvalidDataException($"SFTP product {id} has an unsafe remote path.");
            var sizeNode = node["Size"];
            if (sizeNode?.GetAttribute("Units") != "MB" ||
                !double.TryParse(sizeNode.InnerText, NumberStyles.Number, CultureInfo.InvariantCulture, out var sizeMb) || sizeMb <= 0)
                throw new InvalidDataException($"SFTP product {id} has an invalid size.");
            var sizeBytes = checked((long)Math.Ceiling(sizeMb * 1024 * 1024));
            if (sizeBytes > policy.MaximumBytes) throw new InvalidDataException($"SFTP product {id} exceeds the catalogued size limit.");
            products.Add(new(id, name, version, remotePath, Path.GetFileName(remotePath), sizeBytes, node["Reboot"]?.InnerText == "1"));
        }
        var missing = allowed.Except(products.Select(item => item.ProductId), StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("SFTP product catalog omitted allowlisted product IDs: " + string.Join(", ", missing));
        return products.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string ParseHighestVersion(PackageDefinition package, string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > BoundedVendorReleaseContentClient.MaximumBytes)
            throw new InvalidDataException("External release response exceeds the 2 MiB safety limit.");
        var pattern = package.MetadataDetails.ReleaseVersionPattern;
        var matches = Regex.Matches(content, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        if (matches.Count == 0) throw new InvalidDataException($"The {package.ReleaseChannel} version was not found on the vendor release page.");
        string? highest = null;
        foreach (Match match in matches)
        {
            var candidate = match.Groups["Version"].Value;
            var parsed = VersionValue.Parse(candidate);
            if (highest is null || parsed.CompareTo(VersionValue.Parse(highest)) > 0) highest = candidate;
        }
        return highest!;
    }

    internal static string ParseDownloadUri(PackageDefinition package, string content, string expectedVersion)
    {
        var policy = package.DeliveryPolicy ?? throw new InvalidDataException("Direct-download policy is unavailable.");
        var matches = Regex.Matches(content, policy.DownloadUriPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        foreach (Match match in matches)
        {
            var value = WebUtility.HtmlDecode(match.Groups["Uri"].Value);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                !policy.AllowedHosts.Contains(uri.DnsSafeHost, StringComparer.OrdinalIgnoreCase)) continue;
            var captured = match.Groups["Version"].Value.Replace('-', '.');
            if (!VersionValue.TryParse(captured, out var parsed) || parsed.CompareTo(VersionValue.Parse(expectedVersion)) != 0) continue;
            return uri.AbsoluteUri;
        }
        return string.Empty;
    }

    private static PackageDefinition RequireParent(PackageDefinition package, IReadOnlyDictionary<string, PackageDefinition> lookup)
    {
        if (!lookup.TryGetValue(package.ParentProviderId, out var provider)) throw new InvalidDataException("Parent provider is unavailable.");
        return provider;
    }

    private static string ParentCatalogUri(PackageDefinition package, IReadOnlyDictionary<string, PackageDefinition> lookup) =>
        lookup.TryGetValue(package.ParentProviderId, out var parent) ? parent.DeliveryPolicy?.CatalogUri ?? string.Empty : string.Empty;

    private static ExternalReleaseEvidence Evidence(PackageDefinition package, string available, string observed, bool checkedOnline,
        bool onlineAvailable, string releaseUri, string downloadUri, string detail, IReadOnlyList<VendorCatalogProduct> products) =>
        new(package.Id, available, observed, checkedOnline, onlineAvailable, releaseUri, downloadUri, detail, products);

    private static Uri RequireCataloguedHttps(string value, string label) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.DnsSafeHost.Length > 0
            ? uri : throw new InvalidDataException($"The catalogued {label} is invalid.");

    private static string FailureDetail(string prefix, Exception exception, string knownVersion)
    {
        var message = new string(exception.Message.Where(character => !char.IsControl(character)).Take(512).ToArray());
        return $"{prefix}; {message}" + (knownVersion.Length > 0 ? $" Catalog baseline {knownVersion} remains in use." : string.Empty);
    }

    public void Dispose()
    {
        if (ownsContent && content is IDisposable disposable) disposable.Dispose();
    }
}

internal sealed class BoundedVendorReleaseContentClient : IVendorReleaseContentClient, IDisposable
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private readonly HttpClient client;

    public BoundedVendorReleaseContentClient() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseDefaultCredentials = false
    })
    { }

    internal BoundedVendorReleaseContentClient(HttpMessageHandler handler)
    {
        client = new(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AVWorkstationToolkit", "1.1"));
    }

    public async Task<string> ReadAsync(Uri cataloguedUri, CancellationToken cancellationToken)
    {
        var approvedHost = cataloguedUri.DnsSafeHost;
        var current = cataloguedUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || current.UserInfo.Length != 0 ||
                !current.DnsSafeHost.Equals(approvedHost, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Vendor content redirected to an unapproved host.");
            using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirect == MaximumRedirects || response.Headers.Location is null) throw new HttpRequestException("Vendor content request exceeded the redirect limit.");
                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > MaximumBytes))
                throw new InvalidDataException("Vendor content response is empty or exceeds the 2 MiB limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var memory = new MemoryStream();
            var buffer = new byte[32_768];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > MaximumBytes) throw new InvalidDataException("Vendor content response exceeds the 2 MiB limit.");
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (memory.Length == 0) throw new InvalidDataException("Vendor content response was empty.");
            return Encoding.UTF8.GetString(memory.ToArray());
        }
        throw new HttpRequestException("Vendor content request did not return a successful response.");
    }

    public void Dispose() => client.Dispose();
}
