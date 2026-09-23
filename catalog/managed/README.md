# Embedded managed-catalog baseline

A production release must embed the exact immutable signed managed catalog approved for public distribution as
`AVWT-Managed-Catalog.avwtmanaged` in this directory, or pass that file to `Build-Release.ps1` with
`-ManagedCatalogBaselinePath`.

The bundle is public data and must verify as catalog ID `avwt-managed` with the production managed public key compiled
into both the application and worker. Never place a private key, unsigned manifest export, credentials, publisher
working data, or development-signed bundle here.

No production managed baseline is present until the owner provisions the distinct managed signing authority, approves
the public-key handoff, and signs revision 1. Production builds remain intentionally blocked until that happens.
