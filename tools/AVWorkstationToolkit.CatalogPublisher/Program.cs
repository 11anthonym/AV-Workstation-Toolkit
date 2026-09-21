using System.Globalization;
using AVWorkstationToolkit.CatalogPublisher;

try
{
    var managed = args.Length > 0 && string.Equals(args[0], "managed", StringComparison.Ordinal);
    var explicitReference = args.Length > 0 && string.Equals(args[0], "reference", StringComparison.Ordinal);
    var publisherArgs = managed || explicitReference ? args[1..] : args;
    if (publisherArgs.Length == 0 || publisherArgs.Contains("--help", StringComparer.Ordinal))
    {
        Console.WriteLine("""
            AVWT signed catalog publisher

            Usage:
              AVWorkstationToolkit.CatalogPublisher [reference] <options>
              AVWorkstationToolkit.CatalogPublisher managed <options>

            Required:
              --version <numeric version> --revision <positive integer>
              --minimum-app-version <numeric version>
              --created-utc <ISO-8601 UTC>
              --signing-key-id <ID> --private-key <absolute PEM path outside repository>
              --public-base-uri <https://host/feed-root/>

            Optional:
              --repository-root <absolute path>       default: current directory
              --output-root <absolute path>           defaults: artifacts/catalog-feed or artifacts/managed-catalog-feed
              --previous-catalog <signed catalog bundle of the selected type>
              --trusted-public-key <key-id=absolute-public-PEM-path> (repeatable)
              --acknowledge-risk                      reference catalog only

            The managed operation emits a local signed managed catalog. It has no
            production trust anchor, upload, activation, or runtime integration.
            """);
        return 0;
    }

    if (managed && (publisherArgs.Contains("--acknowledge-risk", StringComparer.Ordinal) ||
                    publisherArgs.Contains("--trusted-public-key", StringComparer.Ordinal)))
        throw new ArgumentException("Managed catalog publishing does not accept reference risk acknowledgement or additional trust keys.");
    var parsed = Arguments.Parse(publisherArgs, managed);
    if (managed)
    {
        var result = new ManagedCatalogPublisher().Publish(parsed.ManagedOptions);
        Console.WriteLine($"MANAGED_CATALOG_PUBLISH_OK revision={result.Manifest.Revision} version={result.Manifest.CatalogVersion} bundle={result.BundlePath}");
    }
    else
    {
        var result = new ReferenceCatalogPublisher().Publish(parsed.Options);
        Console.WriteLine($"CATALOG_PUBLISH_OK revision={result.Manifest.Revision} version={result.Manifest.CatalogVersion} bundle={result.BundlePath}");
        Console.WriteLine($"CHANGE_ANALYSIS manufacturers=+{result.Analysis.ManufacturersAdded}/-{result.Analysis.ManufacturersRemoved} families=+{result.Analysis.FamiliesAdded}/-{result.Analysis.FamiliesRemoved} models=+{result.Analysis.ExactModelsAdded}/-{result.Analysis.ExactModelsRemoved} aliases=+{result.Analysis.AliasesAdded}/-{result.Analysis.AliasesRemoved} products=+{result.Analysis.ReferenceSoftwareAdded}/-{result.Analysis.ReferenceSoftwareRemoved} relations=+{result.Analysis.RelationsAdded}/-{result.Analysis.RelationsRemoved} unresolved-to-verified={result.Analysis.UnresolvedToVerified}");
        if (result.Analysis.BroadenedRelationIds.Count > 0)
            Console.WriteLine($"BROADENED_RELATIONS: {string.Join(", ", result.Analysis.BroadenedRelationIds)}");
        if (result.Analysis.NarrowedRelationIds.Count > 0)
            Console.WriteLine($"NARROWED_RELATIONS: {string.Join(", ", result.Analysis.NarrowedRelationIds)}");
        foreach (var warning in result.Analysis.Warnings) Console.WriteLine($"WARNING: {warning}");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"CATALOG_PUBLISH_FAILED: {exception.Message}");
    return 1;
}

file sealed class Arguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly List<string> trustedKeys = [];
    private bool acknowledgeRisk;

    public ReferenceCatalogPublishOptions Options { get; private set; } = null!;
    public ManagedCatalogPublishOptions ManagedOptions => new(
        Options.RepositoryRoot,
        Options.OutputRoot,
        Options.CatalogVersion,
        Options.Revision,
        Options.MinimumAppVersion,
        Options.CreatedUtc,
        Options.SigningKeyId,
        Options.PrivateKeyPath,
        Options.PublicBaseUri,
        Options.PreviousCatalogPath);

    public static Arguments Parse(string[] args, bool managed = false)
    {
        var parsed = new Arguments();
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--acknowledge-risk")
            {
                if (parsed.acknowledgeRisk) throw new ArgumentException("Argument '--acknowledge-risk' is repeated.");
                parsed.acknowledgeRisk = true;
                continue;
            }
            if (!Allowed.Contains(name)) throw new ArgumentException($"Unknown argument '{name}'.");
            if (++index >= args.Length) throw new ArgumentException($"Argument '{name}' requires a value.");
            if (name == "--trusted-public-key") parsed.trustedKeys.Add(args[index]);
            else if (!parsed.values.TryAdd(name, args[index])) throw new ArgumentException($"Argument '{name}' is repeated.");
        }

        var repositoryRoot = Path.GetFullPath(parsed.values.GetValueOrDefault("--repository-root", Environment.CurrentDirectory));
        var publicKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var specification in parsed.trustedKeys)
        {
            var separator = specification.IndexOf('=');
            if (separator <= 0 || separator == specification.Length - 1) throw new ArgumentException("Trusted public key must use key-id=absolute-path syntax.");
            var keyId = specification[..separator];
            var path = specification[(separator + 1)..];
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Trusted public-key paths must be absolute.");
            if (!publicKeys.TryAdd(keyId, File.ReadAllText(path))) throw new ArgumentException($"Trusted public key ID '{keyId}' is repeated.");
        }

        parsed.Options = new(
            repositoryRoot,
            Path.GetFullPath(parsed.values.GetValueOrDefault(
                "--output-root",
                Path.Combine(repositoryRoot, "artifacts", managed ? "managed-catalog-feed" : "catalog-feed"))),
            parsed.Required("--version"),
            long.Parse(parsed.Required("--revision"), NumberStyles.None, CultureInfo.InvariantCulture),
            parsed.Required("--minimum-app-version"),
            DateTimeOffset.Parse(parsed.Required("--created-utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            parsed.Required("--signing-key-id"),
            Path.GetFullPath(parsed.Required("--private-key")),
            new Uri(parsed.Required("--public-base-uri"), UriKind.Absolute),
            parsed.values.GetValueOrDefault("--previous-catalog") is { } previous ? Path.GetFullPath(previous) : null,
            publicKeys,
            parsed.acknowledgeRisk);
        return parsed;
    }

    private string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Required argument '{name}' is missing.");

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "--repository-root", "--output-root", "--version", "--revision", "--minimum-app-version", "--created-utc",
        "--signing-key-id", "--private-key", "--public-base-uri", "--previous-catalog", "--trusted-public-key"
    };
}
