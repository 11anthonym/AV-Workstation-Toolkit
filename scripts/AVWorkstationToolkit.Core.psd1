@{
    RootModule = 'AVWorkstationToolkit.Core.psm1'
    ModuleVersion = '1.1.1'
    GUID = '8f5ce752-7e4f-4f9c-91a3-7113a970db91'
    Author = 'AV Workstation Toolkit contributors'
    CompanyName = 'AV Workstation Toolkit Project'
    Copyright = '(c) 2026 AV Workstation Toolkit contributors.'
    Description = 'Validated catalog, external-package awareness, planning, request, and WinGet safety functions for AV Workstation Toolkit.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @(
        'Get-AVWorkstationToolkitDataRoot',
        'Invoke-AVWorkstationToolkitLegacyDataMigration',
        'Get-AVWorkstationToolkitDistributionRoot',
        'Get-AVWorkstationToolkitCatalog',
        'Find-AVWorkstationToolkitCatalog',
        'Get-AVWorkstationToolkitCatalogVendors',
        'Resolve-AVWorkstationToolkitCatalogVendorSelection',
        'Test-AVWorkstationToolkitCatalogFilter',
        'Get-AVWorkstationToolkitMetadataVerificationState',
        'ConvertFrom-AVWorkstationToolkitExternalCatalogJson',
        'Compare-AVWorkstationToolkitVersion',
        'Get-AVWorkstationToolkitExternalInventory',
        'ConvertFrom-AVWorkstationToolkitExternalReleaseContent',
        'ConvertFrom-AVWorkstationToolkitExternalDownloadContent',
        'Get-AVWorkstationToolkitExternalReleaseInfo',
        'Resolve-AVWorkstationToolkitExternalPayload',
        'Resolve-AVWorkstationToolkitVendorCachePayload',
        'Complete-AVWorkstationToolkitVendorDownload',
        'Get-AVWorkstationToolkitAuthenticatedSftpCatalog',
        'Get-AVWorkstationToolkitTrustedSftpHost',
        'Set-AVWorkstationToolkitTrustedSftpHost',
        'Get-AVWorkstationToolkitRebootState',
        'Test-AVWorkstationToolkitElevated',
        'Get-AVWorkstationToolkitWingetCommand',
        'Invoke-AVWorkstationToolkitWingetCapture',
        'ConvertFrom-AVWorkstationToolkitWingetExportJson',
        'Get-AVWorkstationToolkitWingetStructuredInventoryQuality',
        'Get-AVWorkstationToolkitWingetInventory',
        'ConvertFrom-AVWorkstationToolkitWingetUpgradeText',
        'Test-AVWorkstationToolkitIdInText',
        'Get-AVWorkstationToolkitPlan',
        'Open-AVWorkstationToolkitExplorerPath',
        'Open-AVWorkstationToolkitHttpsUri',
        'Remove-AVWorkstationToolkitAnsi',
        'Protect-AVWorkstationToolkitSensitiveText',
        'Get-AVWorkstationToolkitDiagnostics',
        'ConvertTo-AVWorkstationToolkitDiagnosticsText'
    )
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('AV Workstation Toolkit','winget','WPF','workstation')
            ProjectUri = 'https://github.com/11anthonym/AV-Workstation-Toolkit'
        }
    }
}
