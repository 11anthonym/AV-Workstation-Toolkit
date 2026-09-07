# Device Lookup Roadmap

## Current limitation

The compatibility catalog contains reviewed, read-only software-to-device relationships, but it is not yet a comprehensive hardware inventory. A missing model search means that no verified relationship is currently recorded; it does not mean the device needs no software.

## Correctness pass complete

The Find software & devices workflow now preserves the canonical relationship scope selected by search, rather than looking up a display label a second time. It ranks exact model/alias and family matches ahead of weaker matches, accepts conservative whitespace/hyphen separator variants, exposes every valid result in a bounded scroll surface, and explains the difference between an unverified model relationship and no catalog match. Compatibility navigation remains descriptive and cannot select a package or authorize worker activity.

## Next coverage requirement

Comprehensive model lookup needs a structured hardware identity and coverage layer: canonical manufacturer, device category, family, exact models, aliases, lifecycle, and an explicit coverage state. `DeviceSoftwareRelation` remains the sole authority for software applicability; the future identity layer must not infer compatibility or execution authority.

## Definition of done

The feature is comprehensive only when an agreed commercial-AV device-family/model ledger has explicit coverage outcomes, evidence-backed software relations where applicable, clearly displayed unresolved cases, and regression tests for exact model, alias, family, and no-result behavior across each covered category.
