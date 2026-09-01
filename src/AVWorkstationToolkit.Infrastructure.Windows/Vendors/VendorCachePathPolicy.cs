using AVWorkstationToolkit.Application.Vendors;

namespace AVWorkstationToolkit.Infrastructure.Windows.Vendors;

public sealed class VendorCachePathPolicy
{
    private readonly Func<string, bool> isReparsePoint;

    public VendorCachePathPolicy() : this(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { }

    internal VendorCachePathPolicy(Func<string, bool> isReparsePoint) =>
        this.isReparsePoint = isReparsePoint ?? throw new ArgumentNullException(nameof(isReparsePoint));

    public string GetTemporaryPayloadPath(string explicitDataRoot, VendorDeliveryAuthorization authorization, string fileName)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var dataRoot = RequireAbsoluteNonRoot(explicitDataRoot);
        var cacheRoot = Path.Combine(dataRoot, "vendor-cache");
        var packageRoot = Path.GetFullPath(Path.Combine(cacheRoot, authorization.PackageId, authorization.Version));
        if (!IsStrictChild(cacheRoot, packageRoot)) throw new IOException("The vendor cache path escaped its data root.");
        Directory.CreateDirectory(packageRoot);
        RejectReparseChain(dataRoot, packageRoot);
        var validatedName = RequireInstallerFileName(fileName);
        var path = Path.GetFullPath(Path.Combine(packageRoot, validatedName + ".download"));
        if (!IsStrictChild(packageRoot, path)) throw new IOException("The vendor payload path escaped its package cache.");
        if (File.Exists(path)) throw new IOException("A temporary vendor payload already exists.");
        return path;
    }

    public string ValidateTemporaryPayload(string explicitDataRoot, VendorDeliveryAuthorization authorization, string path)
    {
        var expected = GetExpectedTemporaryPayloadPath(explicitDataRoot, authorization, Path.GetFileNameWithoutExtension(path));
        var full = Path.GetFullPath(path);
        if (!string.Equals(expected, full, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new IOException("The temporary vendor payload is not the canonical cache file.");
        RejectReparseChain(RequireAbsoluteNonRoot(explicitDataRoot), full);
        return full;
    }

    public string ValidateCachedPayload(string explicitDataRoot, VendorDeliveryAuthorization authorization, string path)
    {
        var full = Path.GetFullPath(path);
        var fileName = RequireInstallerFileName(Path.GetFileName(full));
        var expectedTemporary = GetExpectedTemporaryPayloadPath(explicitDataRoot, authorization, fileName);
        var expected = expectedTemporary[..^".download".Length];
        if (!string.Equals(expected, full, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new IOException("The cached vendor payload is not the canonical package/version file.");
        RejectReparseChain(RequireAbsoluteNonRoot(explicitDataRoot), full);
        return full;
    }

    public string GetCachedPayloadPath(string explicitDataRoot, VendorDeliveryAuthorization authorization, string fileName)
    {
        var expectedTemporary = GetExpectedTemporaryPayloadPath(explicitDataRoot, authorization, RequireInstallerFileName(fileName));
        var path = expectedTemporary[..^".download".Length];
        var root = RequireAbsoluteNonRoot(explicitDataRoot);
        if (File.Exists(path) || Directory.Exists(Path.GetDirectoryName(path)!)) RejectReparseChain(root, path);
        return path;
    }

    private static string GetExpectedTemporaryPayloadPath(string explicitDataRoot, VendorDeliveryAuthorization authorization, string fileName)
    {
        var root = RequireAbsoluteNonRoot(explicitDataRoot);
        return Path.GetFullPath(Path.Combine(root, "vendor-cache", authorization.PackageId, authorization.Version,
            RequireInstallerFileName(fileName) + ".download"));
    }

    internal static string RequireInstallerFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value != Path.GetFileName(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetExtension(value).ToLowerInvariant() is not (".exe" or ".msi" or ".msix" or ".msixbundle"))
            throw new InvalidDataException("The vendor installer file name is invalid.");
        return value;
    }

    private static string RequireAbsoluteNonRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 1_024 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
            throw new IOException("The vendor data root must be a bounded absolute path.");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) throw new IOException("The vendor data root cannot be a filesystem root.");
        return full;
    }

    private static bool IsStrictChild(string parent, string candidate) =>
        Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void RejectReparseChain(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(candidate);
        if (!string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase) && !IsStrictChild(fullRoot, current))
            throw new IOException("The vendor cache path escaped its data root.");
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && isReparsePoint(current))
                throw new IOException("The vendor cache path contains an unsupported reparse point.");
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), fullRoot, StringComparison.OrdinalIgnoreCase)) return;
            current = Directory.GetParent(current)?.FullName ?? throw new IOException("The vendor cache path is not contained.");
        }
    }
}
