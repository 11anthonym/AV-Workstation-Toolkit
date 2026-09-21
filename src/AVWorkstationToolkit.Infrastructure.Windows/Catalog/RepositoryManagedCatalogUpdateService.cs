using AVWorkstationToolkit.Application.Catalog;

namespace AVWorkstationToolkit.Infrastructure.Windows.Catalog;

/// <summary>Explicit source-checkout boundary; it never downloads or activates signed production data.</summary>
public sealed class RepositoryManagedCatalogUpdateService(string repositoryRoot) : IManagedCatalogUpdateService
{
    private readonly string root = Path.GetFullPath(repositoryRoot);

    public ManagedCatalogUpdateStatus Status { get; private set; } =
        new(ManagedCatalogUpdateState.NotConfigured, 0, "Development source", "Development source", 0, string.Empty,
            false, false, "Source checkout uses canonical managed-applications.json; signed updates are available only to packaged runtimes.");

    public ManagedCatalogSet LoadActiveOrEmbedded()
    {
        var catalog = new RepositoryCatalogLoader().LoadManaged(root);
        return new(catalog, new(0, "Development source", true, Status.Detail));
    }

    public Task<ManagedCatalogUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Status);
    }

    public Task<ManagedCatalogUpdateStatus> InstallAvailableAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(cancellationToken);
}
