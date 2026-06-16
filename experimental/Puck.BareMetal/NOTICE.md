# Third-party notice — Puck.BareMetal

Puck.BareMetal is part of ByteTerrace.Puck and is distributed under the repository's license
(PolyForm Noncommercial 1.0.0 — free for noncommercial use; commercial use requires a separate
license — see [`LICENSE`](LICENSE) and the root `LICENSING.md`).

The vendored third-party components below are **not** original to Puck. Each keeps its **own**
permissive license and copyright; those licenses permit redistribution inside this work. Only the
combined whole and the ByteTerrace-authored glue are under the Puck license.

## mimalloc

- **Path:** [`mimalloc/`](mimalloc/) — Microsoft, <https://github.com/microsoft/mimalloc> (`v3.3.2`)
- **License:** MIT — see [`mimalloc/LICENSE`](mimalloc/LICENSE)
- **Use:** the heap allocator for the hosted (non-UEFI) binary, compiled from source into the
  freestanding link. The no-CRT bring-up glue is `compat/native/mimalloc-glue.c` (ours).

## mbedTLS

- **Path:** [`mbedtls/`](mbedtls/) — <https://github.com/Mbed-TLS/mbedtls> (3.6.x)
- **License:** Apache-2.0 (elected from its Apache-2.0 / GPL-2.0-or-later dual offer) — see
  [`mbedtls/LICENSE`](mbedtls/LICENSE)
- **Use:** the TLS 1.2 client for the bare-metal HTTPS path. The freestanding port (config + the
  few CRT shims it needs) is under `compat/native/` and `compat/native/mbedtls-port/` (ours).

## lwIP

- **Path:** [`lwip/`](lwip/) — <https://savannah.nongnu.org/projects/lwip/>
- **License:** BSD (2/3-clause) — see [`lwip/COPYING`](lwip/COPYING)
- **Use:** the IPv4 / NO_SYS TCP/IP stack. Our port (`lwipopts.h`, `arch/cc.h`, the netif driver)
  lives in `compat/native/` (ours).

## Built (not vendored) at the bare-metal target

The bare-metal Vulkan path runs an unmodified Mesa **RADV** ICD and **musl** dynamic linker, built
from upstream source by the recipes in [`radv/`](radv/) and staged at boot (both **MIT**; not
checked into this tree). On real AMD hardware it also loads AMD's redistributable GPU firmware,
which retains its own (proprietary, redistributable) license.

## Puck.Runtime

[`runtime/`](runtime/) is ByteTerrace's own freestanding .NET core library (the NativeAOT system
module), under the Puck license. Portions are derived from **dotnet/runtime**'s MIT-licensed
`System.Private.CoreLib` / `Runtime.Base` — specifically the NativeAOT runtime ABI it must match
(the `MethodTable` layout, the `Rhp*` runtime exports, the startup/static-init contract) and the
minimal `System.*` type shapes. dotnet/runtime is © .NET Foundation and contributors, MIT. The
native runtime glue under `compat/native/` (the `puck-*.c` files) is likewise ByteTerrace's, with
`PuckInitGCStatics` ported from dotnet/runtime's MIT `StartupCodeHelpers.InitializeStatics`.
