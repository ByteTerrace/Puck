# Shader build mechanics

Read this before touching DXC build steps, kernel variants, pass labels/timing, or
descriptor/register wiring. Back to [../SKILL.md](../SKILL.md) for engine semantics,
the sync-pairs table, composition/anchors/views, and gotchas.

`coneMarchTileBounds` abandons the gap and tail searches together after
`TileGapStallLimit` (three) consecutive occupied, non-increasing-clearance
samples. It retains the established entry and the far-plane sentinels; a stall
never proves empty space. The other gap outcomes still run `coneMarchFarBound`
with `TileFarSteps` (ten). When changing these heuristics, compare whole-frame
cost and hit/material captures, including grazing rays and separated bands.

Primary, surface, ambient and views are separate dispatches. Their wrappers share
the views entry point; primary defines `SDF_PRIMARY_PASS`, surface and ambient
define their named pass macros, and the consumers use `SDF_PRIMARY_READ`.
Primary, surface and ambient keep the full ISA. All four share the indirect bbox,
active-pixel test, view extent and child mask. Primary writes every active record,
including misses; surface initializes neutral normal/AO rows for every active
record. `RecordHitPass` places compute read/write barriers between producers and
consumers. Cadence-skipped frames run none of these dispatches.
`SdfWorldKernels.Primary`, `.Surface` and `.Ambient`, their reload entries and ISA
reports must accompany every kernel set. Eleven timestamp slots cover frame
start plus all ten pass closes, including skipped frames.

The hit buffer is binding 49 / u6 in the shared views layout, with scalar uint
storage matching the four-byte UAV descriptor stride. `PrimaryHitByteLength`
is 80: twenty words per full-extent pixel per viewport. Rows carry depth/terminal
radius/threshold/material bits; four hit lanes; then frame-slot bits/seam weight/
other-material bits/flags. These flags hold march steps in bits 0–7, saturated
primary query count in 8–30, and hit acceptance in bit 31. Row 3 holds geometric
normal.xyz and gradient magnitude. Row 4 holds curvature, surface/AO query count,
raw AO and reinterpreted flags (bit 0: ordinary lit surface eligible for AO).
Material Soften affects the later lighting normal; AO uses the geometric normal.
Depth and attributes are unquantized. Allocation is width × height × viewport
capacity × 80 bytes and is shared under the cross-frame barrier, like the source
textures. Check memory as well as frame time. Shader A/B can define
`SDF_MONOLITHIC_VIEWS` and pair it with ISA-report-preserving no-op primary,
surface and ambient kernels; do not charge unused passes to the reference. Validate
material seams, detail/secondary modes, uneven rectangles, render scale, layout
changes, and diagnostic counters on both backends before accepting a change.

`dotnet build src/Puck.SdfVm -c Release` runs DXC IN PLACE in the source tree
(build FAILS without DXC; `/p:DxcCommand=` overrides) — commit the
regenerated `.spv`/`.dxil` with the source change. Editing `sdf-world.hlsli`
or `sdf-vm.hlsli` recompiles `sdf-instance-cull.comp`, `sdf-beam.comp`,
`sdf-world-views.comp`, `sdf-sky.comp`, AND `sdf-cull-args.comp`.

**Stage 1 compiles THREE variants** (`SdfViewsKernelVariant`, selected per
program at `UploadProgram` — walk order Full → Folds → CoreOps): the full-ISA
reference (`sdf-world-views.comp`), the fold-ops middle tier
(`sdf-world-views-folds.comp`, `#define SDF_FOLD_OPS` — folds/scopes/simple
exotic shapes kept, the HEAVY warp/noise family stripped), and the core-ops
strip (`sdf-world-views-core.comp`). The strip macros in sdf-vm.hlsli are a
two-tier ladder: `SDF_STRIP_ALL_EXOTIC` (core only) and `SDF_STRIP_HEAVY`
(core + folds; TwistY/Bend*/LogSphere/CellJitter/Displace/DomainWarp/
NoiseDisplace and the RegularPolygon/Star/Trapezoid/Ellipse shape bodies).
KEEP the two sets IN SYNC with `SdfViewsKernelVariants.FirstHeavyTouch`/
`FirstExoticTouch` — a case stripped under a macro must send `Select` to a
fuller variant. The folds tier is why a world full of pattern folds and
grass scopes no longer pays the full interpreter's ~38% occupancy: measured
~1.5× on views for the shipped world.
`ValidateShaderBytecodeSources` fails the build on bytecode without a
same-stem `.hlsl` (Puck.SdfVm only; the other shader-shipping projects lack
the guard — a known follow-up).

**The MASK-FIRST pass order (the uniform-grid instance-cull arc), now preceded
by the sky pre-pass.** Ten passes per frame: `sdf-frame-upload.comp` (2026-09-03:
copies this frame's host-written viewport rows, dynamic transforms, and frame
instance grid from the ring slot's HOST-VISIBLE buffers into single
DEVICE-LOCAL twins, one uint per thread — `SdfWorldEngine.RecordFrameUpload`,
the `upload` timing pass, ~0.02 ms — because every march kernel used to bind
the host-visible ring buffers directly and fetch across PCIe per sample: the
instance-cull walk per tile, `sdfShadowGather` per lit pixel, `mapCore`'s
`sdfDynamicTransforms` read on every dynamic-instance evaluation. The ring
buffers stay the CPU's write target; the twins are what the beam/cull/views
sets bind. Measured on the RTX 2060 shipped world: mask 5.2 → 4.0 ms, views
−2 to −4 ms at the floor tier) → `sdf-sky.comp` (fills every
non-child viewport's source pixel with `skyColor(cameraRayDirection(...))` —
direct, not indirect, over the full render-dims rect, so a tile the beam
later culls already holds real sky rather than stale device memory; it
shares Stage 1's own bindings array/descriptor set — see the "procedural
sky" sync-pair row) → `sdf-instance-cull.comp` (per-tile instance mask — the
host-built CSR uniform grid from `SdfInstanceGrid`, bin-by-CENTER with the
LOAD-BEARING `footprintPad` = max retained binned radius plus rounding padding.
`SdfInstanceGrid` keeps bounds above eight times the median eligible radius in
the always-tested list before deriving the fine-grid extent; never shrink the
pad while leaving oversized bounds binned. Ordinary dynamic bounds are binned
after frame transforms resolve; the frozen program grid keeps them always-tested.
`InstanceGridScaleLawTests` checks candidate coverage, large bounds, empty/parked
entries and allocating/pooled packing agreement. A disabled grid falls back to the flat
per-instance loop, forced by `SdfProgramBuilder.Build(buildInstanceGrid:
false)` / the demo's `sdf.grid off` verb) → `sdf-beam.comp` (cone march over
the TILE-MASKED field via `mapMasked` — bit-exact per the bound-sizing
contract because a masked-out instance's bound excludes the tile's whole
cone; this is what flattened the O(instances) beam wall: 187.8→6.6 ms @4096,
119→1.0 ms @1024 scattered carves) → `sdf-cull-args` → primary → surface → ambient → views → composite.
The compositor (`sdf-world-composite.comp`) no longer carries an empty-tile
flattening constant or a cull-buffer binding of its own — every source pixel
is real content every frame, so it is a plain copy/upsample with no tile-cull
knowledge.
The instance cull is deliberately NOT fused into the beam (its register
footprint cost the cone march ~12% occupancy, measured), and it uses direct
mask-buffer bit writes, NOT a per-thread accumulation array (512 B/thread
scratch, also measured worse). `sdfInstanceMasks`' D3D12 register is
per-consumer: Stage 1 t13 (default), the beam t3 via
`SDF_INSTANCE_MASKS_REGISTER` before the include. Timing pass labels are
`["upload", "sky", "mask", "beam", "cull-args", "primary", "surface", "ambient", "views", "composite"]`
(`SdfWorldEngine.PassLabels`; `TimingCapacity` 11 covers all marks).
The bench's beam column reports beam+mask so ladders stay comparable. `primary`
measures fine traversal; `views` measures hit shading and secondary queries.
The cull-args reduction closes its own mark. No live gate today — the now-quarantined
`world-grid-cull` Post stage (grid==flat bit-identical via the destructible-slab scene)
plus the existing instanced==flat stages historically checked it.
