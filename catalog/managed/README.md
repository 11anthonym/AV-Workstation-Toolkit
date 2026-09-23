# Embedded managed-catalog baseline

A production release must embed the exact immutable signed managed catalog approved for public distribution as
`AVWT-Managed-Catalog.avwtmanaged` in this directory, or pass that file to `Build-Release.ps1` with
`-ManagedCatalogBaselinePath`.

The bundle is public data and must verify as catalog ID `avwt-managed` with the production managed public key compiled
into both the application and worker. Never place a private key, unsigned manifest export, credentials, publisher
working data, or development-signed bundle here.

The tracked bundle is the owner-signed and approved revision 1 (catalog version `2026.9.22.1`, key
`avwt-managed-2026-a`), the current production baseline. Replacing it requires the normal signed managed-catalog
publication and release review.
