using System.Security.Cryptography;
using System.Text;
using AVWorkstationToolkit.Application.Vendors;
using Renci.SshNet;

namespace AVWorkstationToolkit.Infrastructure.Windows.Vendors;

internal interface IVendorSftpTransport
{
    Task<string> ProbeHostFingerprintAsync(VendorEndpoint endpoint, CancellationToken cancellationToken);
    Task DownloadAsync(VendorSftpIdentity identity, ReadOnlyMemory<char> secret, string remotePath, Stream destination, long maximumBytes, CancellationToken cancellationToken);
}

public sealed class VendorSftpDeliveryService : IVendorSftpDelivery, IVendorSftpHostProbe
{
    private readonly IVendorSftpTransport transport;
    private readonly IVendorCredentialStore credentials;
    private readonly VendorCachePathPolicy paths;

    public VendorSftpDeliveryService(VendorCachePathPolicy paths)
        : this(new SshNetVendorSftpTransport(), new WindowsVendorCredentialStore(), paths)
    {
    }

    internal VendorSftpDeliveryService(IVendorSftpTransport transport, IVendorCredentialStore credentials, VendorCachePathPolicy paths)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public Task<string> ProbeAsync(VendorEndpoint endpoint, CancellationToken cancellationToken) =>
        transport.ProbeHostFingerprintAsync(endpoint, cancellationToken);

    public async Task<VendorDownloadResult> DownloadAsync(
        VendorDeliveryAuthorization authorization, string explicitDataRoot, VendorCredential? suppliedCredential, bool saveCredential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.Mode != Domain.Catalog.DeliveryMode.AuthenticatedSftp || authorization.SftpIdentity is null)
            throw new InvalidOperationException("SFTP delivery requires catalog-authorized SFTP metadata.");
        var identity = authorization.SftpIdentity;
        var observed = await transport.ProbeHostFingerprintAsync(identity.Endpoint, cancellationToken).ConfigureAwait(false);
        if (!FixedTimeEquals(observed, identity.ExpectedFingerprint)) throw new InvalidDataException("The SFTP host fingerprint does not match the trusted identity.");

        using var stored = suppliedCredential is null ? credentials.Read(identity) : null;
        var credential = suppliedCredential ?? stored ?? throw new InvalidOperationException("No scoped SFTP credential is available.");
        if (!string.Equals(credential.Username, identity.Username, StringComparison.Ordinal)) throw new InvalidDataException("The credential username does not match the SFTP request.");
        var fileName = VendorCachePathPolicy.RequireInstallerFileName(Path.GetFileName(authorization.RemotePath));
        var destination = paths.GetTemporaryPayloadPath(explicitDataRoot, authorization, fileName);
        try
        {
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await transport.DownloadAsync(identity, credential.Secret, authorization.RemotePath, output, authorization.MaximumBytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            var length = output.Length;
            if (length <= 0 || length > authorization.MaximumBytes) throw new InvalidDataException("The SFTP payload size violates the catalogued limit.");
            if (saveCredential) credentials.Write(identity, credential.Secret.Span);
            return new(VendorPayloadState.Downloaded, destination, "The SFTP payload was downloaded to the isolated cache for verification.");
        }
        catch
        {
            try { if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) == 0) File.Delete(destination); } catch { }
            throw;
        }
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        try { return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally { CryptographicOperations.ZeroMemory(leftBytes); CryptographicOperations.ZeroMemory(rightBytes); }
    }
}

internal sealed class SshNetVendorSftpTransport : IVendorSftpTransport
{
    public Task<string> ProbeHostFingerprintAsync(VendorEndpoint endpoint, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new ConnectionInfo(endpoint.Host, endpoint.Port, "AVWorkstationToolkitHostProbe", new NoneAuthenticationMethod("AVWorkstationToolkitHostProbe")) { Timeout = TimeSpan.FromSeconds(20) };
        string? fingerprint = null;
        using var client = new SftpClient(connection) { OperationTimeout = TimeSpan.FromSeconds(20) };
        client.HostKeyReceived += (_, eventArgs) => { fingerprint = FormatFingerprint(eventArgs.HostKey); eventArgs.CanTrust = false; };
        try { client.Connect(); }
        catch when (!string.IsNullOrWhiteSpace(fingerprint)) { }
        return fingerprint ?? throw new InvalidOperationException("The SFTP server did not present a host identity.");
    }, cancellationToken);

    public Task DownloadAsync(VendorSftpIdentity identity, ReadOnlyMemory<char> secret, string remotePath, Stream destination, long maximumBytes, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var password = new string(secret.Span);
        try
        {
            using var client = new SftpClient(identity.Endpoint.Host, identity.Endpoint.Port, identity.Username, password) { OperationTimeout = TimeSpan.FromMinutes(30) };
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
            client.HostKeyReceived += (_, eventArgs) => eventArgs.CanTrust = FixedFingerprint(eventArgs.HostKey, identity.ExpectedFingerprint);
            client.Connect();
            var attributes = client.GetAttributes(remotePath);
            if (attributes.IsDirectory || attributes.Size <= 0 || attributes.Size > maximumBytes) throw new InvalidDataException("The SFTP payload size violates the catalogued limit.");
            using var bounded = new BoundedWriteStream(destination, maximumBytes);
            client.DownloadFile(remotePath, bounded);
            client.Disconnect();
        }
        finally { password = string.Empty; }
    }, cancellationToken);

    private static string FormatFingerprint(byte[] hostKey) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
    private static bool FixedFingerprint(byte[] hostKey, string expected) => VendorSftpDeliveryService.FixedTimeEquals(FormatFingerprint(hostKey), expected);

    private sealed class BoundedWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private long written;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            written = checked(written + count);
            if (written > maximumBytes) throw new InvalidDataException("The SFTP payload exceeded the catalogued size limit.");
            inner.Write(buffer, offset, count);
        }
    }
}
