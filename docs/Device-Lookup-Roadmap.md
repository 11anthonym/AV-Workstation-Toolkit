# Device Lookup Roadmap

## Current limitation

The compatibility catalog contains reviewed, read-only software-to-device relationships, but it is not yet a comprehensive hardware inventory. A missing model search means that no verified relationship is currently recorded; it does not mean the device needs no software.

## Correctness pass complete

The Find software & devices workflow now preserves the canonical relationship scope selected by search, rather than looking up a display label a second time. It ranks exact model/alias and family matches ahead of weaker matches, accepts conservative whitespace/hyphen separator variants, exposes every valid result in a bounded scroll surface, and explains the difference between an unverified model relationship and no catalog match. Compatibility navigation remains descriptive and cannot select a package or authorize worker activity.

## Hardware identity foundation complete

`manifests/hardware-identities.json` now supplies a strict, descriptive identity layer. A hardware family owns its manufacturer, category, family aliases, lifecycle, coverage state, and optional compatibility-family crosswalk. Exact models retain stable IDs and aliases while pointing to that family, avoiding duplicated relationship data.

Coverage states are explicit: `VerifiedSoftwareRelationships`, `Unresolved`, and `FamilyOnly`. A verified model/family can expose only the existing `DeviceSoftwareRelation` scope. An unresolved model intentionally exposes no inherited family software, and an uncatalogued search remains distinct from a known unresolved device.

`DeviceSoftwareRelation` remains the sole authority for software applicability. Hardware identity does not create package, vendor-delivery, worker, or execution authority.

## Control-processor first coverage pass complete

The first bounded expansion adds exact Crestron CP4N/RMC4/PRO4, AMX NX-1200/NX-2200/NX-3200/NX-4200, Extron IPCP Pro, IPCP Pro xi/Q xi, and retired IPL Pro S1 identities. Extron generations remain distinct: their Global Configurator, Global Scripter, and Toolbelt relations are independently scoped and retain certification, project, and firmware constraints. Q-SYS Core 110f remains an `AudioDsp` identity with unresolved software coverage because its Designer support is hardware-revision and release constrained.

This pass adds no package record, download path, credential rule, worker capability, or device action. The next expansion must remain category-bounded and evidence-backed; older AMX controller generations, unlisted Extron models, installed-version identity, coexistence, account-gated acquisition, and firmware/project compatibility remain unresolved.

## Audio DSP / conferencing processor Batch A complete

Batch A adds evidence-scoped exact identities for Q-SYS Cores, Biamp TesiraFORTÉ X and reviewed AVB models, Extron DMP 64/128 Plus and FlexPlus processors, Shure IntelliMix P300, ClearOne CONVERGE Pro 2, Symetrix Radius/Prism/Edge and Jupiter, Bose ControlSpace EX/ESP, Allen & Heath AHM, and Yamaha DME/MRX/MTX processors. The software relations remain model-scoped: Q-SYS Core 110f remains unresolved because RAM-revision qualification controls Designer support; Shure Designer and firmware must match P300 evidence; Symetrix Composer/firmware and Yamaha generation/firmware/project pairing remain explicit constraints; Jupiter retains its separate legacy software workflow.

DSP Batch B remains: BSS, Xilica, Rane Commercial, Crestron Avia DSP, AtlasIED, Ashly Audio, and Poly SoundStructure. This is descriptive coverage only—no identity or relation creates a package, download, credential, worker, firmware, or device-action route.

## Next coverage requirement

Comprehensive model lookup still needs an agreed, evidence-backed commercial-AV model/family coverage ledger. The new machine-readable identity catalog can measure known models, aliases, verified and unresolved model coverage, family-only coverage, and category coverage before that expansion begins.

## Definition of done

The feature is comprehensive only when an agreed commercial-AV device-family/model ledger has explicit coverage outcomes, evidence-backed software relations where applicable, clearly displayed unresolved cases, and regression tests for exact model, alias, family, and no-result behavior across each covered category.
