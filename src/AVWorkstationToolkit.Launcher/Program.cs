using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AVWorkstationToolkit.App;

namespace AVWorkstationToolkit.Launcher;

internal static class Program
{
    private const string ProductName = "AV Workstation Toolkit";
    private const string DataDirectoryName = "AVWorkstationToolkit";
    private const string PayloadResourcePrefix = "AVWorkstationToolkit.Payload.";
    private const uint ErrorIcon = 0x00000010;
    private static readonly string[] RequiredRuntimeFiles =
    [
        "worker/AVWorkstationToolkit.Worker.exe",
        "manifests/managed-applications.json",
        "manifests/external-applications.json",
        "manifests/commercial-av-catalog.json",
        "manifests/software-compatibility.json",
        "manifests/hardware-identities.json",
        "notices/THIRD-PARTY-NOTICES.md",
        "notices/PROJECT-LICENSE.txt",
        "notices/DOTNET-LICENSE.txt",
        "notices/DOTNET-THIRD-PARTY-NOTICES.txt"
    ];
    private static readonly string[] RetiredRuntimeFiles =
    [
        "app/AVWorkstationToolkit.xaml",
        "scripts/Add-AVWorkstationToolkitExternalPackage.ps1",
        "scripts/AppProfiles.psd1",
        "scripts/AVWorkstationToolkit.Core.psd1",
        "scripts/AVWorkstationToolkit.Core.psm1",
        "scripts/AVWorkstationToolkit.Vendor.psm1",
        "scripts/Export-AVWorkstationToolkitBaselineManifest.ps1",
        "scripts/Get-WorkstationSnapshot.ps1",
        "scripts/Invoke-AVWorkstationToolkitAction.ps1",
        "scripts/Invoke-AVWorkstationToolkitDeployment.ps1",
        "scripts/Invoke-AVWorkstationToolkitMaintenance.ps1",
        "scripts/Start-AVWorkstationToolkit.ps1",
        "scripts/Test-DeploymentReadiness.ps1"
    ];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(args);
        }
        catch (Exception exception)
        {
            return Fail(exception.Message, headless: false, dataRoot: null);
        }

        var dataRoot = options.DataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryName);

        try
        {
            dataRoot = SafePath.RequireAbsoluteNonRoot(dataRoot, "data root");
            var integrity = PrepareEmbeddedRuntime(dataRoot);
            var applicationRoot = integrity.ApplicationRoot;
            var workerPath = Path.Combine(applicationRoot, "worker", "AVWorkstationToolkit.Worker.exe");

            if (options.VerificationOutput is not null || options.DiagnosticsOutput is not null)
            {
                var outputPath = options.VerificationOutput ?? options.DiagnosticsOutput!;
                WriteDiagnosticResult(
                    outputPath,
                    integrity,
                    applicationRoot,
                    workerPath,
                    dataRoot,
                    IsElevated());
                return integrity.Success && File.Exists(workerPath) ? 0 : 1;
            }

            if (!integrity.Success)
            {
                return Fail(integrity.Message, headless: false, dataRoot);
            }
            if (IsElevated())
            {
                return Fail(
                    "For safety, AV Workstation Toolkit must be launched as a standard user. Close this copy and start it normally; individual installers can request elevation through Windows.",
                    headless: false,
                    dataRoot);
            }
            Directory.CreateDirectory(Path.Combine(dataRoot, "logs", "requests"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "reports"));
            if (!File.Exists(workerPath))
                return Fail("The packaged compiled worker is missing.", headless: false, dataRoot);
            if (options.SmokeTest)
                return RunCompiledApp(new AVWorkstationToolkit.App.App());

            var context = new PackagedAppStartupContext(
                applicationRoot,
                dataRoot,
                ProductVersion,
                GetFileHash(workerPath),
                SmokeTest: options.ProductionSmoke,
                Prerelease: ProductPrerelease);
            return RunCompiledApp(new AVWorkstationToolkit.App.App(context));
        }
        catch (Exception exception)
        {
            return Fail(exception.Message, options.IsHeadless, dataRoot);
        }
    }

    private static int RunCompiledApp(AVWorkstationToolkit.App.App app)
    {
        app.InitializeComponent();
        return app.Run();
    }

    private static PayloadIntegrity PrepareEmbeddedRuntime(string dataRoot)
    {
        var applicationRoot = Path.Combine(dataRoot, "runtime", ProductVersion);
        var verified = 0;
        try
        {
            Directory.CreateDirectory(applicationRoot);
            if (SafePath.ContainsReparsePoint(dataRoot, applicationRoot))
            {
                return new(false, "The AV Workstation Toolkit runtime directory is an unsupported reparse point.", verified, 0, applicationRoot);
            }

            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(PayloadResourcePrefix, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (resourceNames.Length == 0)
            {
                return new(false, "The AV Workstation Toolkit executable contains no embedded runtime payload.", verified, 0, applicationRoot);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var resourceName in resourceNames)
            {
                var encodedPath = resourceName[PayloadResourcePrefix.Length..];
                var segments = encodedPath.Split('/', StringSplitOptions.None);
                if (segments.Length < 2 || segments.Any(segment =>
                        string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.Contains(':')) ||
                    encodedPath.Contains('\\'))
                {
                    return new(false, $"The embedded AV Workstation Toolkit payload contains an invalid path: {encodedPath}", verified, 0, applicationRoot);
                }

                var normalizedPath = string.Join('/', segments);
                if (!seen.Add(normalizedPath))
                {
                    return new(false, $"The embedded AV Workstation Toolkit payload contains a duplicate path: {normalizedPath}", verified, 0, applicationRoot);
                }

                var fullPath = Path.GetFullPath(Path.Combine(
                    applicationRoot,
                    normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!SafePath.IsStrictChild(applicationRoot, fullPath))
                {
                    return new(false, $"An embedded AV Workstation Toolkit file resolves outside the runtime directory: {normalizedPath}", verified, 0, applicationRoot);
                }

                var parentPath = Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidOperationException($"Embedded payload path has no parent: {normalizedPath}");
                Directory.CreateDirectory(parentPath);
                if (SafePath.ContainsReparsePoint(dataRoot, parentPath) ||
                    (File.Exists(fullPath) && SafePath.ContainsReparsePoint(dataRoot, fullPath)))
                {
                    return new(false, $"An AV Workstation Toolkit runtime path contains an unsupported reparse point: {normalizedPath}", verified, 0, applicationRoot);
                }

                using var resourceStream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Embedded payload stream is unavailable: {normalizedPath}");
                using var memory = new MemoryStream();
                resourceStream.CopyTo(memory);
                var content = memory.ToArray();
                var expectedHash = Convert.ToHexString(SHA256.HashData(content));

                var existingHash = File.Exists(fullPath)
                    ? GetFileHash(fullPath)
                    : string.Empty;
                if (!existingHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    var temporaryPath = Path.Combine(
                        parentPath,
                        $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.WriteAllBytes(temporaryPath, content);
                        File.Move(temporaryPath, fullPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                }

                if (!GetFileHash(fullPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return new(false, $"An embedded AV Workstation Toolkit file failed cache verification: {normalizedPath}", verified, 0, applicationRoot);
                }
                verified++;
            }

            var retiredFilesRemoved = RemoveRetiredRuntimeFiles(dataRoot, applicationRoot);

            foreach (var requiredPath in RequiredRuntimeFiles)
            {
                if (!seen.Contains(requiredPath) ||
                    !File.Exists(Path.Combine(applicationRoot, requiredPath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return new(false, $"The embedded AV Workstation Toolkit runtime is missing a required file: {requiredPath}", verified, retiredFilesRemoved, applicationRoot);
                }
            }

            return new(
                true,
                $"Verified {verified} embedded AV Workstation Toolkit runtime files and removed {retiredFilesRemoved} retired runtime files.",
                verified,
                retiredFilesRemoved,
                applicationRoot);
        }
        catch (Exception exception)
        {
            return new(false, $"The embedded AV Workstation Toolkit runtime could not be prepared: {exception.Message}", verified, 0, applicationRoot);
        }
    }

    private static int RemoveRetiredRuntimeFiles(string dataRoot, string applicationRoot)
    {
        var removed = 0;
        foreach (var relativePath in RetiredRuntimeFiles)
        {
            var fullPath = Path.GetFullPath(Path.Combine(applicationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!SafePath.IsStrictChild(applicationRoot, fullPath))
                throw new IOException($"A retired runtime path escaped the application root: {relativePath}");
            if (!File.Exists(fullPath)) continue;
            if (SafePath.ContainsReparsePoint(dataRoot, fullPath))
                throw new IOException($"A retired runtime path contains an unsupported reparse point: {relativePath}");
            File.Delete(fullPath);
            removed++;
        }
        foreach (var directoryName in new[] { "app", "scripts" })
        {
            var directory = Path.Combine(applicationRoot, directoryName);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                if (SafePath.ContainsReparsePoint(dataRoot, directory))
                    throw new IOException($"A retired runtime directory contains an unsupported reparse point: {directoryName}");
                Directory.Delete(directory);
            }
        }
        return removed;
    }

    private static string GetFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteDiagnosticResult(
        string outputPath,
        PayloadIntegrity integrity,
        string applicationRoot,
        string workerPath,
        string dataRoot,
        bool elevated)
    {
        outputPath = SafePath.RequireAbsoluteNonRoot(outputPath, "diagnostic output path");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("SchemaVersion", 3);
        writer.WriteString("Product", ProductName);
        writer.WriteString("Version", ProductVersion);
        writer.WriteString("RuntimeFramework", RuntimeInformation.FrameworkDescription);
        writer.WriteString("RuntimeVersion", Environment.Version.ToString());
        writer.WriteString("RuntimeArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteBoolean("Success", integrity.Success && File.Exists(workerPath));
        writer.WriteString("IntegrityMessage", integrity.Message);
        writer.WriteNumber("FilesVerified", integrity.FilesVerified);
        writer.WriteNumber("RetiredRuntimeFilesRemoved", integrity.RetiredFilesRemoved);
        writer.WriteString("ApplicationRoot", applicationRoot);
        writer.WriteString("FrontendPath", Environment.ProcessPath ?? string.Empty);
        writer.WriteBoolean("FrontendPresent", true);
        writer.WriteString("FrontendArchitecture", "Compiled C# WPF");
        writer.WriteString("WorkerPath", workerPath);
        writer.WriteBoolean("WorkerPresent", File.Exists(workerPath));
        writer.WriteString("WorkerSha256", File.Exists(workerPath) ? GetFileHash(workerPath) : string.Empty);
        writer.WriteBoolean("LegacyRecoveryPresent", false);
        writer.WriteString("DataRoot", dataRoot);
        writer.WriteBoolean("Elevated", elevated);
        writer.WriteEndObject();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Fail(string message, bool headless, string? dataRoot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(dataRoot))
            {
                Directory.CreateDirectory(dataRoot);
                File.AppendAllText(
                    Path.Combine(dataRoot, "launcher-error.log"),
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Failure reporting must never hide the original launch error.
        }
        if (!headless)
        {
            _ = MessageBoxW(0, message, $"{ProductName} could not start", ErrorIcon);
        }
        return 1;
    }

    private static string ProductVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    // A beta build is stamped "1.1.1-beta.2+commit"; only the label is taken, and only for display.
    private static string ProductPrerelease =>
        AVWorkstationToolkit.Application.Diagnostics.ProductRelease.FromInformationalVersion(
            ProductVersion,
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion).Prerelease;

    private sealed record PayloadIntegrity(
        bool Success,
        string Message,
        int FilesVerified,
        int RetiredFilesRemoved,
        string ApplicationRoot);

    private sealed class LaunchOptions
    {
        public bool SmokeTest { get; private set; }
        public bool ProductionSmoke { get; private set; }
        public string? DataRoot { get; private set; }
        public string? VerificationOutput { get; private set; }
        public string? DiagnosticsOutput { get; private set; }
        public bool IsHeadless => VerificationOutput is not null || DiagnosticsOutput is not null;

        public static LaunchOptions Parse(string[] args)
        {
            var options = new LaunchOptions();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "--smoke-test":
                        options.SmokeTest = true;
                        break;
                    case "--production-smoke":
                        options.ProductionSmoke = true;
                        break;
                    case "--data-root":
                        options.DataRoot = RequireValue(args, ref index, "--data-root");
                        break;
                    case "--verify":
                        options.VerificationOutput = RequireValue(args, ref index, "--verify");
                        break;
                    case "--diagnostics":
                        options.DiagnosticsOutput = RequireValue(args, ref index, "--diagnostics");
                        break;
                    default:
                        throw new ArgumentException($"Unsupported AV Workstation Toolkit launcher argument: {args[index]}");
                }
            }

            if (options.VerificationOutput is not null && options.DiagnosticsOutput is not null)
            {
                throw new ArgumentException("Choose either --verify or --diagnostics, not both.");
            }
            if (options.SmokeTest && options.ProductionSmoke)
                throw new ArgumentException("Choose either --smoke-test or --production-smoke, not both.");
            if (options.DataRoot is not null && !options.SmokeTest && !options.IsHeadless)
            {
                throw new ArgumentException("A custom data root is available only for smoke tests and diagnostics.");
            }
            return options;
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            index++;
            if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{option} requires a value.");
            }
            return args[index];
        }
    }
}
