# Third-party notices

This document inventories software used to build, test, or run AV Workstation
Toolkit. AV Workstation Toolkit itself is licensed under
[Apache-2.0](LICENSE). Third-party components remain governed by their own
licenses, and this document does not replace those licenses.

Versions are taken from the locked NuGet graph, pinned build projects, and
immutable GitHub Actions references used for version 1.1.1. “Distributed”
means present in the normal EXE/MSI/ZIP release chain. Commercial AV products
listed in the application catalog are metadata records only; their software is
not part of this project or its normal release artifacts.

## Runtime and packaged components

| Component | Version | Source | License | Role | Distributed | Notice requirement |
|---|---:|---|---|---|---|---|
| SSH.NET | 2026.0.0 | [sshnet/SSH.NET](https://github.com/sshnet/SSH.NET/tree/7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e) | MIT | Authenticated SFTP runtime | Yes, embedded in the untrimmed single-file EXE and worker | Retain the SSH.NET MIT notice and its BCrypt notice below. |
| BouncyCastle.Cryptography | 2.7.0 | [bcgit/bc-csharp](https://github.com/bcgit/bc-csharp/tree/4007498b13582d90ee1eda5d9920c324428b98b3) | MIT | SSH.NET cryptography dependency | Yes, embedded in the untrimmed single-file EXE and worker | Retain its MIT copyright and license below. |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | [dotnet/runtime](https://github.com/dotnet/runtime/tree/eba546b0f0d448e0176a2222548fd7a2fbf464c0) | MIT | SSH.NET transitive runtime dependency | Yes, linker output confirms packaged code | Covered by the .NET license and exact upstream notices embedded in the EXE. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | [dotnet/runtime](https://github.com/dotnet/runtime/tree/81cabf2857a01351e5ab578947c7403a5b128ad1) | MIT | Resolved transitive dependency | Yes, embedded in the untrimmed compiled runtime | Retain the upstream MIT license coverage. |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.11 | [NuGet package](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.11) | MIT plus upstream notices | Self-contained .NET runtime | Yes, as the self-contained runtime | The exact package `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` are embedded and hash-verified into the versioned runtime notice directory. |
| Microsoft.NETCore.App.Host.win-x64 | 10.0.11 | [NuGet package](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64/10.0.11) | MIT plus upstream notices | Native single-file application host | Yes | Uses the same reviewed .NET 10.0.11 license and notice set as the runtime package. |

The current ZIP and MSI contain the same single-file executable. The MSI has no
third-party custom action or vendor payload. At launch, the executable makes
this file, the exact .NET license, and the exact .NET third-party notice file
available beneath its deterministic, hash-verified `notices` runtime directory.

## Build, test, and hosted-service components

| Component | Version | Source | License / terms | Role | Distributed | Notice requirement |
|---|---:|---|---|---|---|---|
| Microsoft.NET.ILLink.Tasks | 10.0.11 | [dotnet/dotnet](https://github.com/dotnet/dotnet/tree/e2f47b0110ed922f21a1522da67279133ce28f32) | MIT | Publish trimming | No | Build-only inventory item. |
| .NET SDK | 10.0.100 minimum; latest installed stable .NET 10 feature band | [dotnet/sdk](https://github.com/dotnet/sdk) | MIT plus upstream notices | Build and test SDK | No | Build-only; the selected SDK is recorded in release provenance. |
| WixToolset.Sdk | 6.0.2 | [wixtoolset/wix](https://github.com/wixtoolset/wix/tree/b3f340393117094a75ea8ced77f2357e4aa095e7) | Microsoft Reciprocal License for source; official binary package also carries the Open Source Maintenance Fee agreement | MSI build tool | No WiX binary or custom action is present in the MSI | Owner review of the official binary agreement is required before revenue-generating builds. This is separate from the project's license choice. |
| PSScriptAnalyzer | 1.24.0 | [PowerShell/PSScriptAnalyzer](https://github.com/PowerShell/PSScriptAnalyzer/tree/4b4a136d3b669a1fc127f182e7360160e4919acb) | MIT; upstream notices also identify Newtonsoft.Json under MIT | CI and local static analysis | No | Build/test only. |
| actions/checkout | 6.1.0, commit `d23441a48e516b6c34aea4fa41551a30e30af803` | [actions/checkout](https://github.com/actions/checkout/tree/d23441a48e516b6c34aea4fa41551a30e30af803) | MIT | GitHub Actions checkout | No | Hosted build-service dependency; immutable commit pin retained. |
| actions/setup-dotnet | 5.4.0, commit `26b0ec14cb23fa6904739307f278c14f94c95bf1` | [actions/setup-dotnet](https://github.com/actions/setup-dotnet/tree/26b0ec14cb23fa6904739307f278c14f94c95bf1) | MIT | GitHub Actions SDK setup | No | Hosted build-service dependency; immutable commit pin retained. |
| actions/upload-artifact | 7.0.1, commit `043fb46d1a93c77aae656e7c1c64a875d1fc6a0a` | [actions/upload-artifact](https://github.com/actions/upload-artifact/tree/043fb46d1a93c77aae656e7c1c64a875d1fc6a0a) | MIT | Private CI artifact retention | No | Hosted build-service dependency; immutable commit pin retained. |
| actions/download-artifact | 8.0.1, commit `3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c` | [actions/download-artifact](https://github.com/actions/download-artifact/tree/3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c) | MIT | Verified signed release transfer between jobs | No | Hosted build-service dependency; immutable commit pin retained. |
| signpath/github-action-submit-signing-request | 2.3, commit `c92b958760219087e01f8d67a1669ed57afe2627` | [SignPath GitHub signing action](https://github.com/SignPath/github-action-submit-signing-request/tree/c92b958760219087e01f8d67a1669ed57afe2627) | MIT | Trusted GitHub artifact submission and signed-artifact retrieval | No | Hosted signing-service integration; immutable commit pin retained. |
| Git | Developer/runner supplied; version not pinned | [git/git](https://github.com/git/git) | GPL-2.0-only | Commit and dirty-tree provenance | No | External build tool only; its copyleft code is not linked into or redistributed with AV Workstation Toolkit. |
| GitHub CLI | Runner supplied; version not pinned | [cli/cli](https://github.com/cli/cli) | MIT | Tagged-release publication | No | External release tool only. |

Windows PowerShell 5.1, WPF/.NET Framework, the registry, Credential Manager,
Explorer, Windows Installer, Microsoft Defender, the Windows SDK signing tool,
and WinGet/Desktop App Installer are Windows or Microsoft-supplied platform
integrations. AV Workstation Toolkit does not redistribute those components.

## Source, assets, and optional payloads

- No third-party source, submodule, Git LFS object, font, icon, installer, or
  vendor binary is vendored in the maintained source tree.
- No binary artwork, screenshot, font, icon, or other third-party asset is
  tracked in the maintained source tree.
- Test fixtures are bounded project fixtures, not redistributable vendor
  software.
- The commercial AV catalog records product names and official links for
  knowledge and inventory. Catalog inclusion never copies or licenses a
  vendor's application.
- Optional offline bundles are outside the normal GitHub release boundary.
  Each locally supplied payload requires a separate redistribution-rights and
  license review before an operator creates or shares such a bundle.

## Project-license context

The packaged runtime dependencies above use permissive MIT-style terms. No
GPL/copyleft runtime dependency was found. Git is GPL-2.0-only but is an
external build tool. WiX is build-only under MS-RL source terms and the
separate binary-package agreement described above. Relevant patent-related
materials include the [.NET patent promise](https://github.com/dotnet/runtime/blob/eba546b0f0d448e0176a2222548fd7a2fbf464c0/PATENTS.TXT) and the patent terms in
MS-RL. The project license is Apache-2.0; this status does not alter the
licenses, notices, redistribution terms, or legal review needs of the
third-party components above.

## Included permission notices

### SSH.NET — MIT

Copyright (c) Renci, Oleg Kapeljushnik, Gert Driesen and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### BouncyCastle.Cryptography — MIT

Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### BCrypt implementation carried by SSH.NET

Copyright (c) 2006 Damien Miller

Copyright (c) 2010 Ryan D. Emerle

Permission to use, copy, modify, and distribute this software for any purpose
with or without fee is hereby granted, provided that the above copyright
notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
