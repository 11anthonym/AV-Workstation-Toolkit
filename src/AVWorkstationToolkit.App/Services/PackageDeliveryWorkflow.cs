using System.Runtime.InteropServices;
using System.Security;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AVWorkstationToolkit.Application.Details;
using AVWorkstationToolkit.Application.Planning;
using AVWorkstationToolkit.Application.Providers;
using AVWorkstationToolkit.Application.Vendors;
using AVWorkstationToolkit.Domain.Catalog;
using AVWorkstationToolkit.Domain.Planning;
using AVWorkstationToolkit.Infrastructure.Windows.Vendors;

namespace AVWorkstationToolkit.App.Services;

public sealed record PackageDeliveryOutcome(bool Completed, string Detail);

public interface IPackageDeliveryWorkflow
{
    bool CanHandle(PackageState state, WorkstationPlan plan);
    Task<PackageDeliveryOutcome> DeliverAsync(PackageState state, WorkstationPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>
/// User-driven external package handoff. It can open validated official pages,
/// download and verify catalog-authorized payloads, and reveal verified cache
/// files. It cannot execute installers or grant managed action authority.
/// </summary>
public sealed class PackageDeliveryWorkflow(
    PackageCatalog catalog,
    VendorInteractionCoordinator vendors,
    IValidatedUserHandoffService handoffs,
    VendorCachePathPolicy cachePaths,
    string dataRoot) : IPackageDeliveryWorkflow
{
    public bool CanHandle(PackageState state, WorkstationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);
        var package = state.Package;
        if (package.Provider != ProviderKind.External || package.DeliveryMode is DeliveryMode.None or DeliveryMode.InventoryOnly) return false;
        return package.Authority is CatalogAuthority.OperationalExternal or CatalogAuthority.AwarenessOnly;
    }

    public async Task<PackageDeliveryOutcome> DeliverAsync(PackageState state, WorkstationPlan plan, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(state, plan)) return new(false, "This app doesn't have an approved vendor page or download.");
        var package = state.Package;
        ExternalReleaseEvidence? release = null;
        plan.ExternalReleases?.TryGetValue(package.Id, out release);
        return package.DeliveryMode switch
        {
            DeliveryMode.DirectDownload => await DownloadHttpsAsync(package, state, release, cancellationToken).ConfigureAwait(true),
            DeliveryMode.AuthenticatedSftp or DeliveryMode.ParentProvider =>
                await DownloadSftpAsync(package, release, cancellationToken).ConfigureAwait(true),
            DeliveryMode.VendorPage or DeliveryMode.Awareness or DeliveryMode.Bundled => OpenOfficial(package),
            _ => new(false, "This app doesn't have an approved vendor page or download.")
        };
    }

    private PackageDeliveryOutcome OpenOfficial(PackageDefinition package)
    {
        handoffs.OpenOfficialUri(OpenOfficialUriIntent.FromDeliveryCatalog(package));
        return new(true, $"Opened the official vendor page for {package.Name}. AVWT didn't run an installer.");
    }

    private async Task<PackageDeliveryOutcome> DownloadHttpsAsync(
        PackageDefinition package,
        PackageState state,
        ExternalReleaseEvidence? release,
        CancellationToken cancellationToken)
    {
        if (release is null || !release.OnlineAvailable || string.IsNullOrWhiteSpace(release.DownloadUri) ||
            !Uri.TryCreate(release.DownloadUri, UriKind.Absolute, out var uri))
            return OpenOfficial(package);
        var version = state.AvailableVersion;
        var authorization = VendorDeliveryAuthorization.ForHttps(package, version, uri);
        var cachedPath = cachePaths.GetCachedPayloadPath(dataRoot, authorization, Path.GetFileName(uri.LocalPath));
        if (File.Exists(cachedPath))
        {
            var cached = vendors.ResolveCached(authorization, cachedPath);
            if (cached.State == VendorPayloadState.Verified)
            {
                handoffs.RevealVerifiedPayload(authorization, cached, dataRoot);
                return new(true, $"Opened the saved download for {package.Name} in File Explorer. AVWT didn't run it.");
            }
        }
        if (MessageBox.Show(
                $"Download {package.Name} {version} from the approved vendor website? AVWT will check the file's integrity and publisher, save it, and open its folder. AVWT won't run it.",
                "Download package", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return new(false, "Download cancelled. No connection was made.");
        var downloaded = await vendors.DownloadHttpsAndVerifyAsync(authorization, cancellationToken).ConfigureAwait(true);
        if (downloaded.State != VendorPayloadState.Verified) return new(false, downloaded.Detail);
        handoffs.RevealVerifiedPayload(authorization, downloaded, dataRoot);
        return new(true, $"Downloaded and checked {package.Name}. AVWT opened its folder but didn't run the file.");
    }

    private async Task<PackageDeliveryOutcome> DownloadSftpAsync(
        PackageDefinition package,
        ExternalReleaseEvidence? release,
        CancellationToken cancellationToken)
    {
        var provider = package.DeliveryMode == DeliveryMode.ParentProvider ? catalog.GetRequired(package.ParentProviderId) : package;
        if (release is null || !release.OnlineAvailable || release.Products.Count == 0)
            return new(false, "The vendor's approved product list isn't available. Refresh and try again.");
        if (MessageBox.Show(
                $"Download {package.Name} using your vendor account? AVWT will confirm the vendor server, check the file's publisher, and save the file. AVWT won't run it.",
                "Sign in to vendor download", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return new(false, "Download cancelled. Your saved sign-in wasn't used.");
        var product = package.DeliveryMode == DeliveryMode.ParentProvider
            ? release.Products.Single()
            : SelectProduct(release.Products);
        if (product is null) return new(false, "No product was selected.");
        var policy = provider.DeliveryPolicy ?? throw new InvalidOperationException("The parent provider delivery policy is unavailable.");
        var endpoint = new VendorEndpoint(policy.Host, policy.Port);
        var observed = await vendors.ProbeSftpHostAsync(endpoint, cancellationToken).ConfigureAwait(true);
        var trusted = vendors.ReadTrustedHost(endpoint);
        if (trusted is null)
        {
            if (!ConfirmTrust(endpoint, observed, changed: false)) return new(false, "The vendor server wasn't approved.");
            trusted = vendors.TrustHost(endpoint, observed);
        }
        else if (!VendorSftpDeliveryService.FixedTimeEquals(trusted.Fingerprint, observed))
        {
            if (!ConfirmTrust(endpoint, observed, changed: true)) return new(false, "The changed vendor server identity wasn't approved.");
            trusted = vendors.TrustHost(endpoint, observed);
        }

        while (true)
        {
            var prompt = ShowCredentialPrompt(product, endpoint, observed);
            if (prompt is null) return new(false, "Download cancelled. Your saved sign-in wasn't used.");
            var authorization = VendorDeliveryAuthorization.ForSftp(package, provider, product, prompt.Username, trusted);
            if (prompt.ForgetSaved)
            {
                var removed = vendors.DeleteCredential(authorization.SftpIdentity!);
                MessageBox.Show(removed ? "Saved vendor sign-in removed." : "No saved sign-in was found for this account and server.",
                    "Vendor sign-in", MessageBoxButton.OK, MessageBoxImage.Information);
                prompt.Dispose();
                continue;
            }
            var cachedPath = cachePaths.GetCachedPayloadPath(dataRoot, authorization, product.FileName);
            if (File.Exists(cachedPath))
            {
                var cached = vendors.ResolveCached(authorization, cachedPath);
                if (cached.State == VendorPayloadState.Verified)
                {
                    prompt.Dispose();
                    handoffs.RevealVerifiedPayload(authorization, cached, dataRoot);
                    return new(true, $"Opened the saved download for {package.Name} in File Explorer. AVWT didn't run it.");
                }
            }
            using (prompt)
            using (var credential = prompt.Secret.Length == 0 ? null : new VendorCredential(prompt.Username, prompt.TakeSecret()))
            {
                var downloaded = await vendors.DownloadSftpAndVerifyAsync(authorization, credential, prompt.SaveCredential, cancellationToken).ConfigureAwait(true);
                if (downloaded.State != VendorPayloadState.Verified) return new(false, downloaded.Detail);
                handoffs.RevealVerifiedPayload(authorization, downloaded, dataRoot);
                return new(true, $"Downloaded and checked {package.Name} {product.Version}. AVWT opened its folder but didn't run the file.");
            }
        }
    }

    private static bool ConfirmTrust(VendorEndpoint endpoint, string fingerprint, bool changed)
    {
        var prefix = changed
            ? "The vendor server's identity has changed. Do not continue unless the vendor or your administrator confirmed this change."
            : "This is the first connection to this vendor server. Confirm its identity before signing in.";
        return MessageBox.Show($"{prefix}\n\nServer: {endpoint.Host}:{endpoint.Port}\nServer fingerprint:\n{fingerprint}\n\nApprove this server for your Windows account?",
            changed ? "Vendor server identity changed" : "Confirm vendor server", MessageBoxButton.YesNo,
            changed ? MessageBoxImage.Error : MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static VendorCatalogProduct? SelectProduct(IReadOnlyList<VendorCatalogProduct> products)
    {
        var list = new ListBox { ItemsSource = products, DisplayMemberPath = nameof(VendorCatalogProduct.Name), MinHeight = 240 };
        var ok = new Button { Content = "Continue", IsDefault = true, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var panel = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list);
        var window = new Window { Title = "Choose a vendor package", Content = panel, Width = 560, Height = 420, Owner = System.Windows.Application.Current?.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        return window.ShowDialog() == true ? (VendorCatalogProduct?)list.SelectedItem : null;
    }

    private static CredentialPrompt? ShowCredentialPrompt(VendorCatalogProduct product, VendorEndpoint endpoint, string fingerprint)
    {
        var username = new TextBox { MaxLength = 256, Margin = new Thickness(0, 4, 0, 10) };
        var password = new PasswordBox { MaxLength = 1280, Margin = new Thickness(0, 4, 0, 10) };
        var save = new CheckBox { Content = "Save this sign-in in Windows Credential Manager", Margin = new Thickness(0, 0, 0, 12) };
        var status = new TextBlock { Text = "Leave the password blank to use the saved sign-in for this account and server. Passwords aren't included in logs or commands.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var download = new Button { Content = "Download", IsDefault = true, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        var forget = new Button { Content = "Remove saved sign-in", MinWidth = 130, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(forget); buttons.Children.Add(download);
        var panel = new StackPanel { Margin = new Thickness(20), Width = 560 };
        panel.Children.Add(new TextBlock { Text = $"Download {product.Name} {product.Version}", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"Approved server: {endpoint.Host}:{endpoint.Port}\nFingerprint: {fingerprint}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "Vendor username" }); panel.Children.Add(username);
        panel.Children.Add(new TextBlock { Text = "Password" }); panel.Children.Add(password);
        panel.Children.Add(save); panel.Children.Add(status); panel.Children.Add(buttons);
        var window = new Window { Title = "Vendor sign-in", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = System.Windows.Application.Current?.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        bool forgetSaved = false;
        forget.Click += (_, _) => { forgetSaved = true; if (username.Text.Trim().Length > 0) window.DialogResult = true; else status.Text = "Enter the username for the saved sign-in you want to remove."; };
        download.Click += (_, _) => { if (username.Text.Trim().Length > 0) window.DialogResult = true; else status.Text = "Enter the vendor username."; };
        if (window.ShowDialog() != true) return null;
        var secret = SecureChars(password.SecurePassword);
        password.Clear();
        return new(username.Text.Trim(), secret, save.IsChecked == true, forgetSaved);
    }

    private static char[] SecureChars(SecureString secure)
    {
        if (secure.Length == 0) return [];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            var value = new char[secure.Length];
            Marshal.Copy(pointer, value, 0, value.Length);
            return value;
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }

    private sealed class CredentialPrompt(string username, char[] secret, bool saveCredential, bool forgetSaved) : IDisposable
    {
        private char[] secret = secret;
        public string Username { get; } = username;
        public ReadOnlyMemory<char> Secret => secret;
        public bool SaveCredential { get; } = saveCredential;
        public bool ForgetSaved { get; } = forgetSaved;
        public char[] TakeSecret() { var value = secret; secret = []; return value; }
        public void Dispose() { Array.Clear(secret); secret = []; }
    }
}
