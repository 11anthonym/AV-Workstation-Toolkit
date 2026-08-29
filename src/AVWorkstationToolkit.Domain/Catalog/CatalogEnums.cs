namespace AVWorkstationToolkit.Domain.Catalog;

public enum ProviderKind { WinGet, External }
public enum CatalogAuthority { ManagedWinGet, OperationalExternal, AwarenessOnly }
public enum PackageProfile { Standard, Field, Developer, Optional }
public enum PackagePriority { P1, P2, Utility, Dev }
public enum PackageRisk { None, Driver, Service, Listener }
public enum DeploymentPolicy { Allowlisted, ManualHold }
public enum MaintenancePolicy { Allowlisted, Hold }
public enum DeploymentClass { Managed, ManualHandoff, ParentProvider, InventoryOnly, AwarenessOnly, WebOnly, ServerOnly, Embedded }
public enum CatalogMaintenancePolicy { Managed, Manual, ProjectPinned, LegacyHold, VendorManaged, NotApplicable }
public enum VersionRule { Latest, SameMajorMinor, ProjectPinned, ParentCatalog, InventoryOnly, EmbeddedFirmware, WebManaged, Unknown }
public enum VersionCouplingMode { Independent, ParentProvider, SameMajorMinor, ProjectPinned, FirmwarePaired, Unknown }
public enum Lifecycle { Current, Legacy, Transition, CompatibilityUnverified, Discontinued, Unknown }
public enum DeliveryMode { None, VendorPage, DirectDownload, AuthenticatedSftp, ParentProvider, Bundled, Awareness, InventoryOnly }
public enum ReleaseMode { None, VendorPage, ParentCatalog, InventoryOnly }
public enum DetectionMode { WinGet, Registry, None }
public enum DetectionVersionPolicy { None, AtLeast, SameMajorMinor }
public enum DistributionPolicy { Unknown, LinkOnly, VendorDownloadAllowed, Redistributable, PackageManagerOnly, ManualInstall, ReviewBeforeBundling }
public enum LicensingModel { Free, Freemium, Paid, License, Subscription, HardwareLicense, DealerLicense, UnknownCost }
public enum InstallationForm { Installed, Portable, Msi, Exe, Zip, Store, WinGet, VendorPortal, WindowsInbox, Web, Embedded }
public enum SupportedOperatingSystem { Windows, MacOS, Linux, IOS, Android, Web, Embedded, Server, Unknown }
public enum ApplicationType
{
    ControlSystem, DSPAudio, AVoIP, AudioNetworking, WirelessRF, AudioMeasurement,
    LoudspeakerPrediction, AmplifierManagement, Conferencing, CameraPTZ, DisplayProjector,
    DigitalSignage, DvLEDVideoWall, Intercom, MediaServerShowControl, BroadcastVideo,
    LightingControl, FieldUtility, NetworkUtility, SerialUtility, UsbDiagnostic, EDIDHDCP,
    FirmwareUtility, Development, Driver, Service, Server, WebApplication, EmbeddedSoftware,
    LegacySupport
}
public enum PackageRole
{
    AVEngineer, FieldService, ControlProgramming, DSPEngineering, NetworkEngineering,
    RFCoordination, Commissioning, DesignEngineering, BroadcastVideo, DigitalSignage,
    LightingProgramming, SystemAdministration, Development
}
public enum PackageStatus
{
    Current, Missing, UpdateAvailable, ManualUpdate, Held, Manual, Inventory,
    NotDetected, InventoryIncomplete, InventoryUnavailable, CheckUnavailable,
    Awareness, Error
}
public enum PackageAction { None, Install, Update, Manual }
public enum InventoryQuality { Complete, Partial, Unavailable, PackageError, NotApplicable }
public enum QuickView { All, Missing, Updates }
public enum CatalogPreset
{
    All, P1, Onsite, Free, FreePublic, Dealer, Licensed, Drivers, Services,
    Firmware, Current, Legacy, Unmanaged, InstalledSourceLimited
}
public enum CatalogDiscipline
{
    All, DSP, AudioNetworking, AVoIP, RF, Conferencing, Displays, DvLED, Control,
    Broadcast, MediaShow, Lighting, Intercom, Utilities, Measurement,
    FirmwareCommissioning, Development
}
public enum PolicyDisposition { Allowed, Blocked, RequiresAcknowledgement, Held, ManualOnly, NotActionable }
public enum RebootReason { WindowsUpdate, ComponentBasedServicing }
public enum MetadataVerificationState { Current, ReviewSoon, VerificationRequired, Quarantined }
