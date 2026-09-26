# Workstation migration and deployment profiles

AV Workstation Toolkit can carry the applications of an old workstation to its
replacement, and can set up a workstation from an intentional baseline called a
deployment profile. Both produce the same checklist: what is already installed,
what the Toolkit can install for you, and what you install yourself. The
checklist stays on the workstation until you finish it, so a migration can span
vendor-portal downloads, licensing, reboots, and several days.

Open it from **Workstation migration** in the main window's header, or
**Tools > Workstation migration and profiles**.

## Moving to a replacement workstation

**On the old workstation**

1. Open **Workstation migration**. It uses the main window's latest status check, or
   scans if none has finished; **Scan this PC** scans again.
2. Choose **Export inventory…** and save the file somewhere you can reach from
   the new workstation, such as a USB drive or a share.

**On the replacement workstation**

1. Open **Workstation migration** and choose **Import inventory…**.
2. The Toolkit scans the new workstation immediately and compares it with the
   old one. There is no separate plan-building step: the imported inventory is
   the migration list.
3. Work through the checklist. It opens on **To do**, the selected
   applications that aren't done yet.

Every user-facing application from the old workstation starts selected.
Runtimes, driver packages, and Windows-supplied components such as Microsoft
Edge start excluded, because the applications that need them install them;
include any you want. Hidden system components and updates stay in the
inventory file as evidence but aren't added to the checklist.

## The checklist

| Status | Meaning | What to do |
|---|---|---|
| Installed | Detected on this workstation. A version that differs from the old one is noted. | Nothing. |
| Done (confirmed) | You marked it done, and this workstation can't confirm it. | Nothing. |
| Install available | The approved managed catalog allows an automatic installation here. | **Install** or **Install all available**. |
| Restart first | Allowed, but it installs a driver, service, or listener and Windows is waiting for a restart. | Restart Windows, then install. |
| Installing… | The worker is installing it. | Wait. |
| Install failed | The last attempt failed, or reported success without the app being detected. | Read the detail, then retry or install it yourself. |
| Manual | A catalog application the Toolkit can't install automatically, such as Crestron Toolbox or Q-SYS Designer. | Install it yourself (**Get package** in the main window opens its approved vendor page or download), then **Rescan**. |
| Manual · not in catalog | An application the Toolkit doesn't catalog, or that WinGet knows but the managed catalog doesn't approve. | Install it yourself, then **Rescan**. |
| Review | Its identity is ambiguous or only suggested by a similar name. | Decide whether it's needed; install it yourself or exclude it. |
| Can't check | This workstation's inventory was incomplete. | **Rescan**; see Diagnostics if it persists. |
| Excluded | Left out of this migration. | Include it again if you change your mind. |

Manual installation is a normal state, not a failure: much AV software comes
from vendor portals, dealer accounts, or licensed installers. After you install
an app yourself, **Rescan** checks the workstation again; a recognized app moves
to **Installed** on its own.

Checklist actions:

- **Include** check box, **Exclude**, and **Include** leave an app out of the
  migration or bring it back.
- **Remove…** takes an app out of this migration permanently. Excluding is the
  reversible choice.
- **Mark done** records that you installed an app this workstation can't
  detect. It is shown separately from a detected installation and can be undone.
- **Rescan** (F5) scans the workstation again.
- **Finish migration** ends the migration and deletes its saved checklist.

The search box filters by name, publisher, catalog ID, or WinGet ID. Select a
row to see how the app was identified and what was found on this workstation.

## Deployment profiles

A deployment profile describes what a type of workstation should have, such as
a jump PC, a service technician laptop, a programming workstation, or a
commissioning workstation. It lists catalog applications, applications the
catalog doesn't know (by name and optional publisher), whether each is
required, and manual checks such as "Verify remote connectivity".

- **New profile…** opens the editor. Select catalog applications, add any that
  aren't in the catalog, mark optional ones, and type manual checks one per
  line. Save the profile to a file; profiles are kept in the `profiles` folder
  by default and can be shared like any file.
- **Save checklist as profile…** starts a profile from the current checklist's
  selected applications, so a well-set-up workstation can become a baseline.
- **Apply deployment profile…** builds the checklist from a profile and scans
  the workstation. Optional applications start excluded.
- **Revise a profile…** opens a saved profile with the next version number.
  Applying a newer version of the profile a checklist came from shows what the
  revision adds and removes, and keeps the progress on applications that stay.

Manual checks are reminders for the technician. The Toolkit never runs them.

## How the inventory is built

The inventory is the union of everything Windows and WinGet report, not the
subset any one package manager recognizes. AV software from Crestron, Extron,
Biamp, Q-SYS, Shure, Symetrix, AMX, Bose, Audinate, and other manufacturers is
rarely in WinGet, and it's often the software a technician most needs to carry
over.

1. **Windows uninstall registrations are the base.** The same read-only
   provider as the main window reads the 64-bit, 32-bit, and per-user uninstall
   lists (HKLM 64-bit, HKLM 32-bit, and HKCU). Each registration keeps its view,
   key, display name, version, publisher, and visibility flags as evidence.
2. **Catalog identity enriches a registration.** The curated registry detectors
   the catalog uses for installed state recognize names such as "Extron
   Electronics - Toolbelt" or "Crestron Device Database200.460.001.00". The
   workstation inventory and the main window share one detection rule, so a
   detector fix reaches both. Managed WinGet applications carry their own
   verified registered names, locked by a test fixture of real and look-alike
   names.
3. **WinGet identity enriches a registration.** `winget export` supplies exact
   package IDs. An ID attaches to the registration it corresponds to (by
   catalog identity, by a registered name matching the ID, or by publisher and
   exact version), so one application is one entry. A package WinGet reports
   with no registration, such as an MSIX-only app, becomes its own entry.
4. **Windows Installer upgrade codes are read from the registry** to recognize
   an MSI product family across versions. They are never read through Windows
   Installer APIs or WMI, which can start repair or reconfiguration.
5. **Unknown applications survive.** A registration that neither the catalog
   nor WinGet recognizes stays in the inventory with its own evidence.
6. **Duplicates merge; versions don't.** One product registered in two views
   or scopes becomes one entry; side-by-side versions stay separate.

If WinGet is unavailable, registry applications are still inventoried and the
file records WinGet as unavailable. If a registry view can't be read, the file
records the gap, and the checklist shows **Can't check** for items it can't
decide.

The inventory reads names, versions, publishers, registration keys, visibility
flags, and upgrade codes. It doesn't read install paths, uninstall commands,
license keys, credentials, browser data, or user files. MSIX/AppX applications
appear only when WinGet reports them.

### How the checklist recognizes an application

Identity is resolved on the workstation, from the strongest evidence first:

| Evidence | Confidence | Can it lead to an automatic install? |
|---|---|---|
| Catalog application ID, or the exact WinGet ID of a managed record | Exact | Only if this workstation's managed catalog approves it. |
| A curated catalog detector matched the registered name | Catalog | Only if this workstation's managed catalog approves it. |
| Only a similar name resembles a catalog record | Probable | No. Shown as **Review**. |
| More than one catalog record matches | Ambiguous | No. Shown as **Review**. |
| Nothing in the catalog matches | Unidentified | No. Shown as **Manual · not in catalog**. |

To decide whether an app is installed here, the checklist uses the catalog's
own state for catalog apps. For other apps it uses the same WinGet ID, the same
Windows Installer upgrade code, the same uninstall registration, or the same
normalized name with an agreeing publisher, in that order. A version difference
doesn't make an installed app count as missing; it is noted.

## Security boundary

An inventory or a profile only describes what is wanted. It never grants
installation authority.

- **Identity is re-resolved locally.** A file's `catalogId` or WinGet ID is
  evidence to compare against this workstation's catalog, not an instruction.
  A WinGet ID outside the approved managed catalog, such as
  `Some.Arbitrary.Package`, stays **Manual · not in catalog**.
- **Authority comes from this workstation's plan.** Automatic installation is
  offered only for an app this workstation's current plan shows as an approved
  managed WinGet install. The request carries only exact plan package IDs and
  goes through the same action coordinator, authorization, risk
  acknowledgement, pending-restart rule, holds, and independently validating
  worker as the main window. The worker installs one exact ID at a time with
  `--id`, `--exact`, and `--source winget`.
- **Nothing is marked done because an installer said so.** After an
  installation the workstation is scanned again, and an app is **Installed**
  only when it is detected.
- **Strict parsing.** Every document has an explicit `schemaVersion` and
  `documentType`, a size limit, and bounded, control-character-free text.
  Unknown and repeated fields are rejected. A file from a newer version of the
  Toolkit is refused with a message to update, not guessed at. Opening one kind
  of document in place of another reports which kind it is.
- **Manual checks are text.** A profile has no field for commands, scripts,
  URLs, or installer paths, and a document containing one is rejected.
- The feature adds no new process launches, network destinations, or
  elevation. Its scan is the main window's existing read-only refresh, and its
  extra registry reads are read-only.

Out of scope, deliberately: uninstalling, arbitrary EXE or MSI execution,
installers from URLs, credentials and license keys, browser profiles,
domain or Entra join, VPN, EDR, and security-policy configuration.

## Files

### Workstation inventory (`workstation-inventory`, schema 1)

```json
{
  "schemaVersion": 1,
  "documentType": "workstation-inventory",
  "generator": "AV Workstation Toolkit 1.1.2",
  "capturedAtUtc": "2026-09-26T05:00:00Z",
  "machine": { "computerName": "AV-LAPTOP-01", "windowsEdition": "Windows 11 Pro", "windowsVersion": "24H2", "osBuild": "26100.4652", "architecture": "x64" },
  "sources": { "registry": "complete", "winget": "complete" },
  "applications": [
    {
      "displayName": "Crestron Toolbox 3.1390.0008.3",
      "displayVersion": "3.1390.0008.3",
      "publisher": "Crestron Electronics Inc.",
      "scope": "machine",
      "architecture": "x86",
      "relevance": "application",
      "catalogId": "Crestron.Toolbox",
      "registrations": [
        { "view": "HKLM32", "key": "{1B52BC01-2F6E-4FAE-BB09-1F28D2BF1D63}_is1", "displayName": "Crestron Toolbox 3.1390.0008.3", "displayVersion": "3.1390.0008.3", "publisher": "Crestron Electronics Inc." }
      ]
    },
    {
      "displayName": "Wireshark 4.4.0 x64",
      "displayVersion": "4.4.0",
      "scope": "machine",
      "architecture": "x64",
      "relevance": "application",
      "catalogId": "WiresharkFoundation.Wireshark",
      "registrations": [ { "view": "HKLM64", "key": "Wireshark", "displayName": "Wireshark 4.4.0 x64", "displayVersion": "4.4.0" } ],
      "winget": { "id": "WiresharkFoundation.Wireshark", "version": "4.4.0", "correlation": "catalogIdentity" }
    }
  ]
}
```

- `relevance` is `application`, `supportComponent`, `systemComponent`, or
  `update`. It decides only whether the checklist includes an item by default.
- `catalogId` is present when the source workstation identified the app with
  confidence; `winget` when WinGet reported it. Registrations are the raw
  evidence and are always kept.
- Limits: 8 MiB, 5,000 applications, 32 registrations per application, 512
  characters per text value.

The file identifies the source computer by name and lists its software,
versions, and publishers. Treat it as internal operational information.

### Deployment profile (`workstation-profile`, schema 1)

```json
{
  "schemaVersion": 1,
  "documentType": "workstation-profile",
  "profileId": "remote-support-jump-pc",
  "name": "Remote Support Jump PC",
  "profileVersion": 3,
  "description": "Remote support jump workstation",
  "updatedAtUtc": "2026-09-26T05:00:00Z",
  "applications": [
    { "catalogId": "Google.Chrome", "displayName": "Google Chrome", "required": true },
    { "displayName": "Proactive Agent", "publisher": "Proactive", "required": true },
    { "catalogId": "Adobe.Acrobat.Reader.32-bit", "displayName": "Adobe Acrobat Reader", "required": false }
  ],
  "checks": [
    { "id": "verify-remote-connectivity", "text": "Verify remote connectivity" }
  ]
}
```

`profileId` is a stable slug that identifies the profile across revisions;
`profileVersion` increases with each revision. Each application needs a
`catalogId` or a `displayName`, and may carry a `wingetId` as identity
evidence. Limits: 1 MiB, 1,000 applications, 200 checks.

### Saved migration (`migration-session`, schema 1)

The active checklist is saved as `migration\session.json` beneath the per-user
data root (`%LOCALAPPDATA%\AVWorkstationToolkit` for the packaged app). It
records the source (computer name or profile name and version), each item's
descriptive details, whether it's included, any technician confirmation, the
last installation attempt, removed and skipped counts, and manual-check
progress. It holds no secrets and is validated as strictly as an imported
file. If it can't be read, the window says so and offers to discard it.
**Finish migration** deletes it.

## Limitations and deferred work

- MSIX/AppX applications that WinGet doesn't report aren't inventoried. The
  documented package API needs a Windows SDK projection that would change the
  shipping package, and the undocumented registry repository is mostly system
  and framework packages.
- An application with no registered name that the catalog recognizes, and no
  WinGet identity, can only be marked done by a technician.
- Matching an uncatalogued app by name treats any version as installed and
  notes the difference.
- The source workstation's architecture and scope are recorded but aren't used
  to choose an installer.
- The workstation doesn't yet keep a history of profiles applied after a
  checklist is finished. The stable profile ID and version, and the revision
  comparison, are in place for that.
- The Toolkit doesn't install anything that isn't an approved managed WinGet
  application, and adds no new install paths for migration.
