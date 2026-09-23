# Publishing signed managed application catalogs

## Publisher boundary

`AVWorkstationToolkit.CatalogPublisher managed` creates and verifies a local signed snapshot of the canonical
`manifests/managed-applications.json`. The publisher remains offline: it cannot upload, activate, or add a trust
anchor. Packaged application and worker runtimes can consume its output only when the signing public key was compiled
into their managed-catalog trust policy. No production managed-catalog key is currently configured.

The managed authority is separate from the descriptive Device Lookup authority:

- catalog identity: `avwt-managed`
- bundle extension: `.avwtmanaged`
- expected signing keys: supplied explicitly to the managed verifier; no reference key is accepted implicitly
- approved payload: exactly `managed-applications.json`
- forbidden-product policy: must exactly match the current application contract

The catalog cannot carry commands, scripts, executable paths, installer URLs, package sources, environment expansion,
or WinGet arguments. Unknown fields fail through the existing strict managed JSON parser. The existing `CatalogParser`
continues to validate exact package IDs, profiles, risks, deployment and maintenance policies, installer modes, and
duplicate IDs.

## Artifact contract

```text
managed-catalog-feed/
├── stable/
│   ├── managed-catalog-channel.json
│   └── managed-catalog-channel.sig
└── catalogs/<revision>/
    └── AVWT-Managed-Catalog-<version>.avwtmanaged
```

The ZIP-compatible bundle contains exactly:

```text
managed-applications.json
managed-catalog-manifest.json
managed-catalog-manifest.sig
```

The ECDSA P-256 manifest signature authorizes the manifest bytes. The manifest hashes the one approved payload and
records `CatalogId`, schema, version, monotonic revision history, UTC creation time, minimum application version,
signing-key ID, and validated package count. The separately signed channel identifies the immutable revision artifact
and hashes its exact bytes. Managed verification checks both signatures, the channel-to-bundle hash, the manifest-to-
payload hash, exact ZIP containment, application compatibility, strict managed JSON, and policy normalization.

Payload, manifest, ZIP entry ordering/timestamps, and all channel fields before the bundle hash are deterministic for
identical inputs. As with the existing reference publisher, platform ECDSA signatures are intentionally nonce-bearing;
two independent signing runs may produce different valid signatures and therefore different final bundle hashes. Each
signed bundle and channel pair is one indivisible immutable publication unit.

## Development-only invocation

Use an isolated temporary P-256 key outside the repository. Do not use the reference-catalog production key and do not
provision a production managed key during development.

```powershell
dotnet run --project .\tools\AVWorkstationToolkit.CatalogPublisher\AVWorkstationToolkit.CatalogPublisher.csproj -c Release -- managed `
  --version 2026.9.20.1 `
  --revision 1 `
  --minimum-app-version 1.1.1 `
  --created-utc 2026-09-20T12:00:00Z `
  --signing-key-id managed-development-only `
  --private-key C:\TemporaryIsolatedKeys\managed-development-only.pem `
  --public-base-uri https://catalog.example.test/avwt/managed/
```

The default output is `artifacts/managed-catalog-feed` and must not already exist. The operation is offline and cannot
upload or activate its output. Runtime update behavior, the independent worker boundary, and the manual operator
workflow are documented in [Signed managed application catalog updates](Managed-Catalog-Updates.md).

An owner-approved release phase must still provision a distinct production managed signing authority, review and
compile the public key, publish and verify the fixed production channel, and embed the first approved signed bundle as
the offline baseline.

## Initial production revision proposal

The prepared first production publication uses the existing reviewed authoring source without adding or removing any
WinGet authority:

| Field | Proposed value |
| --- | --- |
| Catalog ID | `avwt-managed` |
| Signing key ID | `avwt-managed-2026-a` |
| Catalog version | `2026.9.22.1` |
| Revision | `1` |
| Minimum application version | `1.1.1` |
| Public base URI | `https://11anthonym.github.io/AVWT-Catalog/managed/` |
| Immutable bundle | `managed/catalogs/1/AVWT-Managed-Catalog-2026.9.22.1.avwtmanaged` |
| Channel metadata | `managed/stable/managed-catalog-channel.json` |
| Channel signature | `managed/stable/managed-catalog-channel.sig` |

Before signing, confirm that `manifests/managed-applications.json` still has SHA-256
`AFCFEC1D3A8CC52D11B9DF64180FA5C883BF5F3D06122DD61367CC3FC66A2E19`, contains 30 packages, and retains PuTTY
with `InstallerMode` set to `InstallerDefault`. A changed source hash is not an automatic failure, but requires a fresh
review and an explicit publication decision rather than silently using this prepared approval record.

## Owner-controlled production key provisioning

The owner must run this step in PowerShell 7 or later on a BitLocker-protected workstation. The private key stays in
the owner-only directory and must never be pasted into an issue, terminal transcript, application repository, catalog
repository, build artifact, or chat. This command creates a new ECDSA P-256 key pair; it must not be run if either
destination already exists.

```powershell
$keyRoot = Join-Path $HOME 'Documents\Keys\AVWT-Managed-Catalog'
$privateKeyPath = Join-Path $keyRoot 'avwt-managed-2026-a-private.pem'
$publicKeyPath = Join-Path $keyRoot 'avwt-managed-2026-a-public.pem'

if (Test-Path -LiteralPath $privateKeyPath -PathType Leaf) { throw "Private key already exists: $privateKeyPath" }
if (Test-Path -LiteralPath $publicKeyPath -PathType Leaf) { throw "Public key already exists: $publicKeyPath" }
$null = New-Item -ItemType Directory -Path $keyRoot -Force

$ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($ownerSid)
$acl.SetAccessRuleProtection($true, $false)
$access = [Security.AccessControl.FileSystemAccessRule]::new(
    $ownerSid,
    [Security.AccessControl.FileSystemRights]::FullControl,
    [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
    [Security.AccessControl.PropagationFlags]::None,
    [Security.AccessControl.AccessControlType]::Allow)
$null = $acl.AddAccessRule($access)
Set-Acl -LiteralPath $keyRoot -AclObject $acl

$utf8 = [Text.UTF8Encoding]::new($false)
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    [IO.File]::WriteAllText($privateKeyPath, $key.ExportPkcs8PrivateKeyPem(), $utf8)
    [IO.File]::WriteAllText($publicKeyPath, $key.ExportSubjectPublicKeyInfoPem(), $utf8)
    $fingerprint = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($key.ExportSubjectPublicKeyInfo()))
}
finally {
    $key.Dispose()
}

[pscustomobject]@{
    KeyId = 'avwt-managed-2026-a'
    PublicKeyPath = $publicKeyPath
    SubjectPublicKeyInfoSha256 = $fingerprint
}
```

Back up the private PEM once to the approved encrypted offline key store and test that backup under the owner's key
recovery policy. Only the public PEM and its displayed SPKI SHA-256 fingerprint leave this protected location. The
public-key handoff is used to add `avwt-managed-2026-a` to `ProductionManagedCatalogTrustAnchors`, add a pinned
fingerprint regression, and prove that the reference key and development keys remain rejected. No placeholder or
private-key path belongs in production configuration.

## Owner-controlled first signing

Run the existing publisher from a clean, reviewed AVWT checkout after the public-key fingerprint has been approved.
Set `CreatedUtc` once at signing time; the output directory must not already exist.

```powershell
$repositoryRoot = (Get-Location).Path
$privateKeyPath = Join-Path $HOME 'Documents\Keys\AVWT-Managed-Catalog\avwt-managed-2026-a-private.pem'
$outputRoot = Join-Path $repositoryRoot 'artifacts\managed-catalog-feed-production'
$createdUtc = [DateTimeOffset]::UtcNow.ToString('O')

dotnet run --project .\tools\AVWorkstationToolkit.CatalogPublisher\AVWorkstationToolkit.CatalogPublisher.csproj -c Release -- managed `
  --repository-root $repositoryRoot `
  --output-root $outputRoot `
  --version 2026.9.22.1 `
  --revision 1 `
  --minimum-app-version 1.1.1 `
  --created-utc $createdUtc `
  --signing-key-id avwt-managed-2026-a `
  --private-key $privateKeyPath `
  --public-base-uri https://11anthonym.github.io/AVWT-Catalog/managed/
if ($LASTEXITCODE -ne 0) { throw 'Managed catalog publication failed.' }
```

The publisher validates the canonical JSON, writes the immutable bundle and signed channel pair, then verifies its own
output through the production-compatible managed verifier. Review the emitted manifest, package count, payload hash,
bundle hash, channel URI, key ID, revision, and minimum application version before moving any file.

## Public publication and release order

The public `AVWT-Catalog` repository currently validates only the descriptive reference layout. Before the first
managed publication, its owner-approved validation workflow must narrowly admit these paths while preserving its
existing unexpected-file, symlink, immutability, and private-key-marker checks:

```text
managed/stable/managed-catalog-channel.json
managed/stable/managed-catalog-channel.sig
managed/catalogs/<positive revision>/AVWT-Managed-Catalog-<numeric version>.avwtmanaged
```

Require exactly one managed bundle per revision directory and both stable channel files as a pair. Then use this order:

1. Commit the approved public PEM under key ID `avwt-managed-2026-a` to the AVWT production trust anchors and add the
   pinned SPKI fingerprint test. Never commit the private PEM.
2. Copy the verified immutable revision-1 bundle to `catalog/managed/AVWT-Managed-Catalog.avwtmanaged` in AVWT so the
   application and worker receive the same trusted offline baseline. Commit only after verification with the compiled
   production public key.
3. Publish the immutable bundle to the managed revision directory in `AVWT-Catalog`; verify its public HTTPS bytes and
   SHA-256 against the signed channel locally.
4. Publish the channel JSON and detached signature together only after the immutable URL is serving the approved bytes.
5. Verify the fixed production channel through `ManagedCatalogChannelClient` and confirm a same-revision check is a
   no-op against the embedded baseline.
6. From the clean reviewed AVWT commit, perform the normal production release build with the approved code-signing
   identity. The canonical managed and reference baselines are selected automatically; explicit absolute paths may be
   supplied with `-ManagedCatalogBaselinePath` and `-ReferenceCatalogBaselinePath`.
7. Run package provenance, signature, embedded-payload, worker-boundary, startup, ZIP, and MSI checks before release.

The immutable managed bundle and stable channel are public data. Private signing material is never an input to the
application build. Later signed reference-catalog revisions, including Device Lookup records and descriptive software
relationships, remain independent of this managed authority and do not require replacing the AVWT executable.
