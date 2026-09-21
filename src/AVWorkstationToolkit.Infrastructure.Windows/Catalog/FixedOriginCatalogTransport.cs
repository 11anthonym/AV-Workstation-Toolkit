using System.Collections.Frozen;
using System.Net;
using AVWorkstationToolkit.Domain.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

/// <summary>Bounded anonymous HTTPS transport shared by the two independently verified catalog feeds.</summary>
internal sealed class FixedOriginCatalogTransport : IDisposable
{
    private readonly IReadOnlySet<string> approvedHosts;
    private readonly HttpClient client;

    internal FixedOriginCatalogTransport(IReadOnlySet<string> approvedHosts, TimeSpan timeout)
        : this(approvedHosts, timeout, new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseDefaultCredentials = false
        }, ownsHandler: true)
    {
    }

    internal FixedOriginCatalogTransport(
        IReadOnlySet<string> approvedHosts,
        TimeSpan timeout,
        HttpMessageHandler handler,
        bool ownsHandler = true)
    {
        if (approvedHosts is null || approvedHosts.Count == 0)
            throw new ArgumentException("At least one catalog host is required.", nameof(approvedHosts));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        this.approvedHosts = approvedHosts.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), ownsHandler) { Timeout = timeout };
    }

    internal async Task<byte[]> GetExactAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        RequireApprovedHttps(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/octet-stream, application/json;q=0.9");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new CatalogValidationException("Catalog channel redirects are not permitted.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maximumBytes))
            throw new CatalogValidationException("Catalog channel response size is invalid.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new CatalogValidationException("Catalog channel response exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new CatalogValidationException("Catalog channel response is empty.");
        return output.ToArray();
    }

    internal void RequireApprovedHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !approvedHosts.Contains(uri.IdnHost) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new CatalogValidationException("Catalog channel URI is outside the approved HTTPS origin.");
    }

    public void Dispose() => client.Dispose();
}
