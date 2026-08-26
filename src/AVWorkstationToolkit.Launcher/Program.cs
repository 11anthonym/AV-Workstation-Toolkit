using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace AVWorkstationToolkit.Launcher;

internal static class Program
{
    private const string ProductName = "AV Workstation Toolkit";
    private const string DataDirectoryName = "AVWorkstationToolkit";
    private const string PayloadResourcePrefix = "AVWorkstationToolkit.Payload.";
    private const uint ErrorIcon = 0x00000010;
    private static readonly string[] RequiredRuntimeFiles =
    [
        "app/AVWorkstationToolkit.xaml",
        "scripts/Start-AVWorkstationToolkit.ps1",
        "scripts/AVWorkstationToolkit.Core.psd1",
        "scripts/AVWorkstationToolkit.Core.psm1",
        "scripts/AVWorkstationToolkit.Vendor.psm1",
        "scripts/AppProfiles.psd1",
        "scripts/Invoke-AVWorkstationToolkitAction.ps1",
        "manifests/external-applications.json",
        "manifests/commercial-av-catalog.json",
        "notices/THIRD-PARTY-NOTICES.md",
        "notices/PROJECT-LICENSE.txt",
        "notices/DOTNET-LICENSE.txt",
        "notices/DOTNET-THIRD-PARTY-NOTICES.txt"
    ];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("--vendor-bridge", StringComparison.OrdinalIgnoreCase))
        {
            return VendorBridge.Run();
        }

        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(args);
        }
        catch (Exception exception)
        {
            return Fail(exception.Message, headless: false, dataRoot: null);
        }

        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var dataRoot = options.DataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryName);

        try
        {
            dataRoot = SafePath.RequireAbsoluteNonRoot(dataRoot, "data root");
            var integrity = PrepareEmbeddedRuntime(dataRoot);
            var applicationRoot = integrity.ApplicationRoot;
            var scriptPath = Path.Combine(applicationRoot, "scripts", "Start-AVWorkstationToolkit.ps1");

            if (options.VerificationOutput is not null || options.DiagnosticsOutput is not null)
            {
                var outputPath = options.VerificationOutput ?? options.DiagnosticsOutput!;
                WriteDiagnosticResult(
                    outputPath,
                    integrity,
                    applicationRoot,
                    scriptPath,
                    powershellPath,
                    dataRoot,
                    IsElevated());
                return integrity.Success && File.Exists(scriptPath) && File.Exists(powershellPath) ? 0 : 1;
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
            if (!File.Exists(scriptPath))
            {
                return Fail($"The packaged frontend is missing: {scriptPath}", headless: false, dataRoot);
            }
            if (!File.Exists(powershellPath))
            {
                return Fail("Windows PowerShell 5.1 is required but was not found in the Windows system directory.", headless: false, dataRoot);
            }

            Directory.CreateDirectory(Path.Combine(dataRoot, "logs", "requests"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "reports"));

            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                WorkingDirectory = applicationRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in BuildPowerShellArguments(scriptPath, dataRoot, options))
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["AVWORKSTATIONTOOLKIT_DATA_ROOT"] = dataRoot;
            startInfo.Environment["AVWORKSTATIONTOOLKIT_PACKAGED"] = "1";
            startInfo.Environment["AVWORKSTATIONTOOLKIT_DISTRIBUTION_ROOT"] = Path.GetFullPath(AppContext.BaseDirectory);
            startInfo.Environment["AVWORKSTATIONTOOLKIT_LAUNCHER_PATH"] = Environment.ProcessPath
                ?? throw new InvalidOperationException("The packaged launcher path could not be resolved.");
            startInfo.Environment["AVWORKSTATIONTOOLKIT_LAUNCHER_VERSION"] = ProductVersion;
            startInfo.Environment["AVWORKSTATIONTOOLKIT_LAUNCHER_RUNTIME"] = RuntimeInformation.FrameworkDescription;
            startInfo.Environment["AVWORKSTATIONTOOLKIT_LAUNCHER_RUNTIME_VERSION"] = Environment.Version.ToString();
            startInfo.Environment["AVWORKSTATIONTOOLKIT_LAUNCHER_ARCHITECTURE"] = RuntimeInformation.ProcessArchitecture.ToString();

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the AV Workstation Toolkit frontend process.");
            if (options.WaitForExit || options.SmokeTest)
            {
                process.WaitForExit();
                return process.ExitCode;
            }
            return 0;
        }
        catch (Exception exception)
        {
            return Fail(exception.Message, options.IsHeadless, dataRoot);
        }
    }

    private static IReadOnlyList<string> BuildPowerShellArguments(
        string scriptPath,
        string dataRoot,
        LaunchOptions options)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy", "RemoteSigned",
            "-STA",
            "-File", scriptPath,
            "-DataRoot", dataRoot
        };
        if (options.SmokeTest)
        {
            arguments.Add("-SmokeTest");
        }
        if (options.RenderPreviewPath is not null)
        {
            arguments.Add("-RenderPreviewPath");
            arguments.Add(options.RenderPreviewPath);
        }
        if (options.RenderWidth > 0)
        {
            arguments.Add("-RenderWidth");
            arguments.Add(options.RenderWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (options.RenderHeight > 0)
        {
            arguments.Add("-RenderHeight");
            arguments.Add(options.RenderHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return arguments;
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
                return new(false, "The AV Workstation Toolkit runtime directory is an unsupported reparse point.", verified, applicationRoot);
            }

            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(PayloadResourcePrefix, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (resourceNames.Length == 0)
            {
                return new(false, "The AV Workstation Toolkit executable contains no embedded runtime payload.", verified, applicationRoot);
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
                    return new(false, $"The embedded AV Workstation Toolkit payload contains an invalid path: {encodedPath}", verified, applicationRoot);
                }

                var normalizedPath = string.Join('/', segments);
                if (!seen.Add(normalizedPath))
                {
                    return new(false, $"The embedded AV Workstation Toolkit payload contains a duplicate path: {normalizedPath}", verified, applicationRoot);
                }

                var fullPath = Path.GetFullPath(Path.Combine(
                    applicationRoot,
                    normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!SafePath.IsStrictChild(applicationRoot, fullPath))
                {
                    return new(false, $"An embedded AV Workstation Toolkit file resolves outside the runtime directory: {normalizedPath}", verified, applicationRoot);
                }

                var parentPath = Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidOperationException($"Embedded payload path has no parent: {normalizedPath}");
                Directory.CreateDirectory(parentPath);
                if (SafePath.ContainsReparsePoint(dataRoot, parentPath) ||
                    (File.Exists(fullPath) && SafePath.ContainsReparsePoint(dataRoot, fullPath)))
                {
                    return new(false, $"An AV Workstation Toolkit runtime path contains an unsupported reparse point: {normalizedPath}", verified, applicationRoot);
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
                    return new(false, $"An embedded AV Workstation Toolkit file failed cache verification: {normalizedPath}", verified, applicationRoot);
                }
                verified++;
            }

            foreach (var requiredPath in RequiredRuntimeFiles)
            {
                if (!seen.Contains(requiredPath) ||
                    !File.Exists(Path.Combine(applicationRoot, requiredPath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return new(false, $"The embedded AV Workstation Toolkit runtime is missing a required file: {requiredPath}", verified, applicationRoot);
                }
            }

            return new(true, $"Verified {verified} embedded AV Workstation Toolkit runtime files.", verified, applicationRoot);
        }
        catch (Exception exception)
        {
            return new(false, $"The embedded AV Workstation Toolkit runtime could not be prepared: {exception.Message}", verified, applicationRoot);
        }
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
        string scriptPath,
        string powershellPath,
        string dataRoot,
        bool elevated)
    {
        outputPath = SafePath.RequireAbsoluteNonRoot(outputPath, "diagnostic output path");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("SchemaVersion", 2);
        writer.WriteString("Product", ProductName);
        writer.WriteString("Version", ProductVersion);
        writer.WriteString("RuntimeFramework", RuntimeInformation.FrameworkDescription);
        writer.WriteString("RuntimeVersion", Environment.Version.ToString());
        writer.WriteString("RuntimeArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteBoolean("Success", integrity.Success && File.Exists(scriptPath) && File.Exists(powershellPath));
        writer.WriteString("IntegrityMessage", integrity.Message);
        writer.WriteNumber("FilesVerified", integrity.FilesVerified);
        writer.WriteString("ApplicationRoot", applicationRoot);
        writer.WriteString("FrontendPath", scriptPath);
        writer.WriteBoolean("FrontendPresent", File.Exists(scriptPath));
        writer.WriteString("PowerShellPath", powershellPath);
        writer.WriteBoolean("PowerShellPresent", File.Exists(powershellPath));
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

    private sealed record PayloadIntegrity(
        bool Success,
        string Message,
        int FilesVerified,
        string ApplicationRoot);

    private sealed class LaunchOptions
    {
        public bool SmokeTest { get; private set; }
        public bool WaitForExit { get; private set; }
        public string? RenderPreviewPath { get; private set; }
        public int RenderWidth { get; private set; }
        public int RenderHeight { get; private set; }
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
                    case "--wait":
                        options.WaitForExit = true;
                        break;
                    case "--render-preview":
                        options.RenderPreviewPath = RequireValue(args, ref index, "--render-preview");
                        break;
                    case "--render-width":
                        options.RenderWidth = RequireDimension(args, ref index, "--render-width");
                        break;
                    case "--render-height":
                        options.RenderHeight = RequireDimension(args, ref index, "--render-height");
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
            if ((options.RenderPreviewPath is not null || options.RenderWidth > 0 || options.RenderHeight > 0) && !options.SmokeTest)
            {
                throw new ArgumentException("Preview options require --smoke-test.");
            }
            if (options.RenderPreviewPath is not null)
            {
                options.RenderPreviewPath = SafePath.RequireAbsoluteNonRoot(options.RenderPreviewPath, "preview output path");
            }
            if (options.WaitForExit && !options.SmokeTest)
            {
                throw new ArgumentException("--wait is available only with --smoke-test.");
            }
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

        private static int RequireDimension(string[] args, ref int index, string option)
        {
            var value = RequireValue(args, ref index, option);
            if (!int.TryParse(value, out var dimension) || dimension < 600 || dimension > 8192)
            {
                throw new ArgumentException($"{option} must be an integer from 600 through 8192.");
            }
            return dimension;
        }
    }
}
