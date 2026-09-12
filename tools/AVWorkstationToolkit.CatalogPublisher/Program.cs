using System.Globalization;
using AVWorkstationToolkit.CatalogPublisher;

try
{
    if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
    {
        Console.WriteLine("""
            AVWT signed reference catalog publisher

            Required:
              --version <numeric version> --revision <positive integer>
              --minimum-app-version <numeric version>
              --created-utc <ISO-8601 UTC> --expires-utc <ISO-8601 UTC>
              --signing-key-id <ID> --private-key <absolute PEM path outside repository>
              --public-base-uri <https://host/feed-root/>

            Optional:
              --repository-root <absolute path>       default: current directory
              --output-root <absolute path>           default: <repository>/artifacts/catalog-feed
              --previous-catalog <signed .avwtcatalog>
              --trusted-public-key <key-id=absolute-public-PEM-path> (repeatable)
              --acknowledge-risk                      required for removals or broadened relation scopes
            """);
        return 0;
    }

    var parsed = Arguments.Parse(args);
    var result = new ReferenceCatalogPublisher().Publish(parsed.Options);
    Console.WriteLine($"CATALOG_PUBLISH_OK revision={result.Manifest.Revision} version={result.Manifest.CatalogVersion} bundle={result.BundlePath}");
    Console.WriteLine($"CHANGE_ANALYSIS manufacturers=+{result.Analysis.ManufacturersAdded}/-{result.Analysis.ManufacturersRemoved} families=+{result.Analysis.FamiliesAdded}/-{result.Analysis.FamiliesRemoved} models=+{result.Analysis.ExactModelsAdded}/-{result.Analysis.ExactModelsRemoved} aliases=+{result.Analysis.AliasesAdded}/-{result.Analysis.AliasesRemoved} products=+{result.Analysis.ReferenceSoftwareAdded}/-{result.Analysis.ReferenceSoftwareRemoved} relations=+{result.Analysis.RelationsAdded}/-{result.Analysis.RelationsRemoved} unresolved-to-verified={result.Analysis.UnresolvedToVerified}");
    if (result.Analysis.BroadenedRelationIds.Count > 0)
        Console.WriteLine($"BROADENED_RELATIONS: {string.Join(", ", result.Analysis.BroadenedRelationIds)}");
    if (result.Analysis.NarrowedRelationIds.Count > 0)
        Console.WriteLine($"NARROWED_RELATIONS: {string.Join(", ", result.Analysis.NarrowedRelationIds)}");
    foreach (var warning in result.Analysis.Warnings) Console.WriteLine($"WARNING: {warning}");
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

    public static Arguments Parse(string[] args)
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
            Path.GetFullPath(parsed.values.GetValueOrDefault("--output-root", Path.Combine(repositoryRoot, "artifacts", "catalog-feed"))),
            parsed.Required("--version"),
            long.Parse(parsed.Required("--revision"), NumberStyles.None, CultureInfo.InvariantCulture),
            parsed.Required("--minimum-app-version"),
            DateTimeOffset.Parse(parsed.Required("--created-utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(parsed.Required("--expires-utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
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
        "--expires-utc", "--signing-key-id", "--private-key", "--public-base-uri", "--previous-catalog", "--trusted-public-key"
    };
}
