# Third-Party Notices — ByteTerrace.Puck

ByteTerrace.Puck is distributed under the repository license (PolyForm Noncommercial 1.0.0 for
noncommercial use; a separate paid license for commercial use — see [`LICENSE.md`](LICENSE.md)
and [`LICENSING.md`](LICENSING.md)). Only the combined whole and the ByteTerrace-authored code
are under the Puck license.

The components below are **not** original to Puck. Each keeps its **own** license and copyright;
those licenses permit redistribution inside this work. **None of them is copyleft** — no
dependency forces the engine's own source open or prevents offering it under paid commercial
terms. Where a component is redistributed in a build, its own license travels with it as that
license requires.

This file is the root inventory. The bare-metal target keeps its own, more detailed notice at
[`experimental/Puck.BareMetal/NOTICE.md`](experimental/Puck.BareMetal/NOTICE.md); it is the
authoritative source for that subtree and is summarized (not duplicated) here. Research,
algorithms, and techniques the engine implements — which carry no redistribution obligation —
are credited in [`ACKNOWLEDGMENTS.md`](ACKNOWLEDGMENTS.md).

---

## Redistributed in the core engine build

| Component | License | Role |
| --- | --- | --- |
| **mimalloc** — Microsoft, <https://github.com/microsoft/mimalloc> (`v3.3`) | MIT | Default unmanaged allocator behind `IAllocator`. Shipped as the native library under [`lib/win-x64/`](lib/win-x64/) and [`lib/linux-x64/`](lib/linux-x64/); see the `LICENSE` beside each. |
| **.NET runtime & libraries** — © .NET Foundation and contributors | MIT | The managed runtime the engine ships on. |
| **Caskaydia Mono Nerd Font** — [Cascadia Code](https://github.com/microsoft/cascadia-code) (Microsoft), patched by [Nerd Fonts](https://github.com/ryanoasis/nerd-fonts) | SIL Open Font License 1.1 | Terminal / on-screen glyphs (baked into the MSDF glyph atlas). OFL-1.1 requires the copyright + license notice to travel with the font data and reserves the original font names for unmodified versions. |

**Build-time only (not redistributed):** the DirectX Shader Compiler (DXC), the Vulkan SDK
(LunarG), and CsWin32 (Microsoft, source generator) are used to build Puck but are not shipped;
their compiled outputs (DXIL/SPIR-V, generated interop) are part of Puck's own build. They are
credited in [`ACKNOWLEDGMENTS.md`](ACKNOWLEDGMENTS.md).

---

## Redistributed in the experimental bare-metal target

`experimental/Puck.BareMetal/` vendors additional components. The list below is a summary; the
per-component paths, versions, upstream URLs, and elected-license details are authoritative in
[`experimental/Puck.BareMetal/NOTICE.md`](experimental/Puck.BareMetal/NOTICE.md).

| Component | License | Notes |
| --- | --- | --- |
| **mimalloc** | MIT | Heap allocator, compiled from source into the freestanding link. |
| **mbedTLS** (`3.6.x`) | Apache-2.0 | Elected from mbedTLS's Apache-2.0 / GPL-2.0-or-later dual offer. TLS 1.2 client for the bare-metal HTTPS path. |
| **lwIP** | BSD (2-/3-clause) | IPv4 / `NO_SYS` TCP/IP stack. |
| **AMD register headers** (`amdgpu/include/`, Linux `v6.15`) | MIT | Register offsets/masks and DRM UAPI headers, vendored verbatim. The kernel's GPL `.c` driver logic is **not** vendored (reference-only, clean-roomed). |
| **RADV (Mesa) + musl** | MIT | Built from upstream at the bare-metal target, not checked into the tree. |
| **AMD GPU firmware (Van Gogh)** | **Proprietary — redistributable, binary-only** | See below. |

### ⚠ AMD GPU firmware — not open source

The signed Van Gogh microcode under `experimental/Puck.BareMetal/amdgpu/firmware/` is **AMD
proprietary software licensed for redistribution in unmodified binary form only** (see
[`LICENSE.amdgpu`](experimental/Puck.BareMetal/amdgpu/firmware/LICENSE.amdgpu)). It is *not*
copyleft — so it does not affect the engine's own licensing — but it is *not* a permissive open-
source license either, and it carries conditions that must be honored on redistribution:

- **Binary only, unmodified** — no reverse engineering, decompilation, or disassembly.
- **Pass-along** — the copyright notice, permission notice, and disclaimers must be reproduced
  alongside the binaries (they are, in `LICENSE.amdgpu`, carried next to the `.bin` files).
- **U.S. export control** — the firmware is subject to U.S. export law (EAR / ITAR are named in
  the notice). Anyone redistributing bare-metal binaries internationally must comply.
- **Liability** is capped at US$10 and warranties are disclaimed.

> **For an attorney, before shipping bare-metal builds commercially or across borders:** confirm
> the export-control posture for the AMD firmware (and, separately, for the mbedTLS cryptography).
> These are redistribution conditions, not licensing blockers for the dual-license model.

---

## Derived code

`experimental/Puck.BareMetal/runtime/` (ByteTerrace's freestanding .NET core module) contains
portions **derived from dotnet/runtime** — the NativeAOT runtime ABI it must match and minimal
`System.*` type shapes. dotnet/runtime is © .NET Foundation and contributors, **MIT**. Details in
[`experimental/Puck.BareMetal/NOTICE.md`](experimental/Puck.BareMetal/NOTICE.md).
