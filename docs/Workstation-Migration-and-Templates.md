# Workstation migration and templates

AV Workstation Toolkit can carry the applications of an old workstation to its
replacement, and can set up a workstation from an intentional baseline called a
workstation template. Both produce the same checklist: what is already installed,
what the Toolkit can install for you, and what you install yourself. The
checklist stays on the workstation until you finish it, so a migration can span
vendor-portal downloads, licensing, reboots, and several days.

Open it from **Workstation migration** in the main window's header, or
**Tools > Workstation migration and templates**.

## Moving to a replacement workstation

**On the old workstation**

1. Open **Workstation migration**. It uses the main window's latest status check, or
   scans if none has finished; **Scan this PC** scans again.
2. Review **Applications on this PC**. Every user-facing application starts
   selected; clear any you don't want to carry over. **Show supporting and
   system components** lists runtimes, drivers, and hidden entries too.
3. Choose **Export inventory…** and save the file somewhere you can reach from
   the new workstation, such as a USB drive or a share. Every observation is
   written as evidence; a cleared item is only marked as not selected.

**On the replacement workstation**

1. Open **Workstation migration** and choose **Import inventory…**.
2. The Toolkit scans the new workstation immediately and compares it with the
   old one. There is no plan to build, approve, or activate: the imported
   inventory is the working migration.
3. Work through the checklist. It opens on **Remaining**: the selected
   applications this workstation doesn't have yet. The header leads with how
   many remain, followed by how many were imported, are installed, are
   excluded, and need review.

Every user-facing application the old workstation selected starts selected
here. Runtimes, driver packages, updaters, and Windows-supplied components such
as Microsoft Edge are listed under **Supporting** and start outside the
migration, because the applications that need them install them; include any
you want. They don't count as exclusions. Hidden system components and updates
stay in the inventory file as evidence but aren't added to the checklist.

## The checklist

An application's state always comes from the workstation's current evidence.
It leaves **Remaining** when a scan detects it, whether AV Workstation Toolkit
installed it, you installed it yourself, or it was already there, and it
returns to **Remaining** if a later scan no longer finds it. The checklist keeps
every item; a detected item is shown under **Completed**, not deleted.

| Status | Meaning | What to do |
|---|---|---|
| Installed | Detected on this workstation. A version that differs from the old one is noted. | Nothing. |
| Install available | The approved managed catalog allows an automatic installation here. | **Install** or **Install all available**. |
| Restart first | Allowed, but it installs a driver, service, or listener and Windows is waiting for a restart. | Restart Windows, then install. |
| Installing… | The worker is installing it. | Wait. |
| Install failed | The last attempt failed, or the worker refused it when it rechecked this PC. | Read the detail, then retry or install it yourself. |
| Installed · not verified | The installer reported success, but no scan has detected the app yet, or this PC couldn't be checked afterward. It still counts as remaining. | **Rescan**, or restart Windows if the installer asked for it. |
| Manual · not yet detected | A catalog application the Toolkit can't install automatically, such as Crestron Toolbox or Q-SYS Designer. | Install it yourself (**Get package** in the main window opens its approved vendor page or download), then **Rescan**. |
| Manual · not in catalog | An application the Toolkit doesn't catalog, or that WinGet knows but the managed catalog doesn't approve. | Install it yourself, then **Rescan**. |
| Review | Its identity is ambiguous or only suggested by a similar name. | Decide whether it's needed; install it yourself or exclude it. |
| Can't check | This workstation's inventory was incomplete. | **Rescan**; see Diagnostics if it persists. |
| Not checked yet | The saved checklist is shown while this PC is being checked, or the check failed. Nothing is complete or installable until a scan finishes. | Wait, or **Rescan** if the check failed. |
| Manual · can't be detected | A catalog application with no Windows detector, and no installer identity from the source to match. | Install it, then **Confirm installed**. |
| Confirmed installed | You confirmed an application of the kind above. | Nothing; **Clear confirmation** undoes it. |
| Excluded | A user-facing application you left out of this migration. | Include it again if you change your mind. |
| Supporting component | A runtime, driver package, updater, or Windows component left out by default. | Include it only if you need it. |

Manual installation is a normal state, not a failure: much AV software comes
from vendor portals, dealer accounts, or licensed installers. After you install
an app yourself, **Rescan** checks the workstation again, and a recognized app
leaves **Remaining** on its own. After an automatic installation the Toolkit
scans the workstation itself; an app is completed only when that scan detects
it, never because an installer reported success.

There is no general "mark done" for applications. **Confirm installed** exists
only for the one case a scan can never settle: a catalog application the
catalog can't detect on Windows, with no WinGet, Windows Installer, or
uninstall-registration identity from the source. Every other application,
including one the catalog doesn't know, is completed only by detection; if it
can't be found it stays **Manual · not yet detected**, **Manual · not in
catalog**, or **Review**. Template manual checks are different: they are
human tasks, and you tick them off yourself.

Checklist actions:

- The **Include** check box, **Exclude**, and **Include** leave an app out of
  the migration or bring it back.
- **Remove…** takes an app out of this migration permanently. Excluding is the
  reversible choice.
- **Rescan** (F5) scans the workstation again.
- **Finish migration** is the one action that ends the migration. It asks
  first, says whose checklist will be cleared, and then clears it; it never
  uninstalls or removes software. Use it when nothing remains or you've
  intentionally excluded the rest.

The migration stays active until you finish it. Closing the window, or closing
AV Workstation Toolkit, only closes the view: the next time you open
**Workstation migration**, the same checklist is shown at once, marked **Not
checked yet** while this PC is checked, and then updated from what the check
finds. If this PC can't be checked, the checklist stays open, the window says
installation status isn't current, and **Rescan** tries again. Importing an
inventory or applying a template while a migration is active never replaces it
silently: you choose **Continue current migration** (the default) or
**Replace with new migration** (**Replace with template** for a template).

The views are **Remaining**, **Install available**, **Manual**, **Needs
attention** (review, can't check, and failed installs), **Completed**,
**Excluded**, **Supporting**, and **All**. The search box filters by name,
publisher, catalog ID, or WinGet ID. Select a row to see how the app was
identified and what was found on this workstation.

## Workstation templates

A workstation template describes what a type of workstation should have, such as
a jump PC, a service technician laptop, a programming workstation, or a
commissioning workstation. It is separate from the Standard, Field, Developer,
and Optional catalog profiles that filter the main window. It lists catalog applications, applications the
catalog doesn't know (by name and optional publisher), whether each is
required, and manual checks such as "Verify remote connectivity".

- **New workstation template…** opens the editor. Select catalog applications, add any that
  aren't in the catalog, mark the ones that aren't required, and type manual checks one per
  line. Save the template to a file; templates are kept in the `templates` folder
  by default and can be shared like any file.
- **Save checklist as workstation template…** starts a template from the current checklist's
  selected applications, so a well-set-up workstation can become a baseline.
- **Apply workstation template…** builds the checklist from a template and scans
  the workstation. Applications that aren't required start excluded.
- **Revise a workstation template…** opens a saved template with the next revision number.
  Applying a newer revision of the template a checklist came from shows what the
  revision adds and removes, and keeps the progress on applications that stay.

Manual checks are reminders for the technician, ticked off by hand. The Toolkit
never runs them. Applications in a template, including ones outside the catalog,
are tracked like any migration item and complete only when detected; a WinGet ID
in a template is identity evidence and never makes an application installable.

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
   an MSI product family across versions. The index is read read-only from the
   machine and per-user registry, and only for a registration Windows Installer
   owns whose key is its product code; malformed values are skipped. Upgrade codes
   are never read through Windows Installer APIs or WMI, which can start repair
   or reconfiguration. Registrations that share an upgrade code and a publisher
   become one application with every installed version listed.
5. **Unknown applications survive.** A registration that neither the catalog
   nor WinGet recognizes stays in the inventory with its own evidence.
6. **Duplicates merge; versions don't.** One product registered in two views
   or scopes becomes one entry; side-by-side versions of an unidentified app stay
   separate.
7. **Classification never discards evidence.** Each entry is marked as an
   application, a supporting component (runtime, driver package, updater, or
   Windows component), a hidden system component, or an update, with a reason.
   Only applications are selected by default; everything is exported.

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
| The catalog ID and the WinGet ID name different applications | Ambiguous | No. Shown as **Review**, and nothing on this PC completes it. |
| Nothing in the catalog matches | Unidentified | No. Shown as **Manual · not in catalog**. |

A catalog ID and a WinGet ID on the same record describe one application, so
they must agree. A record that says `7zip.7zip` and `Git.Git` is neither app: it
stays in **Remaining** as **Review** even when Git (or 7-Zip) is installed, and
neither ID can make it installable. The file still imports; only the
contradiction is reported.

To decide whether an app is installed here, the checklist uses the catalog's
own state for catalog apps, then that app's own Windows Installer upgrade code,
uninstall registration, or normalized name from the source; an imported WinGet
ID doesn't stand in for the catalog's detection. For an app outside the catalog,
it uses the same WinGet ID, then the same upgrade code, the same uninstall
registration, or the same normalized name. An upgrade code, an
uninstall key, or a name counts only when the publishers don't disagree, so
installer identity never overrides name and publisher checks. Identity evidence
never grants installation authority. A version difference doesn't make an
installed app count as missing; it is noted.

## Security boundary

An inventory or a template only describes what is wanted. It never grants
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
  only when it is detected. The window reports the installer's result and the
  check afterward separately: if the check fails or is incomplete, it says
  "Install finished; verification failed" (or that the check was incomplete),
  records the installer's result in the item's history, and leaves the app
  **Installed · not verified** in Remaining until a scan detects it.
- **Strict parsing.** Every document has an explicit `schemaVersion` and
  `documentType`, a size limit, and bounded, control-character-free text.
  Unknown and repeated fields are rejected. A file from a newer version of the
  Toolkit is refused with a message to update, not guessed at. Opening one kind
  of document in place of another reports which kind it is.
- **Manual checks are text.** A template has no field for commands, scripts,
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
  "generator": "AV Workstation Toolkit 1.1.3",
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
  `update`, and `relevanceReason` says why an item isn't an application. They
  decide only whether the checklist includes an item by default.
- `migrate` appears only when the source technician changed the default: `false`
  for an application they cleared, `true` for a component they selected. The
  item is still exported either way.
- `catalogId` is present when the source workstation identified the app with
  confidence; `winget` when WinGet reported it. Registrations are the raw
  evidence and are always kept.
- Limits: 8 MiB, 5,000 applications, 32 registrations per application, 512
  characters per text value.

The file identifies the source computer by name and lists its software,
versions, and publishers. Treat it as internal operational information.

### Workstation template (`workstation-profile`, schema 1)

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

The file format keeps the identifiers it was introduced with: a workstation
template file has `documentType` `workstation-profile`, and its `profileId` and
`profileVersion` fields hold the template ID and revision.
`profileId` is a stable slug that identifies the template across revisions;
`profileVersion` increases with each revision. Each application needs a
`catalogId` or a `displayName`, and may carry a `wingetId` as identity
evidence. Limits: 1 MiB, 1,000 applications, 200 checks.

### Saved migration (`migration-session`, schema 1)

The active checklist is saved as `migration\session.json` beneath the per-user
data root (`%LOCALAPPDATA%\AVWorkstationToolkit` for the packaged app). It
records the source (computer name or template name and revision), each item's
descriptive details, whether it's included, any confirmation of an
undetectable application, the
last installation attempt, removed and skipped counts, and manual-check
progress. It records what the migration is trying to accomplish, never whether
an app is installed: that always comes from the latest scan of this PC. It
holds no secrets and is validated as strictly as an imported file. If it can't
be read, the window says so and offers to discard it. Every change is saved
as it's made; if a save fails, the window says so, keeps the change, and tries
again at the next change or when the window closes.
**Finish migration** deletes it. Each save writes a temporary file beside it and
then replaces it, so a failed save leaves the last valid checklist in place and
the window reports the failure; a leftover temporary file is never read as a
checklist.

## Limitations and deferred work

- MSIX/AppX applications that WinGet doesn't report aren't inventoried. The
  documented package API needs a Windows SDK projection that would change the
  shipping package, and the undocumented registry repository is mostly system
  and framework packages.
- A catalog application with no Windows detector and no installer identity from
  the source (typically one listed in a workstation template) can't be detected, so
  it is completed by **Confirm installed**. Every other application completes
  only by detection.
- Matching an uncatalogued app by name treats any version as installed and
  notes the difference.
- The source workstation's architecture and scope are recorded but aren't used
  to choose an installer.
- The workstation doesn't yet keep a history of templates applied after a
  checklist is finished. The stable template ID and revision number, and the revision
  comparison, are in place for that.
- The Toolkit doesn't install anything that isn't an approved managed WinGet
  application, and adds no new install paths for migration.
