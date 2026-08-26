# Workstation Research Reconciliation and Backlog

This backlog reconciles the external workstation-software research with the actual AV Workstation Toolkit catalogs as of 2026-08-25. It is a development aid, not an installation allowlist. Catalog knowledge, inventory, delivery, and automated execution authority remain separate.

## Reconciliation summary

The report incorrectly treated several existing capabilities as candidates. Wireshark, 7-Zip, VLC, Nmap, Angry IP Scanner, PuTTY, and Everything are already exact-ID managed WinGet applications. Sysinternals Suite, Packet Sender, Tera Term, USB Device Tree Viewer, USBView, AW EDID Editor, Extron EDID Manager, Room EQ Wizard, Dante Virtual Soundcard, Dante Via, NDI Tools, NETGEAR Engage, Lightware Device Controller, Logitech Sync, Barco ClickShare Configurator, AMX NetLinx/N-Able, HARMAN Audio Architect/AVX, Symetrix Composer, and Kramer K-Config already exist in the awareness catalog. Dante Controller, Q-SYS Designer LTS, Crestron Toolbox, Extron Toolbelt, Biamp Tesira, Shure Designer, and Sennheiser Control Cockpit already use constrained operational external-provider records.

This tranche adds only three non-executable awareness records: Windows Pktmon, NDI Analysis, and discontinued NDI Remote. WinMerge, WinSCP, Bruno, FFmpeg/ffprobe, dedicated PTP tooling, and iperf3 remain genuinely absent pending identity, source, publisher, licensing, distribution, role, and baseline review. No default profile or managed WinGet allowlist changed.

## P0 — correctness and security

- Incrementally verify existing high-value metadata and populate `Verification`, `Provenance`, `DistributionPolicy`, and `InstallationForms`; an omitted verification date deliberately remains `VerificationRequired`.
- Review any record when its authoritative domain, publisher, signature policy, download strategy, or lifecycle changes. Quarantine discontinued or conflicting records until a human resolves them.
- Complete legacy Windows compatibility verification for AMX, Kramer, BSS/HARMAN, and other deployed-system toolchains without promoting them into Core or claiming unsupported compatibility.
- Perform product-by-product redistribution and license review before any offline bundling. Public availability alone is not redistribution permission.

## P1 — core diagnostic capability

- Research WinMerge, WinSCP, Bruno, FFmpeg/ffprobe, and iperf3 against their authoritative Windows distribution, publisher, hash, license, and WinGet identities before proposing any catalog or profile change.
- Extend workflow metadata across existing Wireshark, Sysinternals, EDID, USB, serial, socket, file-transfer, and measurement records where authoritative evidence already exists.
- Decide whether workflow packs belong in a future role/profile planner. Keep the default workstation deliberately curated.
- Audit capture and video hardware utilities, including vendor capture-card diagnostics, USB video-device tools, EDID/HDCP analyzers, and signal-validation utilities.

## P2 — protocol and manufacturer expansion

- Research deeper SMPTE ST 2110, PTP, NMOS, IPMX, multicast, and timing-analysis tooling. Do not substitute unverified community binaries for official Windows distributions.
- Audit display and projector fleet tools, focusing on current management utilities, firmware/version coupling, required accounts, and whether the software is Windows-installed, web-only, or embedded.
- Review manufacturer packs as independent overlays for Field Service, Commissioning, Control Programming, DSP, Network/AVoIP, Digital Signage, Broadcast, and Lighting roles.

## P3 — experimental and research queue

- Evaluate capture/video hardware candidates that lack a stable or clearly supported Windows distribution.
- Track emerging IPMX/ST 2110 analysis projects whose Windows packaging, publisher identity, or lifecycle is not yet authoritative.
- Explore compatibility matrices relating project/device firmware to required engineering-tool versions only when vendor evidence is available.
- Revisit portable/offline field-kit caching only after distribution rights, signature/hash policy, storage boundaries, and expiration/reverification behavior are approved.

## First-tranche evidence

- Microsoft documents Pktmon as an in-box Windows network diagnostic, so it is modeled as `WindowsInbox` awareness with no download or action.
- NDI documents Analysis as a separate utility rather than part of the normal NDI Tools launcher.
- NDI documents Remote as discontinued and offline, so its record is non-actionable, source-unavailable, and quarantined.
- Packet Sender, USB Device Tree Viewer, Tera Term, and NETGEAR Engage retain their existing non-automated catalog status while gaining reviewed workflow, installation-form, distribution, provenance, and verification metadata.

The next research pass should update this document with evidence and a policy decision before changing a baseline or execution boundary.
