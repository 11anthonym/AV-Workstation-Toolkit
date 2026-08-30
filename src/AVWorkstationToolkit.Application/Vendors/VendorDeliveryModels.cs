using System.Collections.Frozen;
using System.Text.RegularExpressions;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Versions;

namespace AVWorkstationToolkit.Application.Vendors;

public enum VendorPayloadState { Downloaded, Verified, Rejected }

public sealed record VendorDownloadResult(VendorPayloadState State, string Path, string Detail);

public sealed record VendorEndpoint(string Host, int Port);

public sealed class VendorSftpHostTrust
{
    internal VendorSftpHostTrust(VendorEndpoint endpoint, string fingerprint)
    {
        Endpoint = endpoint;
        Fingerprint = fingerprint;
    }
    public VendorEndpoint Endpoint { get; }
    public string Fingerprint { get; }
}

public sealed class VendorSftpIdentity
{
    internal VendorSftpIdentity(VendorEndpoint endpoint, string username, string expectedFingerprint)
    {
        Endpoint = endpoint;
        Username = username;
        ExpectedFingerprint = expectedFingerprint;
    }
    public VendorEndpoint Endpoint { get; }
    public string Username { get; }
    public string ExpectedFingerprint { get; }
}

public sealed class VendorDeliveryAuthorization
{
    private VendorDeliveryAuthorization(
        string packageId, string version, DeliveryMode mode, Uri? sourceUri, IReadOnlySet<string> allowedHosts,
        string publisherPattern, long maximumBytes, string expectedSha256, VendorSftpIdentity? sftpIdentity,
        string remoteRoot, string remotePath)
    {
        PackageId = packageId;
        Version = version;
        Mode = mode;
        SourceUri = sourceUri;
        AllowedHosts = allowedHosts;
        PublisherPattern = publisherPattern;
        MaximumBytes = maximumBytes;
        ExpectedSha256 = expectedSha256;
        SftpIdentity = sftpIdentity;
        RemoteRoot = remoteRoot;
        RemotePath = remotePath;
    }

    public string PackageId { get; }
    public string Version { get; }
    public DeliveryMode Mode { get; }
    public Uri? SourceUri { get; }
    public IReadOnlySet<string> AllowedHosts { get; }
    public string PublisherPattern { get; }
    public long MaximumBytes { get; }
    public string ExpectedSha256 { get; }
    public VendorSftpIdentity? SftpIdentity { get; }
    public string RemoteRoot { get; }
    public string RemotePath { get; }

    public static VendorDeliveryAuthorization ForHttps(PackageDefinition package, string version, Uri sourceUri)
    {
        var policy = RequirePolicy(package, DeliveryMode.DirectDownload);
        _ = VersionValue.Parse(version);
        RequireHttps(sourceUri);
        var hosts = policy.AllowedHosts.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        if (!hosts.Contains(sourceUri.DnsSafeHost)) throw new InvalidDataException("The HTTPS download host is not catalog-approved.");
        if (!Regex.IsMatch(sourceUri.AbsoluteUri, policy.DownloadUriPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            throw new InvalidDataException("The HTTPS download URI does not match the catalogued policy.");
        return new(package.Id, version, DeliveryMode.DirectDownload, sourceUri, hosts, policy.PublisherPattern,
            policy.MaximumBytes, policy.Sha256, null, string.Empty, string.Empty);
    }

    public static VendorDeliveryAuthorization ForSftp(
        PackageDefinition package, string version, string username, VendorSftpHostTrust trustedHost, string remotePath)
    {
        var policy = RequirePolicy(package, DeliveryMode.AuthenticatedSftp);
        _ = VersionValue.Parse(version);
        ArgumentNullException.ThrowIfNull(trustedHost);
        var endpoint = new VendorEndpoint(RequireDnsHost(policy.Host), RequirePort(policy.Port));
        if (!endpoint.Equals(trustedHost.Endpoint)) throw new InvalidDataException("The trusted SFTP identity does not match the catalogued endpoint.");
        var identity = new VendorSftpIdentity(endpoint, RequireText(username, "username", 256), RequireFingerprint(trustedHost.Fingerprint));
        var root = RequireRemoteRoot(policy.RemoteRoot);
        var path = RequireRemotePath(remotePath, root);
        return new(package.Id, version, DeliveryMode.AuthenticatedSftp, null,
            Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase), policy.PublisherPattern, policy.MaximumBytes, policy.Sha256,
            identity, root, path);
    }

    private static CatalogDeliveryPolicy RequirePolicy(PackageDefinition package, DeliveryMode mode)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Provider != ProviderKind.External || package.Authority != CatalogAuthority.OperationalExternal ||
            package.DeliveryMode != mode || package.DeliveryPolicy is null)
            throw new InvalidOperationException("The package does not grant this vendor delivery authority.");
        if (package.Id.Length is < 2 or > 128 || !char.IsAsciiLetterOrDigit(package.Id[0]) ||
            package.Id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '+' or '_' or '.' or '-')))
            throw new InvalidDataException("The catalogued package ID is invalid.");
        if (package.DeliveryPolicy.MaximumBytes is < 1_048_576 or > 4_294_967_296)
            throw new InvalidDataException("The catalogued vendor payload size limit is invalid.");
        return package.DeliveryPolicy;
    }

    internal static Uri RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.DnsSafeHost) || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Vendor downloads require HTTPS without embedded credentials.");
        return uri;
    }

    internal static string RequireDnsHost(string value)
    {
        var host = RequireText(value, "host", 253);
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) throw new InvalidDataException("The SFTP host must be a DNS name.");
        return host.ToLowerInvariant();
    }

    internal static int RequirePort(int port) => port is >= 1 and <= 65_535 ? port : throw new InvalidDataException("The SFTP port is invalid.");

    internal static string RequireFingerprint(string value)
    {
        var fingerprint = RequireText(value, "host fingerprint", 80);
        if (fingerprint.Length != 50 || !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ||
            fingerprint[7..].Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '+' or '/')))
            throw new InvalidDataException("The SFTP fingerprint must use SHA256 OpenSSH format.");
        return fingerprint;
    }

    internal static string RequireRemoteRoot(string value)
    {
        var root = RequireText(value, "remote root", 256).TrimEnd('/');
        if (!root.StartsWith('/') || root.Split('/').Any(segment => segment is "." or ".."))
            throw new InvalidDataException("The SFTP remote root is invalid.");
        return root;
    }

    internal static string RequireRemotePath(string value, string root)
    {
        var path = RequireText(value, "remote path", 1_024);
        if (!path.StartsWith(root + '/', StringComparison.Ordinal) || path.Contains('\\') || path.Split('/').Any(segment => segment is "." or ".."))
            throw new InvalidDataException("The SFTP remote path escaped the catalogued root.");
        return path;
    }

    private static string RequireText(string value, string label, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException($"The vendor {label} is invalid.");
        return value;
    }
}

public sealed class VendorCredential : IDisposable
{
    private char[] secret;
    public VendorCredential(string username, char[] secret)
    {
        Username = username;
        this.secret = secret;
    }
    public string Username { get; }
    public ReadOnlyMemory<char> Secret => secret;
    public void Dispose()
    {
        Array.Clear(secret);
        secret = [];
    }
    public override string ToString() => $"VendorCredential({Username}, [redacted])";
}

public interface IVendorCredentialStore
{
    VendorCredential? Read(VendorSftpIdentity identity);
    void Write(VendorSftpIdentity identity, ReadOnlySpan<char> secret);
    bool Delete(VendorSftpIdentity identity);
}

public interface IVendorHttpsDelivery
{
    Task<VendorDownloadResult> DownloadAsync(VendorDeliveryAuthorization authorization, string explicitDataRoot, CancellationToken cancellationToken);
}

public interface IVendorSftpDelivery
{
    Task<VendorDownloadResult> DownloadAsync(
        VendorDeliveryAuthorization authorization,
        string explicitDataRoot,
        VendorCredential? suppliedCredential,
        bool saveCredential,
        CancellationToken cancellationToken);
}

public interface IVendorPayloadVerifier
{
    VendorDownloadResult VerifyAndPromote(VendorDeliveryAuthorization authorization, string explicitDataRoot, string temporaryPath);
    VendorDownloadResult ResolveCached(VendorDeliveryAuthorization authorization, string explicitDataRoot, string payloadPath);
}

public sealed record VendorCredentialState(bool Present, string Detail);

/// <summary>
/// Non-shipping vendor interaction coordinator. Catalog authorization remains
/// mandatory, downloads remain untrusted until verification succeeds, and no
/// method can execute a downloaded payload.
/// </summary>
public sealed class VendorInteractionCoordinator(
    string explicitDataRoot,
    IVendorHttpsDelivery https,
    IVendorSftpDelivery sftp,
    IVendorCredentialStore credentials,
    IVendorPayloadVerifier verifier)
{
    public async Task<VendorDownloadResult> DownloadHttpsAndVerifyAsync(
        VendorDeliveryAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        var downloaded = await https.DownloadAsync(authorization, explicitDataRoot, cancellationToken).ConfigureAwait(false);
        return downloaded.State == VendorPayloadState.Downloaded
            ? verifier.VerifyAndPromote(authorization, explicitDataRoot, downloaded.Path)
            : downloaded;
    }

    public async Task<VendorDownloadResult> DownloadSftpAndVerifyAsync(
        VendorDeliveryAuthorization authorization,
        VendorCredential? suppliedCredential,
        bool saveCredential,
        CancellationToken cancellationToken = default)
    {
        var downloaded = await sftp.DownloadAsync(authorization, explicitDataRoot, suppliedCredential, saveCredential, cancellationToken).ConfigureAwait(false);
        return downloaded.State == VendorPayloadState.Downloaded
            ? verifier.VerifyAndPromote(authorization, explicitDataRoot, downloaded.Path)
            : downloaded;
    }

    public VendorCredentialState CredentialState(VendorSftpIdentity identity)
    {
        using var credential = credentials.Read(identity);
        return credential is null
            ? new(false, "No scoped credential is saved.")
            : new(true, "A scoped credential is saved in Windows Credential Manager.");
    }

    public void SaveCredential(VendorSftpIdentity identity, ReadOnlySpan<char> secret) => credentials.Write(identity, secret);
    public bool DeleteCredential(VendorSftpIdentity identity) => credentials.Delete(identity);
    public VendorDownloadResult ResolveCached(VendorDeliveryAuthorization authorization, string payloadPath) =>
        verifier.ResolveCached(authorization, explicitDataRoot, payloadPath);
}
