using System.Net;
using System.Net.Http.Headers;
using AVWorkstationToolkit.Application.Vendors;

namespace AVWorkstationToolkit.Infrastructure.Windows.Vendors;

public sealed class VendorHttpsDownloader : IDisposable
{
    private const int MaximumRedirects = 5;
    private readonly HttpClient client;
    private readonly VendorCachePathPolicy paths;

    public VendorHttpsDownloader(VendorCachePathPolicy paths)
        : this(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseDefaultCredentials = false }, paths)
    {
    }

    internal VendorHttpsDownloader(HttpMessageHandler handler, VendorCachePathPolicy paths)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler))) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AVWorkstationToolkit", "1.1"));
    }

    public async Task<VendorDownloadResult> DownloadAsync(
        VendorDeliveryAuthorization authorization, string explicitDataRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.Mode != Domain.Catalog.DeliveryMode.DirectDownload || authorization.SourceUri is null)
            throw new InvalidOperationException("HTTPS delivery requires catalog-authorized direct-download metadata.");
        var fileName = VendorCachePathPolicy.RequireInstallerFileName(Path.GetFileName(authorization.SourceUri.LocalPath));
        var destination = paths.GetTemporaryPayloadPath(explicitDataRoot, authorization, fileName);
        try
        {
            var current = authorization.SourceUri;
            using var response = await FollowRedirectsAsync(current, authorization, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > authorization.MaximumBytes))
                throw new InvalidDataException("The HTTPS payload size violates the catalogued limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyBoundedAsync(source, output, authorization.MaximumBytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return new(VendorPayloadState.Downloaded, destination, "The HTTPS payload was downloaded to the isolated cache for verification.");
        }
        catch
        {
            DeletePartial(destination);
            throw;
        }
    }

    private async Task<HttpResponseMessage> FollowRedirectsAsync(Uri initial, VendorDeliveryAuthorization authorization, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!current.IsAbsoluteUri || current.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(current.DnsSafeHost) || current.UserInfo.Length != 0)
                throw new InvalidDataException("Vendor redirects require HTTPS without embedded credentials.");
            if (!authorization.AllowedHosts.Contains(current.DnsSafeHost)) throw new InvalidDataException("The HTTPS redirect host is not catalog-approved.");
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode)) return response;
            if (redirect == MaximumRedirects || response.Headers.Location is null)
            {
                response.Dispose();
                throw new HttpRequestException("The vendor download exceeded the redirect limit.");
            }
            var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
            response.Dispose();
            current = next;
        }
        throw new HttpRequestException("The vendor download exceeded the redirect limit.");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[131_072];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("The HTTPS payload exceeded the catalogued size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total == 0) throw new InvalidDataException("The HTTPS payload was empty.");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void DeletePartial(string path)
    {
        try { if (path.EndsWith(".download", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); }
        catch { }
    }

    public void Dispose() => client.Dispose();
}
