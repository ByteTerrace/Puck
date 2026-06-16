# SoA split of the SDF instruction stream — design record

**Status: BUILT, MEASURED, LANDED on `features/licensing`** (originally scoped + prototyped in
the `Puck-soa-scope` worktree off `cd06f67`). This doc is now a record of the design and
its outcome, not a forward plan.

## Outcome (measured, RTX 4070, Debug, pinned worst angle `bench -Pin 6`)

| mode | before (AoS) | after (SoA) | Δ |
|---|---|---|---|
| rt off worldCompute | 15.04 ms | **9.65 ms** | **−36%** |
| tile off worldCompute | 12.99 ms | **8.12 ms** | **−37%** |

- **Pixel-exact:** `validate` PASS on every tier (rt/tile/splits) — bit-identical render.
- **Registers unchanged** (rt 96 / tile 80 / beam 40) → the win is **pure memory latency**,
  not occupancy. Confirms the hypothesis below directly.
- fps roughly doubled at the pin (rt 62→104, tile 81→128). The worst-angle number is the
  frame-time *floor*; a terrain-dominated typical frame sees a smaller cut (the VM march is a
  smaller slice there).

## Problem

The per-step VM instruction fetch in `map()` was the profiled hotspot: the opcode-extract
feeding the dispatch switch (`avatar-vm.glsl`, ~16% of the rt kernel) stalled on the dependent
48-byte `sdfInstructions[i]` load. The over-relaxation experiment had proven the march is
**per-step-evaluation-bound, not step-count-bound** — so the only lever was making each `map()`
cheaper, i.e. cutting the per-step instruction-stream load.

## What was done

Split the GPU `SdfInstruction` (AoS, 48 B) into two parallel streams:

| stream | contents | bytes/instr | read frequency |
|---|---|---|---|
| **header** (avatar set binding 0) | `uvec4` (opcode, shapeType, blendOp, material) | 16 | **every instruction, every step** (the dispatch) |
| **data** (NEW binding 7) | `vec4 data0; vec4 data1;` | 32 | per instruction (this version loads it eagerly) |

## Mechanism (hypothesis — CONFIRMED by the result)

The dispatch loop re-walks an actor's instruction range once per march step (≤60). A single
actor's stream (≤192 instr × 48 B ≈ 9 KB) fits L1 — but a **warp** has 32 lanes each marching a
*different* actor at a *different* step, so the aggregate per-step working set is up to
32 × 9 KB ≈ 288 KB > L1 (128 KB on Ada). The dispatch loads then miss to L2/VRAM — the
dependent-load stall the profiler pinned.

SoA shrinks the **dispatch** working set 3×: header-only is 192 × 16 B ≈ 3 KB/actor, so a warp's
aggregate header set ≈ 96 KB **fits L1**. The dispatch (which depends only on the dense header)
becomes L1-resident; the 32 B data loads are off the critical path. The unchanged register count
in the result confirms this was a latency win, not occupancy.

> This had the same sound-looking shape as over-relaxation (which regressed), so it was treated
> as a hypothesis the bench had to confirm — which it did, decisively and pixel-exact.

## Pixel-exact by construction

SoA moves only *where* bytes live, not their values — the march reads the identical opcode/data
and computes the identical field. So the render is bit-for-bit unchanged (modulo glslc `-O`
nondeterminism on the recompiled `.spv`). No content re-bless; the only baseline step is
re-recording the kernel **byte-identity** baseline (the `.spv` bytes changed).

## Host (C#) changes

- `AvatarSdfInstruction.cs` (48 B combined) → **deleted**, replaced by one-file-each
  `AvatarSdfInstructionHeader` (4×`uint`, 16 B) + `AvatarSdfInstructionData` (2×`Vector4`, 32 B),
  both `[StructLayout(Sequential, Pack=16)]`, namespace `Puck.Characters`.
- `TerminalAvatarSdfService`: `Instructions[]` → two slot-aligned arrays `InstructionHeaders[]` +
  `InstructionData[]`; `PackInstructions` writes both; `ClearPackedArrays` clears both; the
  capacity guard reads `InstructionHeaders.Length`.
- `TerminalVulkanCompositionBackend`: new `m_avatarInstructionDataBuffer` threaded through the
  field decl, the device-restore context (`TerminalVulkanPresentationResourceDisposalContext`),
  `EnsureAvatarBuffers` (size = `Length × 16` for headers + a `× 32` data buffer, create + bind),
  and `UploadAvatarSdfBuffersIfNeeded` (writes both arrays).
- `TerminalVulkanPresentationResourceService`: disposes the new buffer.
- `VulkanNativeAvatarBindingApi` + `IVulkanAvatarBindingApi`: `AvatarStorageBufferCount` 7→8; the
  data buffer is the 8th `(buffer,size)` → bound at `DstBinding = 7`. The avatar set-1 layout is
  single-sourced here (shared by the compute pipeline layout), so this is the only layout to grow.

## GLSL changes (all in `avatar-vm.glsl`)

- Binding 0: `SdfInstruction sdfInstructions[]` → `uvec4 sdfInstructionHeaders[]`.
- New binding 7: `SdfInstructionData { vec4 data0; vec4 data1; } sdfInstructionData[]`.
- A `loadSdfInstruction(int)` helper reconstructs a local `SdfInstruction` from the two streams;
  the four read sites (`map()`, `mapGradient()`, two chart helpers) call it, and the two
  `.length()` guards point at `sdfInstructionHeaders`. The beam kernel never reads binding 0, so
  it does not recompile.

## Verification done

Build 0/0 (warnings-as-errors); `validate -AllowShaderDrift` PASS all tiers (pixel-exact);
Vulkan validation layer clean (the new binding/descriptor write is correct).

## Deliberately deferred (possible follow-up)

This is the **eager-reconstruct** form — it captures the cache-density win but still loads the
32 B data every instruction. A **data-skip** variant (defer the data load past the per-shape AABB
gate so gated shapes never touch their parameters) is a plausible further gain at the worst angle
where many shapes gate, and is left as a separate change.
