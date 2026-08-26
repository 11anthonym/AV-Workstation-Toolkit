using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Renci.SshNet;

namespace AVWorkstationToolkit.Launcher;

internal static class VendorBridge
{
    private const int MaximumRequestCharacters = 65_536;
    private const int MaximumRedirects = 5;
    private const string CredentialPrefix = "AVWorkstationToolkit:VendorSftp:";
    private const string LegacyCredentialPrefix = "AVinite:VendorSftp:";

    public static int Run()
    {
        VendorBridgeRequest? request = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            var json = NormalizeRequestText(ReadBoundedStandardInput());
            request = JsonSerializer.Deserialize(json, VendorBridgeJsonContext.Default.VendorBridgeRequest)
                ?? throw new InvalidDataException("The vendor bridge request is empty.");
            if (isElevated && !string.Equals(request.Operation, "SelfTest", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The vendor bridge must run as a standard user.");
            }
            var response = Execute(request);
            WriteResponse(response);
            return response.Success ? 0 : 1;
        }
        catch (Exception exception)
        {
            WriteResponse(new VendorBridgeResponse
            {
                Success = false,
                Operation = request?.Operation ?? string.Empty,
                Message = SanitizeError(exception)
            });
            return 1;
        }
        finally
        {
            if (request is not null)
            {
                request.Password = null;
            }
        }
    }

    private static VendorBridgeResponse Execute(VendorBridgeRequest request)
    {
        var operation = RequireText(request.Operation, "operation", 64);
        return operation switch
        {
            "SelfTest" => SelfTest(),
            "CredentialRoundTripTest" => CredentialRoundTripTest(),
            "CredentialExists" => CredentialExists(request),
            "DeleteCredential" => DeleteCredential(request),
            "ProbeSftpHost" => ProbeSftpHost(request),
            "TestSftp" => TestSftp(request),
            "DownloadSftp" => DownloadSftp(request),
            "DownloadHttps" => DownloadHttps(request),
            _ => throw new InvalidDataException($"Unsupported vendor bridge operation: {operation}")
        };
    }

    private static VendorBridgeResponse SelfTest()
    {
        var target = $"AVWorkstationToolkit:SelfTest:{Guid.NewGuid():N}";
        _ = WinCredentialStore.Exists(target);
        return new VendorBridgeResponse
        {
            Success = true,
            Operation = "SelfTest",
            Message = "Vendor bridge, Windows Credential Manager API, HTTPS, and SFTP libraries are available."
        };
    }

    private static VendorBridgeResponse CredentialRoundTripTest()
    {
        var target = $"AVWorkstationToolkit:SelfTest:{Guid.NewGuid():N}";
        var username = "AVWorkstationToolkitSelfTest";
        var password = Guid.NewGuid().ToString("N");
        try
        {
            WinCredentialStore.Write(target, username, password);
            if (!WinCredentialStore.Exists(target))
            {
                throw new InvalidOperationException("Windows Credential Manager did not retain the temporary self-test credential.");
            }
            var stored = WinCredentialStore.Read(target);
            if (stored is null || !stored.Username.Equals(username, StringComparison.Ordinal) ||
                !FixedTimeEquals(stored.Password, password))
            {
                throw new InvalidOperationException("Windows Credential Manager did not return the temporary self-test credential.");
            }
        }
        finally
        {
            _ = WinCredentialStore.Delete(target);
        }
        return new VendorBridgeResponse
        {
            Success = true,
            Operation = "CredentialRoundTripTest",
            Message = "The temporary Windows Credential Manager write, read, and delete round trip succeeded."
        };
    }

    private static VendorBridgeResponse CredentialExists(VendorBridgeRequest request)
    {
        var host = RequireDnsHost(request.Host);
        var port = RequirePort(request.Port);
        var username = RequireUsername(request.Username);
        var exists = WinCredentialStore.Exists(GetCredentialTarget(host, port, username)) ||
            WinCredentialStore.Exists(GetLegacyCredentialTarget(host, port, username));
        return new VendorBridgeResponse
        {
            Success = true,
            Operation = "CredentialExists",
            CredentialExists = exists,
            Message = exists ? "A saved credential exists for this user and host." : "No saved credential exists for this user and host."
        };
    }

    private static VendorBridgeResponse DeleteCredential(VendorBridgeRequest request)
    {
        var host = RequireDnsHost(request.Host);
        var port = RequirePort(request.Port);
        var username = RequireUsername(request.Username);
        var deleted = WinCredentialStore.Delete(GetCredentialTarget(host, port, username));
        deleted = WinCredentialStore.Delete(GetLegacyCredentialTarget(host, port, username)) || deleted;
        return new VendorBridgeResponse
        {
            Success = true,
            Operation = "DeleteCredential",
            CredentialExists = false,
            Message = deleted ? "The saved credential was removed." : "No saved credential existed."
        };
    }

    private static VendorBridgeResponse ProbeSftpHost(VendorBridgeRequest request)
    {
        var host = RequireDnsHost(request.Host);
        var port = RequirePort(request.Port);
        var connection = new ConnectionInfo(host, port, "AVWorkstationToolkitHostProbe", new NoneAuthenticationMethod("AVWorkstationToolkitHostProbe"))
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        string? fingerprint = null;
        using var client = new SftpClient(connection) { OperationTimeout = TimeSpan.FromSeconds(20) };
        client.HostKeyReceived += (_, eventArgs) =>
        {
            fingerprint = FormatFingerprint(eventArgs.HostKey);
            eventArgs.CanTrust = false;
        };
        try
        {
            client.Connect();
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(fingerprint))
        {
            // Host identity is received before authentication; rejecting it is
            // intentional for this read-only first-use probe.
        }
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new InvalidOperationException("The SFTP server did not present a host identity.");
        }
        return new VendorBridgeResponse
        {
            Success = true,
            Operation = "ProbeSftpHost",
            Fingerprint = fingerprint,
            Message = "The SFTP host identity was observed without sending a credential."
        };
    }

    private static VendorBridgeResponse TestSftp(VendorBridgeRequest request)
    {
        var parameters = GetSftpParameters(request);
        try
        {
            using var client = CreateTrustedSftpClient(parameters);
            client.Connect();
            client.Disconnect();
            if (request.SaveCredential)
            {
                WinCredentialStore.Write(parameters.CredentialTarget, parameters.Username, parameters.Password);
            }
            return new VendorBridgeResponse
            {
                Success = true,
                Operation = "TestSftp",
                CredentialExists = request.SaveCredential || parameters.UsedStoredCredential,
                Fingerprint = parameters.ExpectedFingerprint,
                Message = request.SaveCredential ? "SFTP authentication succeeded and the credential was saved on this computer." : "SFTP authentication succeeded."
            };
        }
        finally
        {
            parameters.ClearPasswordReference();
        }
    }

    private static VendorBridgeResponse DownloadSftp(VendorBridgeRequest request)
    {
        var parameters = GetSftpParameters(request);
        var remoteRoot = RequireRemoteRoot(request.RemoteRoot);
        var remotePath = RequireRemotePath(request.RemotePath, remoteRoot);
        var maximumBytes = RequireMaximumBytes(request.MaxBytes);
        var fileName = RequireInstallerFileName(Path.GetFileName(remotePath));
        var downloadPath = GetSafeDownloadPath(request, fileName);

        try
        {
            using var client = CreateTrustedSftpClient(parameters);
            client.OperationTimeout = TimeSpan.FromMinutes(30);
            client.Connect();
            var attributes = client.GetAttributes(remotePath);
            if (attributes.IsDirectory || attributes.Size <= 0 || attributes.Size > maximumBytes)
            {
                throw new InvalidDataException("The SFTP payload size violates the catalogued limit.");
            }
            using (var file = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, FileOptions.SequentialScan))
            using (var limited = new LimitedWriteStream(file, maximumBytes))
            {
                client.DownloadFile(remotePath, limited);
                file.Flush(flushToDisk: true);
            }
            client.Disconnect();
            var length = new FileInfo(downloadPath).Length;
            if (length <= 0 || length > maximumBytes)
            {
                throw new InvalidDataException("The downloaded SFTP payload size is invalid.");
            }
            if (request.SaveCredential)
            {
                WinCredentialStore.Write(parameters.CredentialTarget, parameters.Username, parameters.Password);
            }
            return new VendorBridgeResponse
            {
                Success = true,
                Operation = "DownloadSftp",
                DownloadPath = downloadPath,
                CredentialExists = request.SaveCredential || parameters.UsedStoredCredential,
                Fingerprint = parameters.ExpectedFingerprint,
                Message = "The SFTP payload was downloaded to the isolated per-user cache for signature verification."
            };
        }
        catch
        {
            DeleteScopedTemporaryFile(downloadPath);
            throw;
        }
        finally
        {
            parameters.ClearPasswordReference();
        }
    }

    private static VendorBridgeResponse DownloadHttps(VendorBridgeRequest request)
    {
        var sourceUri = RequireHttpsUri(request.SourceUri);
        var allowedHosts = RequireAllowedHosts(request.AllowedHosts);
        RequireAllowedHost(sourceUri, allowedHosts);
        var maximumBytes = RequireMaximumBytes(request.MaxBytes);
        var fileName = RequireInstallerFileName(Path.GetFileName(sourceUri.LocalPath));
        var downloadPath = GetSafeDownloadPath(request, fileName);

        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AVWorkstationToolkit", "1.1"));
            var currentUri = sourceUri;
            HttpResponseMessage? response = null;
            try
            {
                for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
                {
                    response?.Dispose();
                    response = client.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    if (IsRedirect(response.StatusCode))
                    {
                        if (redirect == MaximumRedirects || response.Headers.Location is null)
                        {
                            throw new HttpRequestException("The vendor download exceeded the redirect limit.");
                        }
                        currentUri = response.Headers.Location.IsAbsoluteUri
                            ? response.Headers.Location
                            : new Uri(currentUri, response.Headers.Location);
                        currentUri = RequireHttpsUri(currentUri.AbsoluteUri);
                        RequireAllowedHost(currentUri, allowedHosts);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    break;
                }
                if (response is null || !response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException("The vendor download did not return a successful response.");
                }
                if (response.Content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > maximumBytes))
                {
                    throw new InvalidDataException("The HTTPS payload size violates the catalogued limit.");
                }
                using var source = response.Content.ReadAsStream();
                using var destination = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, FileOptions.SequentialScan);
                CopyBounded(source, destination, maximumBytes);
                destination.Flush(flushToDisk: true);
            }
            finally
            {
                response?.Dispose();
            }
            return new VendorBridgeResponse
            {
                Success = true,
                Operation = "DownloadHttps",
                DownloadPath = downloadPath,
                Message = "The HTTPS payload was downloaded to the isolated per-user cache for signature verification."
            };
        }
        catch
        {
            DeleteScopedTemporaryFile(downloadPath);
            throw;
        }
    }

    private static SftpParameters GetSftpParameters(VendorBridgeRequest request)
    {
        var host = RequireDnsHost(request.Host);
        var port = RequirePort(request.Port);
        var username = RequireUsername(request.Username);
        var fingerprint = RequireFingerprint(request.ExpectedFingerprint);
        var target = GetCredentialTarget(host, port, username);
        var password = request.Password;
        var usedStored = false;
        if (string.IsNullOrEmpty(password))
        {
            var stored = WinCredentialStore.Read(target) ??
                WinCredentialStore.Read(GetLegacyCredentialTarget(host, port, username))
                ?? throw new InvalidOperationException("No saved credential exists; enter the password or save a tested credential first.");
            if (!stored.Username.Equals(username, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The saved credential username does not match the request.");
            }
            password = stored.Password;
            usedStored = true;
        }
        if (password.Length > 1_280)
        {
            throw new ArgumentException("The SFTP password is too long.");
        }
        return new SftpParameters(host, port, username, password, fingerprint, target, usedStored);
    }

    private static SftpClient CreateTrustedSftpClient(SftpParameters parameters)
    {
        var client = new SftpClient(parameters.Host, parameters.Port, parameters.Username, parameters.Password);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
        client.HostKeyReceived += (_, eventArgs) =>
        {
            var observed = FormatFingerprint(eventArgs.HostKey);
            eventArgs.CanTrust = FixedTimeEquals(observed, parameters.ExpectedFingerprint);
        };
        return client;
    }

    private static string GetSafeDownloadPath(VendorBridgeRequest request, string fileName)
    {
        var dataRoot = SafePath.RequireAbsoluteNonRoot(request.DataRoot, "data root");
        var packageId = RequirePackageId(request.PackageId);
        var version = RequireVersion(request.Version);
        var cacheRoot = Path.GetFullPath(Path.Combine(dataRoot, "vendor-cache"));
        var packageRoot = Path.GetFullPath(Path.Combine(cacheRoot, packageId, version));
        if (!SafePath.IsStrictChild(cacheRoot, packageRoot))
        {
            throw new InvalidDataException("The vendor cache path escaped its data root.");
        }
        Directory.CreateDirectory(packageRoot);
        if (SafePath.ContainsReparsePoint(dataRoot, packageRoot))
        {
            throw new InvalidDataException("The vendor cache path contains an unsupported reparse point.");
        }
        var path = Path.GetFullPath(Path.Combine(packageRoot, fileName + ".download"));
        if (!SafePath.IsStrictChild(packageRoot, path))
        {
            throw new InvalidDataException("The vendor payload path escaped its package cache.");
        }
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The temporary vendor payload is an unsupported reparse point.");
            }
            File.Delete(path);
        }
        return path;
    }

    private static void CopyBounded(Stream source, Stream destination, long maximumBytes)
    {
        var buffer = new byte[131_072];
        long total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("The HTTPS payload exceeded the catalogued size limit.");
            }
            destination.Write(buffer, 0, read);
        }
        if (total == 0)
        {
            throw new InvalidDataException("The HTTPS payload was empty.");
        }
    }

    private static string ReadBoundedStandardInput()
    {
        var builder = new StringBuilder();
        var buffer = new char[4_096];
        while (true)
        {
            var read = Console.In.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            builder.Append(buffer, 0, read);
            if (builder.Length > MaximumRequestCharacters)
            {
                throw new InvalidDataException("The vendor bridge request exceeds the 64 KiB limit.");
            }
        }
        if (builder.Length == 0)
        {
            throw new InvalidDataException("The vendor bridge request was not supplied on standard input.");
        }
        return builder.ToString();
    }

    private static string NormalizeRequestText(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..].TrimStart();
        }
        else if (normalized.StartsWith("\u00EF\u00BB\u00BF", StringComparison.Ordinal))
        {
            // Windows PowerShell 5.1 can surface an UTF-8 BOM through its
            // redirected StreamWriter as the three decoded BOM characters.
            normalized = normalized[3..].TrimStart();
        }
        if (normalized.Length == 0 || normalized[0] != '{')
        {
            throw new InvalidDataException("The vendor bridge request must contain one JSON object.");
        }
        return normalized;
    }

    private static void WriteResponse(VendorBridgeResponse response)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(response, VendorBridgeJsonContext.Default.VendorBridgeResponse));
    }

    private static string SanitizeError(Exception exception)
    {
        var message = exception switch
        {
            Renci.SshNet.Common.SshAuthenticationException => "SFTP authentication failed.",
            Renci.SshNet.Common.SshConnectionException => "The trusted SFTP connection failed.",
            _ => string.IsNullOrWhiteSpace(exception.Message) ? "The vendor operation failed." : exception.Message
        };
        var sanitized = new string(message.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return sanitized.Length <= 1_000 ? sanitized : sanitized[..1_000] + "...[truncated]";
    }

    private static string RequireText(string? value, string label, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl) || value != value.Trim())
        {
            throw new InvalidDataException($"The vendor bridge {label} is invalid.");
        }
        return value;
    }

    private static string RequireDnsHost(string? value)
    {
        var host = RequireText(value, "host", 253);
        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            throw new InvalidDataException("The vendor bridge host must be a DNS name.");
        }
        return host.ToLowerInvariant();
    }

    private static int RequirePort(int port)
    {
        if (port is < 1 or > 65_535)
        {
            throw new InvalidDataException("The vendor bridge port is invalid.");
        }
        return port;
    }

    private static string RequireUsername(string? value) => RequireText(value, "username", 256);

    private static string RequireFingerprint(string? value)
    {
        var fingerprint = RequireText(value, "host fingerprint", 80);
        if (!fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) || fingerprint.Length != 50 ||
            fingerprint[7..].Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '+' or '/')))
        {
            throw new InvalidDataException("The SFTP host fingerprint must use SHA256 OpenSSH format.");
        }
        return fingerprint;
    }

    private static string RequireRemoteRoot(string? value)
    {
        var root = RequireText(value, "remote root", 256).TrimEnd('/');
        if (!root.StartsWith('/') || root.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("The SFTP remote root is invalid.");
        }
        return root;
    }

    private static string RequireRemotePath(string? value, string remoteRoot)
    {
        var path = RequireText(value, "remote path", 1_024);
        if (!path.StartsWith(remoteRoot + '/', StringComparison.Ordinal) || path.Split('/').Any(segment => segment is "." or "..") || path.Contains('\\'))
        {
            throw new InvalidDataException("The SFTP remote path escaped the catalogued root.");
        }
        _ = RequireInstallerFileName(Path.GetFileName(path));
        return path;
    }

    private static Uri RequireHttpsUri(string? value)
    {
        var text = RequireText(value, "HTTPS URI", 2_048);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.DnsSafeHost) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("The vendor bridge URI must use HTTPS without embedded credentials.");
        }
        return uri;
    }

    private static HashSet<string> RequireAllowedHosts(string[]? values)
    {
        if (values is null || values.Length is < 1 or > 16)
        {
            throw new InvalidDataException("The HTTPS download requires one to sixteen allowed hosts.");
        }
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            hosts.Add(RequireDnsHost(value));
        }
        return hosts;
    }

    private static void RequireAllowedHost(Uri uri, HashSet<string> allowedHosts)
    {
        if (!allowedHosts.Contains(uri.DnsSafeHost))
        {
            throw new InvalidDataException("The HTTPS download host is not allowlisted.");
        }
    }

    private static string RequirePackageId(string? value)
    {
        var packageId = RequireText(value, "package ID", 128);
        if (packageId.Length < 2 || !char.IsAsciiLetterOrDigit(packageId[0]) || packageId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '+' or '_' or '.' or '-')))
        {
            throw new InvalidDataException("The vendor bridge package ID is invalid.");
        }
        return packageId;
    }

    private static string RequireVersion(string? value)
    {
        var version = RequireText(value, "version", 64);
        var parts = version.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
        {
            throw new InvalidDataException("The vendor bridge version is invalid.");
        }
        return version;
    }

    private static string RequireInstallerFileName(string? value)
    {
        var fileName = RequireText(value, "installer file name", 255);
        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetExtension(fileName).ToLowerInvariant() is not (".exe" or ".msi" or ".msix" or ".msixbundle"))
        {
            throw new InvalidDataException("The vendor installer file name is invalid.");
        }
        return fileName;
    }

    private static long RequireMaximumBytes(long value)
    {
        if (value is < 1_048_576 or > 4_294_967_296)
        {
            throw new InvalidDataException("The vendor bridge size limit must be from 1 MiB through 4 GiB.");
        }
        return value;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string FormatFingerprint(byte[] hostKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static string GetCredentialTarget(string host, int port, string username) => $"{CredentialPrefix}{host}:{port}:{username}";

    private static string GetLegacyCredentialTarget(string host, int port, string username) => $"{LegacyCredentialPrefix}{host}:{port}:{username}";

    private static void DeleteScopedTemporaryFile(string path)
    {
        try
        {
            if (path.EndsWith(".download", StringComparison.OrdinalIgnoreCase) && File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original operation failure.
        }
    }

    private sealed class LimitedWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            _written = checked(_written + count);
            if (_written > maximumBytes)
            {
                throw new InvalidDataException("The SFTP payload exceeded the catalogued size limit.");
            }
            inner.Write(buffer, offset, count);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Flush();
            }
            base.Dispose(disposing);
        }
    }
}

internal sealed class SftpParameters(
    string host,
    int port,
    string username,
    string password,
    string expectedFingerprint,
    string credentialTarget,
    bool usedStoredCredential)
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Username { get; } = username;
    public string Password { get; private set; } = password;
    public string ExpectedFingerprint { get; } = expectedFingerprint;
    public string CredentialTarget { get; } = credentialTarget;
    public bool UsedStoredCredential { get; } = usedStoredCredential;
    public void ClearPasswordReference() => Password = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class VendorBridgeRequest
{
    public string? Operation { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool SaveCredential { get; set; }
    public string? ExpectedFingerprint { get; set; }
    public string? RemoteRoot { get; set; }
    public string? RemotePath { get; set; }
    public string? SourceUri { get; set; }
    public string[]? AllowedHosts { get; set; }
    public string? DataRoot { get; set; }
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    public long MaxBytes { get; set; }
}

internal sealed class VendorBridgeResponse
{
    public bool Success { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public bool CredentialExists { get; set; }
}

[JsonSerializable(typeof(VendorBridgeRequest))]
[JsonSerializable(typeof(VendorBridgeResponse))]
internal sealed partial class VendorBridgeJsonContext : JsonSerializerContext;
