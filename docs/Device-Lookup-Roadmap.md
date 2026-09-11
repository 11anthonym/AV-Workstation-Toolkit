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

The first bounded expansion adds exact Crestron CP4N/RMC4/PRO4, AMX NX-1200/NX-2200/NX-3200/NX-4200, Extron IPCP Pro, IPCP Pro xi/Q xi, and retired IPL Pro S1 identities. Extron generations remain distinct: their Global Configurator, Global Scripter, and Toolbelt relations are independently scoped and retain certification, project, and firmware constraints. Q-SYS Core 110f remains an `AudioDsp` identity; its Q-SYS Designer relationship is verified, while the applicable Designer branch remains conditional on hardware memory/revision.

This pass adds no package record, download path, credential rule, worker capability, or device action. The next expansion must remain category-bounded and evidence-backed; older AMX controller generations, unlisted Extron models, installed-version identity, coexistence, account-gated acquisition, and firmware/project compatibility remain unresolved.

## Audio DSP / conferencing processor Batch A complete

Batch A adds evidence-scoped exact identities for Q-SYS Cores, Biamp TesiraFORTÉ X and reviewed AVB models, Extron DMP 64/128 Plus and FlexPlus processors, Shure IntelliMix P300, ClearOne CONVERGE Pro 2, Symetrix Radius/Prism/Edge and Jupiter, Bose ControlSpace EX/ESP, Allen & Heath AHM, and Yamaha DME/MRX/MTX processors. The software relations remain model-scoped: Q-SYS Core 110f has a verified but hardware-revision-conditioned Designer relationship; Shure Designer and firmware must match P300 evidence; Symetrix Composer/firmware and Yamaha generation/firmware/project pairing remain explicit constraints; Jupiter retains its separate legacy software workflow.

## Audio DSP / conferencing processor Batch B complete

Batch B adds 50 exact hardware identities across BSS Soundweb OMNI/London, Xilica Solaro, Rane Halogen, Crestron Avia, AtlasIED BlueBridge/Atmosphere, Ashly AquaControl/Protea, and Polycom SoundStructure. The relationship scopes deliberately separate OMNI AVX from Soundweb London legacy software, add the missing Avia Audio Tool and SoundStructure Studio descriptive products, and preserve the existing MasterInstaller boundary for Crestron Toolbox.

AtlasIED Atmosphere AZM processors are known hardware with `Unresolved` workstation-software coverage because the reviewed primary configuration surface is embedded browser control. They do not inherit BlueBridge Designer. BSS BLU-120/320/326DA remain excluded from this AudioDsp pass because they are I/O expanders; Ashly 3.6SP/4.8SP remain deferred to loudspeaker processing. All version, firmware, licensing, coexistence, and Windows-support caveats remain descriptive only—no identity or relation creates a package, download, credential, worker, firmware, or device-action route.

## AV-over-IP endpoint Batch A complete

Batch A is complete across seven manufacturers, 12 endpoint families, and 90 exact models: Crestron DM NVX, Extron NAV 1G/10G, AMX SVSI N2300/N2400/N2600, ZeeVee ZyPer4K/ZyPerUHD60, Atlona OmniStream video, Lightware UBEX, and Q-SYS NV/NVM. Lightware UBEX exact coverage includes F-series, five distinct R100 connector variants, and the UBEX-MMU-X200 management unit. LDC is scoped to discovery/configuration and LDU2 to conditional firmware guidance; F110/F120 retain their transitional replacement caveats, and the MMU/web interface remains descriptive rather than a software product. Q-SYS covers NV-32-H, NV-21-HU, NV-1-H-WE, NVM-302E, and NVM-302D through the existing Q-SYS Designer product only; Configurator and Peripheral Manager remain workflow descriptions, not duplicate products. NV-32-H retains its Core Mode caveat, NV-1-H-WE remains encoder-only, and NVM encoder/decoder roles are explicit.

The endpoint relationships remain exact or family-scoped as documented. Browser and management-appliance workflows are constraints rather than desktop products; no protocol, manufacturer, endpoint role, or hardware identity grants package, download, credential, worker, firmware, or device-action authority.

## AV-over-IP endpoint Batch B complete

Batch B adds seven bounded vendor scopes, 10 families, 36 exact identities, and 78 lookup aliases: Aurora IPBaseT VPX/VLX, Visionary PacketAV 5-Series and E4200/D4200 installed-base endpoints, Kramer KDS-7, Matrox ConvertIP, AVPro Edge MXnet 1G, WyreStorm NetworkHD 500/600, and Just Add Power MaxColor. Twenty-nine exact models have verified software relations and seven are explicitly unresolved. It adds 25 relations and refines one existing Matrox relation. New desktop products are limited to evidence-backed Aurora IPBaseT Manager, Visionary VLite, WyreStorm Management Suite/Series Console, and Just Add Power AMP/JADConfig; existing Matrox ConvertIP Manager and ConductIP remain distinct.

KDS-7 and MXnet identities are intentionally `Unresolved` because their reviewed management paths are appliance/controller or browser-hosted rather than proven desktop applications. Command Center, ConductIP appliance behavior, CBOX, Mentor, embedded endpoint pages, and KDS-7 management infrastructure are descriptive only. VLite keeps PacketAV 5-Series separate from E4200/D4200’s one-way 2.3.169 firmware transition; NetworkHD 500 and 600 scopes stay separate; AMP is the current MaxColor recommendation while JADConfig remains legacy with end of support on January 1, 2027. No Batch B identity or relation creates package, download, credential, worker, firmware, or device-action authority.

## Cameras / conferencing devices Batch A complete

Batch A adds 8 camera/conferencing families and 36 exact identities for AVer, Logitech, HP Poly, Cisco, Q-SYS, and Huddly. Twenty exact models have verified software/workflow relations and 16 remain explicitly unresolved. AVer Room Management is scoped to the documented CAM/VB models and PTZApp 2 remains a separate legacy-service search result. Logitech Sync is limited to documented room-device monitoring; Logitech Tune is not inherited. Q-SYS NC cameras use the existing Q-SYS Designer product through camera-specific configuration and commissioning relations, while Configurator/Peripheral Manager remain workflow descriptions.

Cisco Room/Board and the reviewed Poly Studio USB, Studio X, and G7500 identities intentionally remain `Unresolved` for desktop applicability: RoomOS, embedded device web interfaces, Control Hub, and Poly Lens cloud administration are not fabricated as Windows products. Huddly Connect remains the evidence-backed Huddly desktop workflow. Browser, cloud, device-OS, firmware, and appliance workflows are descriptive only. Cameras/conferencing Batch B remains deferred for Crestron 1 Beyond, Sony, Panasonic, Lumens, PTZOptics, Jabra, Yealink, Neat, and other vendors.

## Cameras / conferencing devices Batch B complete

Batch B adds 12 families and 39 exact identities across Crestron / 1 Beyond, Sony Professional, Panasonic, Lumens, PTZOptics, Jabra, Yealink, and Neat. Nineteen models have evidence-backed desktop relations; 20 intentionally remain `Unresolved`. Sony RM-IP Setup Tool is limited to the reviewed SRG setup workflow. Panasonic Media Production Suite and EasyIP Setup Tool Plus are scoped to the reviewed AW-UE models. PTZOptics Camera Management Platform is scoped to Move 4K and Link 4K, Jabra Direct to reviewed PanaCast models, and Yealink USB Connect to reviewed UVC USB cameras.

Crestron 1 Beyond cameras, Lumens cameras, Sony SRG-A40/BRC-X1000, Yealink MeetingBar appliances, and Neat room devices remain known hardware without a fabricated desktop relation. Automate VX/Camera Manager appliance workflows, browser configuration, Neat Pulse, device OS, cloud administration, protocols, and firmware transfers remain descriptive only. The material camera/conferencing scope is now complete for the two bounded batches; deferred vendors remain candidates for a future high-value-device pass rather than implied coverage.

Camera/conferencing category: **DONE** for the agreed Batch A/B scope. Any later additions require the same exact-model and evidence-scoped review; they must not inherit desktop applicability from manufacturer or protocol similarity.

## Displays / projectors first coverage pass complete

The first bounded display identity pass adds 5 families and 22 exact models for Barco, Christie, Epson, Panasonic, and Sharp NEC Display Solutions. Barco Projector Toolset is now exact-model scoped to reviewed UDM, UDX, F-series, and G100 models. Christie Twist, Mystique, and Conductor are limited to the reviewed Griffyn, M 4K RGB, and Crimson models. Epson Projector Professional Tool is scoped to documented Pro L models, Panasonic Geometry Manager Pro to reviewed PT-RQ/PT-MZ/PT-DZ models, and NaViSet Administrator 2 to reviewed Sharp NEC displays.

This remains a first service-relevant scope rather than a comprehensive projector inventory. Panasonic Visual Software Suite migration, Christie array/camera requirements, Epson projector firmware requirements, and NaViSet model/network support remain descriptive constraints. No display identity or relation creates package delivery, credentials, firmware execution, worker authority, or device-action capability.

## Control interfaces and short-model lookup follow-up complete

The device catalog now resolves the unambiguous `110F` marking to the exact Q-SYS Core 110f identity instead of leaving it as a weak substring match. Official Q-SYS documentation verifies the Designer relationship while preserving the important uncertainty at the release boundary: 2 GB units shipped before approximately April 2017 are not supported by Q-SYS Designer v10 or later, upgraded units must be verified by memory/revision, and Core 110f v2 has its own minimum mainline/LTS requirements. Search therefore shows Q-SYS Designer with conditional version guidance, never an inferred automatic release or package action.

This bounded follow-up also adds reviewed RDL D/DB/DS-BTN21 and DD/DDB/DDS-BTN44, Extron Network Button Panel, and Kramer SL-240C/RC-74DL identities. RDL Console is limited to the documented Bluetooth-interface configuration workflow. Extron Toolbelt covers device discovery/management while Global Configurator Plus/Professional and Global Scripter cover the separately documented NBP project workflows. Kramer K-Config is limited to RC-74DL, K-Upload to conditional SL-240C firmware service, and Site-CTRL to legacy RC-74DL monitoring; the first-pass assumption that K-Config applied to SL-240C was removed after official source verification. Middle Atlantic RLNK-910R remains a known exact model with intentionally unresolved desktop-software coverage because RackLink's native/browser, mobile, cloud, and control-system surfaces are not a verified Windows desktop product. The report-suggested MXNet SKUs were not attributed to WyreStorm because the supplied evidence did not establish that manufacturer/scope.

## Signal distribution / switching / extension Batch A complete

This bounded pass reviews Extron, Crestron, Lightware, Kramer, Atlona, Key Digital, Hall Technologies, Gefen, Liberty / Intelix, and MuxLab. It adds 22 signal-distribution families and 69 exact models: 30 have verified desktop relationships and 39 deliberately remain unresolved. The verified scopes are limited to Extron PCS/Firmware Loader/XTP System Configuration, Lightware LDC/LDU2, Kramer Network, Atlona Velocity Device Manager, and Key Digital KDMS Pro where official product documentation establishes the exact workflow.

Known hardware without a proven Windows relationship remains useful and visible. Crestron DM matrices/endpoints, Kramer presentation switchers, Atlona Opus, Key Digital, Hall, Gefen, Intelix, and traditional MuxLab devices retain browser, front-panel, protocol, or manual service workflows as descriptive context rather than fabricated software. XTP and DTP2 remain distinct; Lightware Taurus/MMX2 do not inherit UBEX relations; traditional signal products do not inherit DM NVX, OmniStream, or other AVoIP applicability. The batch adds no package, download, credential, firmware-execution, worker, or device-control authority.

Remaining high-value gaps include broader current Key Digital/Hall exact support matrices, Crestron Toolbox qualification for individual DM generations, and controlled verification of legacy browser/USB utilities. Those gaps remain explicit evidence work rather than inferred compatibility.

## Next coverage requirement

## Frozen holistic Priority-1 denominator

The post-signal-distribution baseline is 52 manufacturers, 104 families, 447 exact models, 880 family/model aliases, 10 categories, 360 models with verified software relationships, 87 unresolved models, and 308 `DeviceSoftwareRelation` records. The bounded holistic expansion freezes 53 additional exact identities across seven implementation waves, producing exactly 500 exact models when complete; Wave 8 is a residual audit rather than an open-ended market survey.

| Wave | Frozen exact-model denominator |
|---|---|
| 1 — Installed microphones / wireless | Shure MXA920, MXA902, MXA710-2FT, ANIUSB-MATRIX, ULXD4D; Sennheiser TeamConnect Ceiling 2 and TeamConnect Ceiling Medium; Audio-Technica ATND1061DAN |
| 2 — Wireless presentation / BYOD | Barco CX-20, CX-30, CX-50 Gen2, C-10; Mersive Solstice Pod Gen3; Crestron AM-3200-WF and AM-3100-WF; Extron ShareLink Pro 1100; Kramer VIA Connect2; ScreenBeam 1100 Plus |
| 3 — Amplifiers / loudspeaker processing | Powersoft UNICA 8K8, MEZZO 604 A, T604 A, X8; LEA Connect 354 and Connect 704; Q-SYS CX-Q 4K4 and CX-Q 8K8; d&b 40D and D80; L-Acoustics LA12X |
| 4 — Recording / streaming | Blackmagic HyperDeck Studio HD Mini, HyperDeck Studio HD Plus, HyperDeck Studio 4K Pro; Epiphan Pearl Mini and Pearl-2; Magewell Ultra Encode AIO; AJA HELO Plus |
| 5 — Assistive listening / network audio | ListenWIFI LW-100P and LA-490; Williams AV WaveCAST C and FM T55; Audinate Dante AVIO USB Adapter |
| 6 — Power / control / utility | Crestron CEN-IO-COM-102, CEN-IO-RY-104, CEN-IO-DIGIN-204; WattBox WB-800-IPVM-12; SurgeX SX-1120-RT; Middle Atlantic RLNK-415R-IEC |
| 7 — Specialized video processing | Analog Way Aquilon RS alpha and Aquilon C+; tvONE CORIOmaster2; RGB Spectrum Galileo GAL16; VuWall PAK 40; Datapath VSN1172 |
| 8 — Residual gap sweep | Reconcile the 53 identities above against the manifest; add no model unless a concrete Priority-1 omission is found and documented within the same 500-model ceiling. |

Each denominator entry must end as an exact identity with either an evidence-scoped `DeviceSoftwareRelation`, a documented non-desktop workflow, or explicit unresolved coverage. The frozen list is the completion ledger; protocols, manufacturer similarity, and device identity never imply software applicability.

### Wave progress

- **Wave 1 complete — installed microphones / wireless.** Added eight exact identities across Shure MXA/ULX-D, Sennheiser TeamConnect Ceiling, and Audio-Technica ATND1061 families. Shure Designer/Update Utility, Wireless Workbench, Sennheiser Control Cockpit, and Audio-Technica Digital Microphone Manager are separately evidence-scoped; Dante, media-control, Q-SYS certification, and browser-client surfaces create no inferred relation or execution authority.
- **Wave 2 complete — wireless presentation / BYOD.** Added ten exact identities across ClickShare, Solstice, AirMedia, ShareLink Pro, VIA, and ScreenBeam. Solstice Dashboard, Extron PCS, and ScreenBeam CMS Enterprise are limited to documented management/firmware scopes. ClickShare client/XMS, AirMedia web/XiO Cloud, and VIA client/appliance workflows remain descriptive and do not become configuration products or package authority.
- **Wave 3 complete — amplifiers / loudspeaker processing.** Added eleven exact identities across Powersoft, LEA Professional, Q-SYS, d&b, and L-Acoustics. ArmoníaPlus, SharkWare, Q-SYS Designer, R1, and LA Network Manager remain evidence-scoped to their reviewed amplifier families; CX-Q is retained as service-relevant legacy hardware, and every firmware/project/Core pairing remains descriptive rather than execution authority.
- **Wave 4 complete — recording / streaming.** Added seven exact identities across Blackmagic HyperDeck Studio, Epiphan Pearl, Magewell Ultra Encode, and AJA HELO Plus. HyperDeck Setup and AJA eMini-Setup are model-scoped desktop utilities; Pearl and Ultra Encode remain explicit unresolved desktop coverage because their reviewed service surfaces are browser, local console, or cloud—not fabricated Windows products.

## Next coverage requirement

Implement the frozen holistic denominator above without reopening completed category-wide audits. The machine-readable identity catalog continues to measure known models, aliases, verified and unresolved model coverage, family-only coverage, and category coverage.

## Definition of done

The feature is comprehensive only when an agreed commercial-AV device-family/model ledger has explicit coverage outcomes, evidence-backed software relations where applicable, clearly displayed unresolved cases, and regression tests for exact model, alias, family, and no-result behavior across each covered category.
