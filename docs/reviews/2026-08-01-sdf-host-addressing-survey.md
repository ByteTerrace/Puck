# `puck.sdf.v1` host-internal-addressing survey — the rest of the `brickWordOffset` class

**Commissioned 2026-08-01 against `00b99c6c`** (read-only survey; no builds, no
runs) to discharge the capability-channels plan's own pre-Phase-3 demand:
*"Parameters that encode host-internal addressing must not round-trip through an
untrusted decoder at all … Find the rest of this class before Phase 3, not
during it."* `SampledRegion.brickWordOffset` was the one known member. This
document is the classified inventory and the decisions it forces — the front
door's decoder spec starts here.

**Scope surveyed:** every author-suppliable parameter of the SDF builder API
(`src/Puck.SdfVm/SdfProgramBuilder.cs`, read end-to-end), the `SdfInstruction`
lane meanings per op/shape, the packed program (`SdfProgram.cs`), the shader
consumption paths (`sdf-vm.hlsli`, `sdf-world.hlsli`), the material-scope
precedent, the engine bind/arbitration points, and the five overlay record
kinds. Checked against `docs/sdf-wiki/verdict-index.md` and the plan's "What the
front door inherits" — nothing here re-opens a standing verdict. **≈155
author-suppliable parameters surveyed** (≈111 SDF ops/shapes/instances, ≈44
overlay), plus 16 host-baked derived lanes, 7 class-B lanes, 9 class-C
reference families.

## The one rule that contains the whole class

The ISA has **no jumps, no loop targets, no author-visible tape addresses** —
the tape is linear and every tape-position value is compiled host-side. The
addressing class therefore lives in specific data lanes plus a sub-class the
plan had not yet named: **host-baked derived lanes** (~16 packed lanes —
reciprocals, baked trig, packed dims, min-axis factors — derived by the builder
from author arguments). If `puck.sdf.v1` were defined over the packed
`Data0`/`Data1` lanes, an author could supply a mismatched pair (e.g. `Repeat`
spacing disagreeing with its baked reciprocal, `SdfProgramBuilder.cs:673-687`)
and silently break march-safety invariants no finite-check catches.

> **The document vocabulary is the builder/writer ARGUMENT surface with
> host-assigned slot bases; packed lanes and table indices never round-trip.**

That one rule host-resolves the entire derived-lane sub-class and preserves
every builder-side validation throw (scope balance, instance nesting,
jitter/spacing, dims range) for free. The overlay corollary: document records
carry *strings* and *clip scopes*; the packer derives glyph offsets, counts,
and clip-table slots.

## Class B — host-internal addressing (host-resolved, or outside the vocabulary)

| # | Lane | Addresses | Arbitrated by | Resolution |
|---|---|---|---|---|
| 1 | `SampledRegion.brickWordOffset` (Data1.z) | base f32 word in the GPU brick pool; `sdfBrickPool[baseWord + …]` **unclamped** (`sdf-vm.hlsli:1207-1211`); upload does NOT validate it (`SdfWorldEngine.cs:985-1003`) | `SdfCarveBakePlanner` slot layout, LRU over 8 slots | **Outside the vocabulary entirely** — documents express carves analytically; brick baking is a host render-cache decision (standing verdict: no baked brick on the geometry channel) |
| 2 | `SampledRegion.packedDims` (dimX/Y/Z ≤1023 each) | the read-extent multiplier over the same pool — a count the shader trusts for addressing; 1023³ ≫ any pool, no render-path capacity check | the planner (bins ≤128/axis) | Falls with #1 — no document form |
| 3 | `ScreenSlab.screenIndex` (0..31) | THREE host tables at once: packed side table, bound screen-source SRVs, decal descriptor band | host code pairing sources to slots | Document declares a screen *surface*; host assigns the physical slot at compose. An author-chosen absolute index shows another tenant's live framebuffer on the author's geometry |
| 4 | `TransformDynamic.slot` / `BeginInstanceDynamic.slot` | per-frame dynamic-transform buffer, `sdfDynamicTransforms[2*slot]` **no shader clamp** (`sdf-vm.hlsli:2010-2011` et al.); host guard is upload-capacity only | composition host assigns contiguous `SlotBase` ranges per emitter (`ISdfSceneEmitter.cs:26-32`) | Document declares its slot **count** and uses slots **relative to its own range**; the host adds the granted base at decode — **the handle-table shape, one layer down** |
| 5 | `SdfInstanceRange.First/End` | instruction-tape positions the packed directories compile from; the raw `SdfProgram(...)` constructor accepts them unvalidated | the builder | Documents carry begin/end-instance *markers* (builder calls); indices never appear; the raw constructor is not a document entry point |
| 6 | Overlay text-run glyph start/count (words 5-6) + clip index (word 9) | offsets into the shared glyph-word region and clip table; `OverlayWord(textBase + start + column)` **no clamp** (`overlay-unified.frag.hlsl:567`); clip read unclamped (`:77-86`) | `OverlayFrameBuilder`'s cursors | Records carry strings and clip scopes; the packer derives all three words |
| 7 | *(adjacent, flagged)* decal `cellBase` — unclamped `sdfDecalCells[cellIndex]` (`sdf-world.hlsli:260, 280-281`) | decal cell table | host at `SetScreenDecal` | Same class if screen decals ever get document forms |

## Class C — reference lanes: the decisions Phase 3 must make

1. **Material ids — the sharpest one, with a live escalation path.** Decide: *a
   document's material references index its own palette section, validated
   `0 ≤ id < paletteCount`, and the sentinel range is unreachable* (screen
   shading expressible only through the screen-surface document shape, whose
   index the host assigns per B.3). Without it: (a) `id ∈ [paletteCount, 65535)`
   reads other packed program tables as colors and, large enough, past the words
   buffer (`sdfMaterialLoad`, `sdf-vm.hlsli:3259-3263`, **no clamp**); (b)
   `id ≥ 65536` enters `sampleScreenSurface` where `screenSurfaces[material−65536]`
   and `sdfDecalCells[…]` are unclamped past 32 entries and `1u << screenIndex`
   wraps mod 32, aliasing another slot's bound-bit. Edge: `AddMaterial` is
   unbounded — the front door owes a `paletteCount < 65535` ceiling.
2. **Positional material strides** (`WallpaperFold`/`RepeatPolar` stride,
   `CellJitter` variants) are deltas in material-id space; the shader guards
   only the sentinel range. Decide: *every contributed document decodes inside a
   mandatory `BeginMaterialScope`*, so the existing clamp
   (`ApplyPositionalMaterialScopeClamp`) bounds reach to the document's own
   palette — the precedent the plan already says to cite.
3. **Glyph text.** `Text`-level only (string + frame + em height; host resolves
   atlas rects, `distanceScale`, layout). Raw `Glyph` UVs are clamped and can
   only show the wrong letter, but `distanceScale` is a march-safety coupling
   the host should own — raw `Glyph` stays a first-party seam.
4. **Overlay roles / glyph ids / icon ids.** Enum-validate at decode against the
   closed sets — these ARE the "overlay record kinds" vocabulary a `Present`
   grant subsets. The shader-side `data[role]` read is unclamped; the decoder is
   the only fence.
5. **Enum lanes generally** (blend, lift, flavor, axis, plane, group, style).
   Shader defaults are benign but *unspecified content* — decode rejects unknown
   values rather than shipping whatever the default arm renders.

## Class A notes that carry admission obligations

- `Displace`/`DomainWarp` amplitude·frequency needs an **admission ceiling**,
  not just finiteness — enforcement of the standing displacement verdict.
- `Repeat`'s in-cell rule is caller-owned and structurally uncheckable by a
  document validator — an admission note, not a lane check.
- `BeginInstance.boundRadius` is the plan's placement-reach vector — reach
  validation at compose (already in the plan).
- `SampledRegion.boundaryFloor` must be host-baked with the brick (falls out of
  B.1 anyway).
- Warp rates (`Twist`/`Bend`) require `AnalyzeLipschitz` to run in the front
  door.
- Quota anchors: `MaxInstances` 16384, `MaxScreenSurfaces` 32, scope depth 1;
  overlay `MaxPanels` 16 / `MaxElements` 1024 / `TextWordCapacity` 16384 /
  `MaxClips` 32 — a cannot-overflow **backstop, not a budget**, since unit 6b
  replaced the narrate-once-then-drop posture with per-channel reservations that
  own the actual spend.

## Named as unverified, not omitted

- **OOB severity on Vulkan**: D3D12's robust SRV semantics return zero for the
  unclamped reads; whether the Vulkan backend enables `robustBufferAccess` on
  all four fleet GPUs was NOT verified. Classification does not depend on it
  (naming host memory is disqualifying regardless); the exploit ceiling does.
- **Panel `style` word consumption** not traced to every shader read (low risk).
- **Views/anchors/camera rigs** treated as host composition, not audited — if
  views ever get document forms, they need their own pass.
- **Queries** are the `request` channel's vocabulary, out of scope here by the
  plan's own channel table.
