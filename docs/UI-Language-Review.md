# Application language review

Reviewed: 2026-09-14. Source baseline: `afda951bc30d2ee29804f744d0121b00e82336a5`.

Status: source-backed audit and compiled-UI implementation complete; human usability acceptance remains pending.

## Verdict

AVWT often describes how its implementation protects a boundary instead of telling a technician what happened and what to do next. The problem is not AV terminology such as DSP, firmware, or model numbers. It is developer terminology such as *authority*, *handoff*, *evidence*, *allowlisted*, *scoped*, and *compiled worker* appearing in everyday instructions.

This creates three problems: unnecessary reading, unclear actions, and occasionally misleading claims. The newly improved warning cards are a useful structure, but their explanations still need this treatment. Repetition and abstract phrasing explain the user's “AI sounding” impression; they do not establish who or what wrote the text.

Scope: compiled main window, search and software/device details, package states, vendor dialogs, action messages, diagnostics, catalog updates, Safety & Security, and About. Followed relevant data producers where wording depended on state. Sampled catalog-authored descriptions; did not audit every manufacturer or re-review compatibility evidence. Excluded developer-only smoke/rehearsal messages from normal-production findings. No authority, manifest, or signed-baseline changes.

## Implementation result

The reviewed compiled-application copy is now implemented. Routine screens lead with the result and next step, while identifiers, provider states, parser messages, fingerprints, and other technical facts remain available in details or diagnostics. Warning banners now name the failed check, explain what is affected, show sanitized detail, and offer **View details** and **Check again**.

The implementation also fixes the meaning defects found by the audit: unknown installation evidence no longer becomes **Not installed**; combined inventory/update failures no longer overstate what remains known; stale software-metadata notices no longer promise that a device-catalog update will fix them; version branches use readable labels; and normal production action text no longer describes a migration worker.

Package planning, DeviceSoftwareRelation scope, vendor authorization, credentials, worker requests, and execution decisions are unchanged. Catalog-authored descriptions remain governed by the existing signed catalog publication process and were not mass-rewritten in this application-copy change.

## Writing standard

Write for an AV field technician, not a developer and not a novice who needs familiar AV terms removed. Use short, direct sentences and action-specific buttons; explain the consequence before implementation details. This follows Microsoft's [Windows application writing guidance](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style).

Use the same term for the same thing, ordinary verbs, and natural contractions. Avoid replacing useful technical precision with vague reassurance. Microsoft's [word-choice checklist](https://learn.microsoft.com/en-us/style-guide/checklists/word-choice-checklist) supports this approach.

For problems, answer **what failed → what is affected → what can I do?** Keep codes and parser details available in diagnostics. Distinguish an interrupted task from a maintenance notice; neither color alone nor a generic warning count explains the problem. See Nielsen Norman Group's [error-message guidelines](https://www.nngroup.com/articles/error-message-guidelines/).

Put a short explanation next to an unfamiliar term, make instructions explicit, and keep help discoverable. These recommendations align with W3C's [clear-content guidance](https://www.w3.org/WAI/WCAG2/supplemental/objectives/o3-clear-content/); this is supplemental accessibility guidance, not a claim of WPF accessibility conformance.

Practical rules:

- Lead with the task or result, not “AV Workstation Toolkit” in every sentence.
- Prefer “check,” “open,” “save,” and “download” to “validate,” “handoff,” “persist,” and “acquire” in routine copy.
- Keep three layers: short status; effect and next action; optional technical details.
- Use sentence case. Avoid slash-separated sentences and long strings of noun phrases.
- Never substitute “safe,” “supported,” “up to date,” or “not installed” for an unknown state.
- Do not remove real warnings to make the tone friendlier. Explain the specific risk.

## First priority: correct the meaning

These are concrete source findings, not just preferences.

| Finding | Source | Recommended correction |
| --- | --- | --- |
| Empty installed-version text becomes **Not installed**, even when inventory is unavailable or a detected installation has no version. Package details also print the Installed boolean without qualifying inventory quality. | [PackageRowViewModel](../src/AVWorkstationToolkit.App/ViewModels/PackageRowViewModel.cs), `VersionLabel`; [CatalogDetailModels](../src/AVWorkstationToolkit.Application/Details/CatalogDetailModels.cs), Workstation state | Use **Version unknown** for a detected installation without a version; **Couldn't check installation** when presence is unknown; **Not installed** only when absence was established. Apply the same rule in table and details. Do not change planning/selection decisions. |
| An update-check diagnostic always says installed inventory remains available. It cannot establish that from the update issue alone. | [DiagnosticsViewModel](../src/AVWorkstationToolkit.App/ViewModels/DiagnosticsViewModel.cs), `CreateIssuePresentation` | Mention successful installed inventory only when that separate check succeeded. Otherwise state only **We couldn't check for updates.** |
| The malformed-update-output case recommends updating or repairing App Installer/WinGet without establishing a broken installation. | Same diagnostic mapping | First offer **Check again**, then **Copy diagnostics** if it persists. Keep the parser error and WinGet version in technical details. Reserve repair guidance for an actually missing/unavailable installation. |
| The stale package-metadata warning suggests a signed catalog update may refresh that metadata. Package metadata and the updateable device-reference projection are loaded separately. | [DiagnosticsViewModel](../src/AVWorkstationToolkit.App/ViewModels/DiagnosticsViewModel.cs); [ReadOnlyDiagnosticsService](../src/AVWorkstationToolkit.Application/Diagnostics/ReadOnlyDiagnosticsService.cs); [CompiledAppComposition](../src/AVWorkstationToolkit.App/Services/CompiledAppComposition.cs) | Remove the misleading recovery advice. Use **Some software catalog information needs review. No action is required on this PC.** Do not promise that the device-reference feed fixes package metadata. |
| An absent release-family restriction is described as **Any reviewed family**; a specified family is printed as its internal ID. | [ReadOnlyDetailViewModels](../src/AVWorkstationToolkit.App/ViewModels/ReadOnlyDetailViewModels.cs), `RelationSummary` | **No release-family restriction recorded** for the absence of a constraint. Resolve an existing family to its readable name when present. Neither label establishes universal compatibility. |
| Production idle diagnostics initially say **Migration action mode is idle.** | [MainWindowViewModel](../src/AVWorkstationToolkit.App/ViewModels/MainWindowViewModel.cs), initial action snapshot | **No installation or update is running.** Keep actual development-mode disclosures only in development mode. |

The supplied WinGet error proves that update-output parsing failed, not that PuTTY failed, all installed-app checks failed, or Windows needs repair. Copy must not make those leaps.

### Proposed warning for the supplied diagnostics

**Couldn't check for updates**

Installed versions are still shown, but update availability is unknown. Try checking again.

Actions: **Check again** · **View diagnostics**

Inside diagnostics:

- **What happened:** AVWT couldn't read part of WinGet's update list.
- **What this affects:** Some apps may have updates that aren't shown here.
- **Next step:** Check again. If this continues, copy diagnostics when reporting the problem.
- **Technical details:** preserve the sanitized source, parser message, WinGet version, and error code.

The first sentence is conditional on installed inventory succeeding, as it did in the supplied report. Put catalog-maintenance notices in a separate diagnostics group; don't conflate 327 records needing review with 327 broken apps. Keep a count of failed checks distinct from package warnings. Existing issue severity/codes remain intact.

## Surface-by-surface copy changes

These replacements were implemented as state-aware presentation rules, not global string substitutions. Conditions matter.

| Surface / current wording | Proposed wording or behavior |
| --- | --- |
| **Find software & devices** | Keep. The current persistent search hint is also useful. Keep model examples and ensure the hint wraps visibly. |
| **Refresh plan**, **Export plan**, **Current plan** | **Refresh**, **Export app status…**, **App summary**. The export is a report, not a saved operation that will be executed. Preserve F5 and menu access keys. |
| **P1 field candidates** | **Priority 1 apps**, with a short explanation of that filter. Don't relabel priority as popularity or vendor endorsement. |
| **Known, not managed** / **Installed, source limited** | **Not installed through AVWT** / **Installed — limited version information**, subject to the actual preset predicate. Use help text when the predicate covers more than one condition. |
| **Current** / **Missing** | **Up to date** only when the check supports that statement; **Not installed** only after confirmed absence. A known catalog baseline is not necessarily the latest vendor release. |
| **Inventory incomplete** / **Inventory unavailable** | **Installation check incomplete** / **Couldn't check installation**. Explain whether any installed versions were found. |
| **Check unavailable** | **Couldn't check for updates** when it is a release check; do not reuse that label for failed installation detection. |
| **Held** / **Manual** / **Catalog only** | **Automatic updates paused** / a reason-specific **Install through vendor** or **Review required** / **Information only**. Retain the underlying states. |
| Summary: **managed actions**, **manual**, **inventory**, **awareness** | Separate concepts: installed-app results, actions AVWT can perform, and informational entries. Label counts with what is counted; don't merely shorten the existing mixed summary. |
| **No currently permitted automated action** | State the actual reason: **Already up to date**, **Install from the vendor**, **Check failed — refresh to try again**, or **Information only**. |
| **Installed-version evidence** | **Versions found on this PC**. Unknown: **We haven't verified which versions are installed.** Incomplete: **Some installation information is missing.** Unavailable: **We couldn't check installed versions.** |
| **Release families** | **Version branches**, with **Current**, **Long-term support (LTS)**, **Archived**, or **Legacy** as applicable. Q-SYS remains one product; retain its firmware/project constraints. |
| **Applicable devices grouped by purpose** | **Devices this software supports**, grouped under existing Configuration, Programming, Diagnostics, etc. Explain conditional support next to the tool. |
| **Verified software relationships** | **Documented software for this device** when confidence actually reflects vendor documentation. Show other confidence states separately; never imply physical testing from a vendor link. |
| **Software coverage not yet verified** | **Software support not yet verified**; body: **This device is listed, but we haven't verified which software applies.** Retain any documented browser/cloud workflow. |
| **No verified software relationship … read-only lookup result** | Distinguish **Known device; software not yet verified** from **No match in this catalog**. Neither means no software exists or is needed. |
| **Verified normalized model or alias** | **Model match** plus the matched alias where helpful. Normalization is an implementation detail; match quality and compatibility confidence are different concepts. |
| **Current release evidence**, **Evidence handoff failed** | Name the actual source: **Vendor documentation**, **Release notes**, or **Compatibility notes**. Failure: **Couldn't open the link.** Keep technical cause in diagnostics. |
| **Policy and compatibility**, **Version coupling**, **Opens listener** | **Installation and compatibility**, **Version requirements**, **Accepts network connections**. Explain Windows services as background services. |
| **Available / known version** | Split or label by provenance: **Available update** versus **Catalog version**. A source-listed baseline must not imply a freshly checked latest version. |
| **Metadata verification / provenance** | **Source and verification details**. Keep IDs, publisher names, dates, hashes, and technical policies here, not in the main subtitle. |
| **Get package** | Where the destination is known, **Download installer…**, **Open vendor website**, **Open vendor installer…**, or **Show downloaded file**. Derive labels from the existing delivery state only. A direct-download route can fall back to a website, so never promise a download before resolving that route. |
| **Catalog-authorized delivery workflow**, **per-user cache**, **payload** | **Download from the vendor**, **saved download**, **downloaded file**. Do not call every ZIP or utility an installer. |
| **Revealed the verified cached package … No installer was executed.** | **Opened the downloaded file's folder. The installer hasn't been run.** Retain the second sentence where installation could reasonably be assumed. |
| **The scoped saved credential was removed.** | **Saved sign-in removed.** Identify the existing account/server in the dialog; never include passwords. |
| **Records cooperative cancellation intent; it does not terminate the worker.** | **Stop after the current app.** Pending state: **Stop requested. The current app may finish before the remaining apps are skipped.** Confirmed cancellation must remain a separate state. |
| **The isolated compiled worker is running.** | **Preparing selected apps…**, then **Installing {app}…** or **Updating {app}…**, using the actual operation. Keep worker/request IDs in diagnostics. |
| **Unverified** action result | **Couldn't confirm the installed version**. Explain whether the installer finished, failed, or has no final result; don't convert exit code zero into success. |
| **Acknowledge selected driver/service/listener risk for this run only** | Show the selected apps and their actual effects, then **I understand these changes and want to continue.** Explain once that confirmation applies only to this operation. Don't add blanket consent or pre-check it. |
| **Reference catalog updates** | **Device catalog updates**, with **Updates device and software-reference information, not installed apps. Works offline with your saved catalog.** Keep the internal Reference Catalog name in technical details. |
| **Catalog service offline** | **Couldn't check for catalog updates. Your saved catalog is still available.** This doesn't claim the server is down when the cause may be local networking. |
| **Activated on disk**, **Selected catalog** | **Saved. Restart AV Workstation Toolkit to use this catalog.** Clearly distinguish the catalog used by this session from the one selected for next launch; do not label a pending revision as already in use. |
| Diagnostics: **Read-only, sanitized runtime and inventory health** | **App checks and troubleshooting details**. For export, explain what is included; “sanitized” is implementation language and not a promise of anonymity. |
| About: **Local commercial-AV workstation management, inventory, catalog knowledge, and diagnostics** | **Find software for AV devices, check installed apps, and manage supported updates.** Keep version information. Move execution-mode terminology to diagnostics. |

Primary locations: [MainWindow.xaml](../src/AVWorkstationToolkit.App/MainWindow.xaml), [MainWindowViewModel](../src/AVWorkstationToolkit.App/ViewModels/MainWindowViewModel.cs), [PackageRowViewModel](../src/AVWorkstationToolkit.App/ViewModels/PackageRowViewModel.cs), [ReadOnlyDetailViewModels](../src/AVWorkstationToolkit.App/ViewModels/ReadOnlyDetailViewModels.cs), [CatalogDetailModels](../src/AVWorkstationToolkit.Application/Details/CatalogDetailModels.cs), [PackageDeliveryWorkflow](../src/AVWorkstationToolkit.App/Services/PackageDeliveryWorkflow.cs), [CompiledActionCoordinator](../src/AVWorkstationToolkit.Application/Actions/CompiledActionCoordinator.cs), [CatalogUpdateViewModel](../src/AVWorkstationToolkit.App/ViewModels/CatalogUpdateViewModel.cs), and the associated XAML windows.

## Security text should explain a decision

Do not eliminate meaningful security language; put it where the user is making that decision.

**Download confirmation:** “Download {name} {version} from {vendor}? The file will be checked and saved. AVWT won't run the installer.” List the checks actually required for that item in details; don't promise that every route has a vendor hash.

**Changed SFTP server identity:** “The server's identity has changed. Do not continue until the vendor or your administrator confirms the change.” Keep the exact host, port, fingerprint, and explicit confirmation. Distinguish first connection from a changed identity. Never replace this with “Continue anyway” or suppress it as jargon.

**Safety & Security:** organize around what AVWT can do, what installation may change, what vendor downloads do, password handling, and what is not supported. Explain standard-user operation and possible Windows installer consent. “Only supported apps you select can be installed or updated” belongs in the overview; the independently validating worker belongs in technical documentation. Say **software uninstall/rollback is not provided** rather than implying that restoring an earlier device catalog is impossible.

Keep exact codes, WinGet IDs, signatures, fingerprints, source quality, and process results in diagnostics. User-friendly text supplements those facts; it does not replace or reinterpret authorization decisions.

## Catalog-authored wording needs a separate controlled edit

Changing XAML alone will not finish this work. Detail screens also display manifest descriptions and constraints. Samples include “no local installation identity is asserted” and “no console-OS authority is created.” Prefer the field consequence, for example **Installed-version detection isn't available** or **Update the console using the vendor's instructions** only when those statements match the recorded facts.

Keep device generation, firmware sequencing, licensing, and project-version requirements prominent. Do not globally remove constraints or translate “unresolved” into support. Catalog text changes must follow the existing reviewed/signed publication process; never patch or re-sign the committed baseline merely to change prose during this audit.

## Bounded implementation and QA

1. **Correct meaning first:** inventory/version truth, failed-check explanations, misleading catalog-update advice, cancellation/results, and trust prompts. Add tests for complete, partial, and failed checks together.
2. **Apply the vocabulary across screens:** use small presentation mappings over existing typed states. Keep persisted codes and domain enums stable. Avoid a localization-framework rewrite or a generic formatter that guesses meaning from enum names. Update visible text, tooltips, accessibility names, activity messages, and exports deliberately.
3. **Review catalog-authored copy separately:** use the same terminology, preserving evidence and publication controls. No vendor re-research unless a specific sentence cannot be resolved from its source.

Tests should cover table/detail consistency; a detected app with an unknown version; combined inventory/update failures; exact/unknown/no-match device states; vendor-page versus download/cache routes; changed host identity; cancellation requested versus confirmed; unverified outcomes; and catalog restart/offline wording. Existing authority tests must stay unchanged in intent.

Some current tests freeze the prose itself: the Safety window smoke contract requires the phrase “independent compiled worker,” and presentation tests locate “Installed-version evidence.” Update those assertions deliberately with the copy change while keeping behavioral/safety assertions; do not preserve confusing wording just to satisfy a string match, or weaken the tests merely to pass.

Validation for this implementation includes focused presentation/diagnostics/delivery/update tests, the full C# suite, Release build, WPF structural smoke, relevant source/endpoint checks, and format/diff verification. No signing, release, or catalog-publication work is needed for application copy alone.

Human gate: use realistic app states and ask a technician to explain what happened and choose the next action without developer help. Exercise the supplied warning, unknown installed version, CP4N and an unresolved device, vendor download, changed-host warning, cancel-pending state, and saved-catalog restart. Check keyboard/screen-reader labels, visible focus, text wrapping, and resize/high-DPI layout. Automated structure or blank screenshots cannot establish that acceptance.

Done means the main screens explain tasks and consequences plainly, uncertainty stays truthful, technical detail remains available, and no copy change changes execution authority. The compiled-UI implementation meets that source/automated gate; human interactive acceptance remains pending.
