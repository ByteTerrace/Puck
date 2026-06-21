# Backend Parity at a Glance: Vulkan ↔ Direct3D 12

Puck runs the same compute/SDF showcase on **either** a Vulkan or a Direct3D 12 backend through one neutral
seam (`Puck.Abstractions`). The two backends are at functional parity across the whole showcase path — the only
differences left are either **intrinsic to the two APIs** (correct as-is) or a **ceiling both share**.

> Full per-row detail, the idiomatic-divergence notes, and hardware-verification provenance live in the
> deep-dive: **[feature-parity-table.md](feature-parity-table.md)**.

## What's at parity (both backends, verified)

| Capability | VK | DX |
|---|:--:|:--:|
| Device / adapter / swapchain / windowed present | ✅ | ✅ |
| Live VK↔DX backend swap at runtime | ✅ | ✅ |
| Graphics pipeline + first-class render passes + draw verbs | ✅ | ✅ |
| Compute pipeline + dispatch + storage images / device-local buffers | ✅ | ✅ |
| Enhanced/explicit barriers (image-layout + memory, real sync+access scopes) | ✅ | ✅ |
| Inline ray tracing (per-frame TLAS; VK ray-query / DX DXR 1.1) | ✅ | ✅ |
| GPU timestamp counters (per-pass GPU-ms) | ✅ | ✅ |
| Host the SDF world on-screen same-device on either backend | ✅ | ✅ |
| **Cross-API zero-copy surface sharing — live present in BOTH directions** | ✅ | ✅ |

The cross-API path routes through a **D3D12-owned** shared resource in both directions (a D3D12 `CreateSharedHandle`
NT handle is the only one both backends can open). Forward = D3D12 produces → Vulkan host presents (`--world`);
reverse = Vulkan produces → D3D12 host presents (`--world --backend directx --produce vulkan`). Both are live,
GPU-verified on an RTX 4070.

## Intrinsic API differences (◆ by design — *not* gaps to close)

These are places the two APIs simply model something differently. The cell is correct as written; there is no work
to do.

- **WARP / software adapter** — D3D12 only; Vulkan has no software-adapter concept.
- **Static samplers** — D3D12 samplers live in the root signature; there is no dynamic sampler object (idiomatic).
- **Win32-only surfaces** — D3D12 presents to Win32 windows only; Wayland/Xcb aren't in its model (Vulkan does both).
- **Shared-handle export type** — Vulkan's `OPAQUE_WIN32` export is Vulkan-openable only, which is *why* cross-API
  sharing routes through a D3D12-owned resource.

## Absent on both (a shared ceiling, not a portability gap)

Neither backend wires these yet — so they're not a Vulkan-vs-DX difference:

- Indirect dispatch / draw
- Async compute / multi-queue / timeline semaphores
- Depth / stencil (the SDF showcase needs none)
- A VMA-style pooled device allocator (raw alloc per resource on both)
- A sampled-image binding in the compute seam (deferred sampler-seam follow-up)

Hardware ray tracing is **not** on this list — it is present and at parity on both.
