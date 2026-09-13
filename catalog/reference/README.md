# Embedded reference-catalog baseline

A production release must embed the exact immutable signed catalog snapshot approved for public distribution as `AVWT-Reference-Catalog.avwtcatalog` in this directory, or pass that file to `Build-Release.ps1` with `-ReferenceCatalogBaselinePath`.

The bundle is public data and must verify with the production public key compiled into AVWT. Never place a private key, unsigned manifest export, credentials, or publisher working data here. No production baseline is present until the owner completes the production public-key handoff and approves the first signed catalog publication.
