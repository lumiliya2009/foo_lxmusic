# Licenses and attribution

This distribution contains two programs:

- `foo_lxmusic.dll`: the original native foobar2000 component, licensed under MIT. It uses the separately licensed foobar2000 SDK, pfc, libPPUI and Windows Template Library.
- `foo_lx_bridge.exe`: the managed bridge under `bridge/`, licensed under GPL-3.0-or-later, except for its separately attributed Apache-2.0 resource. The programs communicate through command-line arguments and a text protocol over standard output.

The root MIT license does not relicense the bridge or third-party dependencies.

## Source provenance

| Component | Origin and license | License copy |
| --- | --- | --- |
| Managed online music code | Adapted from TzPlayer's Windows implementation; its project README declares GPL-2.0-or-later. This bridge uses the later GPL version 3 or later. Original authors retain their rights. | `licenses/TzPlayer-GPL-2.0-or-later.txt`, `bridge/LICENSE` |
| `bridge/Resources/user-api-preload.js` | Derived from `lyswhut/lx-music-mobile` v1.8.4, Apache-2.0. Modified by TzPlayer for source IDs, source-provided search, native secure random bytes and binary HTTP responses; reused by this bridge. | `licenses/LX-Music-Mobile-Apache-2.0.txt` |
| foobar2000 SDK 2025-03-07 | Copyright (c) 2002–2025 Peter Pawlowski. SDK-specific permissive license. SDK sources are obtained separately for building. | `licenses/foobar2000-SDK.txt` |
| pfc and libPPUI | Copyright (c) 2002–2024 Peter Pawlowski. zlib-style licenses. | `licenses/pfc-zlib.txt`, `licenses/libPPUI-zlib.txt` |
| Windows Template Library 9.10 | Copyright Microsoft Corporation and the WTL Team. Microsoft Public License. Headers are obtained separately for building. | `licenses/WTL-MS-PL.txt` |

Upstream LX Music source: https://github.com/lyswhut/lx-music-mobile/tree/v1.8.4

## Managed runtime dependencies

These packages are restored through NuGet. Their binaries are included with component packages, and their licenses remain in effect.

| Package | Version | License / notices |
| --- | --- | --- |
| Jint | 4.16.4 | BSD-2-Clause; `licenses/Jint-BSD-2-Clause.txt` |
| Acornima | 1.7.0 | BSD-3-Clause; `licenses/Acornima-LICENSE.txt`, `licenses/Acornima-NOTICE.txt` |
| Newtonsoft.Json | 13.0.3 | MIT; `licenses/Newtonsoft.Json-MIT.txt` |
| System.Buffers | 4.5.1 | MIT; `licenses/system.buffers.txt` |
| System.Memory | 4.5.5 | MIT; `licenses/system.memory.txt` |
| System.Numerics.Vectors | 4.5.0 | MIT; `licenses/system.numerics.vectors.txt` |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 | MIT; `licenses/system.runtime.compilerservices.unsafe.txt` |

Jint license retrieved at upstream revision `884bd0c4ebf7806f70041eae0370ee11a9c138bc`; Acornima license and notices retrieved at `401bd62d8aeb9f7cbe7b6147937a87e0c71747a8`. Newtonsoft.Json and .NET package license copies are taken from the corresponding NuGet packages.

This repository does not distribute music, album images, imported third-party source scripts, source subscriptions or provider accounts. Availability of online content depends on each provider and the source selected by the user.
