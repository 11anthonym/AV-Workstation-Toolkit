# Device Lookup Roadmap

## Current limitation

The compatibility catalog contains reviewed, read-only software-to-device relationships, but it is not yet a comprehensive hardware inventory. A missing model search means that no verified relationship is currently recorded; it does not mean the device needs no software.

## Correctness pass complete

The Find software & devices workflow now preserves the canonical relationship scope selected by search, rather than looking up a display label a second time. It ranks exact model/alias and family matches ahead of weaker matches, accepts conservative whitespace/hyphen separator variants, exposes every valid result in a bounded scroll surface, and explains the difference between an unverified model relationship and no catalog match. Compatibility navigation remains descriptive and cannot select a package or authorize worker activity.

## Hardware identity foundation complete

`manifests/hardware-identities.json` now supplies a strict, descriptive identity layer. A hardware family owns its manufacturer, category, family aliases, lifecycle, coverage state, and optional compatibility-family crosswalk. Exact models retain stable IDs and aliases while pointing to that family, avoiding duplicated relationship data.

Coverage states are explicit: `VerifiedSoftwareRelationships`, `Unresolved`, and `FamilyOnly`. A verified model/family can expose only the existing `DeviceSoftwareRelation` scope. An unresolved model intentionally exposes no inherited family software, and an uncatalogued search remains distinct from a known unresolved device.

`DeviceSoftwareRelation` remains the sole authority for software applicability. Hardware identity does not create package, vendor-delivery, worker, or execution authority.

## Next coverage requirement

Comprehensive model lookup still needs an agreed, evidence-backed commercial-AV model/family coverage ledger. The new machine-readable identity catalog can measure known models, aliases, verified and unresolved model coverage, family-only coverage, and category coverage before that expansion begins.

## Definition of done

The feature is comprehensive only when an agreed commercial-AV device-family/model ledger has explicit coverage outcomes, evidence-backed software relations where applicable, clearly displayed unresolved cases, and regression tests for exact model, alias, family, and no-result behavior across each covered category.
