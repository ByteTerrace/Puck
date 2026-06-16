# Avatar SDF VM reference

The avatar VM is a small instruction-stream interpreter that evaluates a signed
distance field per march sample. The GPU core lives in
`src/Puck.Avatars/Assets/Shaders/Characters/avatar-vm.glsl`; the matching C#
contracts (opcodes, shapes, blend ops, groups) live in `src/Puck/Terminal/`,
and `CharacterSdfDefinitionLoader` validates every program at load time.
Programs are authored as JSON (`Puck.avatar-sdf-vm.v0`, see
`creature-*.json` next to the shader) and uploaded verbatim as the instruction
buffer.

## Execution model

The VM runs the whole instruction list once per evaluation point. Its state is:

- `localP` — the evaluation point, initially the avatar-space query point.
- `distanceScale` — a conservative distance correction, initially 1.
- `res` — the running `(distance, materialId)` blend result.

Transform/domain opcodes rewrite `localP` and apply to every **subsequent**
`ShapeBlend` until the point is rebased by `ResetPoint` or `SetBoneSpace`.
`ShapeBlend` evaluates a primitive at the current `localP` and folds it into
`res` with a CSG blend op. `Onion`/`Round` post-process the running result
itself. There is no flow control; the stream executes top to bottom.

A second pass, `mapGradient`, replays the same stream once per hit pixel while
accumulating the exact Jacobian of the local transform, yielding analytic
surface normals — every opcode therefore has a defined gradient, including the
curvature terms of the bends/twist.

## Instruction encoding

Each instruction is three 16-byte lanes:

| Field    | Type    | Contents                                              |
|----------|---------|-------------------------------------------------------|
| `header` | uvec4   | `x` = opcode; `y`/`z`/`w` = opcode-specific arguments |
| `data0`  | vec4    | primary parameters                                    |
| `data1`  | vec4    | secondary parameters                                  |

An optional `"name"` string is authoring-only metadata.

## Opcodes

| # | Opcode | Arguments | Effect |
|---|--------|-----------|--------|
| 0 | `ResetPoint` | — | Rebase `localP` to the avatar-space point; reset `distanceScale`. |
| 1 | `Translate` | `data0.xyz` = offset | `localP -= offset`. |
| 2 | `RotateInverseQuaternion` | `data0` = quaternion | Rotate `localP` by the inverse quaternion (i.e. rotates subsequent shapes by the quaternion). |
| 3 | `ScaleSpace` | `data0.xyz` = scale (non-zero) | Divide `localP` by the scale; `distanceScale` multiplies by `min(scale)` so reported distances stay conservative under non-uniform scale. |
| 4 | `TwistY` | `data0.x` = rad/unit | Twist the XZ plane around Y, angle proportional to `localP.y`. |
| 5 | `BendX` | `data0.x` = curvature | Bend the XY plane, driven by `localP.x`. |
| 6 | `BendY` | `data0.x` = curvature | Bend the XY plane, driven by `localP.y`. |
| 7 | `BendZ` | `data0.x` = curvature | Bend the YZ plane, driven by `localP.y`. |
| 8 | `Elongate` | `data0.xyz` = extents | Stretch subsequent shapes by clamping `localP` to a box. |
| 9 | `ShapeBlend` | see below | Evaluate a primitive and blend it into the running result. |
| 10 | `SetBoneSpace` | `header.y` = bone index | Rebase `localP` into the bone's local space (a bone-rooted `ResetPoint`); `distanceScale` becomes the bone's conservative minimum scale. |
| 11 | `Repeat` | `data0.xyz` = spacing (non-zero) | Infinite domain repetition. |
| 12 | `RepeatLimited` | `data0.xyz` = spacing; `data1.xyz` = per-axis copy limits (≥ 0) | Repetition clamped to `±limit` copies per axis. |
| 13 | `SymmetryX` | — | `localP.x = abs(localP.x)` (mirror across the YZ plane). |
| 14 | `SymmetryY` | — | Mirror across the XZ plane. |
| 15 | `SymmetryZ` | — | Mirror across the XY plane. |
| 16 | `Onion` | `data0.x` = thickness (> 0) | Hollow the running result into a shell: `d = abs(d) - t`. |
| 17 | `Round` | `data0.x` = radius | Inflate the running result: `d -= r`. |
| 18 | `WallpaperFold` | see below | Fold an in-plane coordinate pair onto the fundamental cell of a wallpaper symmetry group. Optionally recolors per cell (`header.w`, see below). |
| 19 | `SurfaceChart` | see below | Declare the chain's surface chart: subsequent shape wins carry band-limited procedural patterns (texturing primitive — never affects distances). |

Repetition/symmetry caveat (standard SDF rule): repeated or folded content must
stay clear of cell boundaries unless a mirror of the group protects that edge,
or distances near the seam become non-conservative.

### ShapeBlend (opcode 9)

- `header.y` — shape type (table below)
- `header.z` — blend op (table below)
- `header.w` — material id
- `data0` — shape dimensions
- `data1.x` — smooth-blend radius (used by the `Smooth*` ops; finite, ≥ 0)
- `data1.y+` — extra shape parameters where noted

Each shape instruction also carries a host-maintained posed AABB
(`SdfShapeBound`, rewritten every animation tick) that lets the march skip
provably non-contributing shapes; the gating is compensated so the rendered
surface is bit-identical to the ungated field.

#### Shape types (`header.y`)

| # | Shape | `data0` layout | Notes |
|---|-------|----------------|-------|
| 0 | `Box` | `(halfX, halfY, halfZ, cornerRadius)` | Outer extents; radius carved inward, ≤ smallest extent. |
| 1 | `Segment` | `(axis xyz, radius)` | Capsule from the origin along `axis`. |
| 2 | `Sphere` | `(radius, -, -, -)` | |
| 3 | `Torus` | `(majorR, minorR, -, -)` | In the XZ plane. |
| 4 | `Link` | `(halfLength, majorR, minorR, -)` | Elongated torus (chain link). |
| 5 | `Plane` | `(normal xyz, offset)` | Half-space; normal must be non-zero. |
| 6 | `Cylinder` | `(radius, halfHeight, -, -)` | Y-axis aligned. |
| 7 | `Cone` | `(sinAngle, cosAngle, height, -)` | Apex at the origin, opens along −Y. |
| 8 | `CutHollowSphere` | `(radius, cutHeight, thickness, -)` | Requires `|h| < r`. |
| 9 | `VesicaSegment` | `(axis xyz, width)` | Lens profile; `0 < w < length(axis) / 2`. |
| 10 | `Ellipsoid` | `(rx, ry, rz, -)` | Approximate (bound-exact); gating compensates. |
| 11 | `RoundCone` | `(axis xyz, r1)`; `data1.y` = r2 | Requires `(r1 − r2)² < dot(axis, axis)`. |
| 12 | `ArcSegment` | `(curvature, length, tubeRadius, -)` | Sweep `curvature * length ≤ π`. |
| 13 | `MsdfGlyph` | authored: `(glyphHeight, halfDepth, unicode, -)` | Extruded glyph slab ("tattoo"/engraving). The packer rewrites it against the live font atlas; codepoint must be printable (≥ 33). |
| 14 | `ScreenSlab` | `(halfW, halfH, halfD, cornerRadius)` | Rounded box whose +Z face is a live display fed by the previous frame's per-view feed image. |

#### Blend ops (`header.z`)

| # | Op | Semantics |
|---|----|-----------|
| 0 | `Union` | `min(a, b)` |
| 1 | `SmoothUnion` | polynomial smooth min, radius `data1.x` |
| 2 | `Subtraction` | carve the shape out of the running result |
| 3 | `Intersection` | `max(a, b)` |
| 4 | `Xor` | symmetric difference |
| 5 | `SmoothIntersection` | smooth variant, radius `data1.x` |
| 6 | `SmoothSubtraction` | smooth variant, radius `data1.x` |

The material id wins whenever the incoming shape is the closer (or, for
carve/intersect ops, the dominating) surface.

### WallpaperFold (opcode 18)

Folds two of the three local axes onto the fundamental cell of one of the 17
plane symmetry groups; the third axis is untouched. Every branch is an
isometry, so distances are preserved and `distanceScale` never changes.

- `header.y` — wallpaper group (IUC order): `P1 P2 Pm Pg Cm Pmm Pmg Pgg Cmm P4
  P4m P4g P3 P3m1 P31m P6 P6m` (0–16)
- `header.z` — fold plane: `0` = XZ, `1` = XY, `2` = YZ
- `data0.xy` — cell pitch per axis (non-zero); `data0.z` is reserved skew and
  must be 0
- `data1.xy` — lattice copy limits per axis (≥ 0, `RepeatLimited` semantics)
- `data1.z` — symmetry-LOD distance threshold (≥ 0; `0` = always full detail).
  Past the threshold the lattice is kept but in-cell folds are skipped (upright
  copies — cheaper and shimmer-free at range)
- `header.w` — parity-material stride (`0` = purely geometric). When set, every
  subsequent shape win in the chain strides its material id by `cellKey ×
  stride`, where the cell key is the checkerboard parity (square lattice, keys
  0–1) or the hex 3-coloring (hex lattice, keys 0–2 — the same coloring that
  keys the P3/P6 turn count, so colors and rotations stay in sync). Author the
  strided rows consecutively (e.g. id 5 + stride 1 → cells use 5, 6, 7). The
  key derives from the lattice, so colors survive the symmetry LOD. Distances
  never depend on it — only the gradient/shading pass reads it.

Constraints: the quarter-turn groups (`P4`, `P4m`, `P4g`) and all hex-lattice
groups (`P3` and up) require square cells and equal limits. The hex groups live
on the equilateral triangular lattice with pitch `data0.x`. `P1` is
bit-identical to a two-axis `RepeatLimited`; `Pm` with zero limits reduces
exactly to the `Symmetry*` opcodes.

Debug mode 11 sweeps all 17 groups (one per frame, `frame % 17`) — see
`docs/debug-visualization.md`.

### SurfaceChart (opcode 19)

The texturing primitive: declares how the winning shape's leaf-local hit
position becomes 2D pattern coordinates, and which procedural pattern modulates
the shape's albedo there. Chart state is chain-scoped (reset by `ResetPoint` /
`SetBoneSpace`, like the warp chain) and applies to every later shape win in
the chain. The march never evaluates any of it — patterns cost one evaluation
at the hit pixel, in the shading pass — and a chart never affects distances.

- `header.y` — chart kind: `0` planar (local XY), `1` cylindrical (around local
  Y; `data1.x` = angular repeats ≥ 1), `2` spherical (`data1.x` repeats),
  `3` triplanar (three local planes blended by the leaf-local normal — the
  SDF-native chart for blobby smooth-union bodies; 3× pattern cost)
- `header.z` — pattern: `0` checker, `1` stripes (`data0.y` = duty),
  `2` jittered spots (`data0.y` = radius in cell units), `3` marble
  (`data0.y` = fbm warp strength, `data0.w` = max octaves ≤ 8), `4` atlas
  (sampled artwork; see below)
- `header.w` — secondary material id: the pattern mask blends the shape's base
  color toward this material's color (unused by pattern 4 — art carries its own
  color, blended by its alpha)
- `data0.x` — frequency (pattern cells per local unit, > 0)
- `data0.z` — blend strength ∈ [0, 1]

**Atlas pattern (4)** samples the surface atlas: a fixed 16×16 grid of 128×128
RGBA slices in one 2048² image (slice 0 = loud missing-art fallback; PNGs from
`TerminalVulkanCompositionOptions.SurfaceAtlasDirectory` fill slices 1..N in
ordinal name order; every other slice is a deterministic placeholder kit). One
slice maps over each chart cell. `data0.y`: `0` = repeat per cell, `1` = single
decal centered on the chart origin (author transparent borders). `data1.w`:
`0` = the instruction-baked slice in `data1.y` (whole-team art — instances of a
species share instruction streams), `1` = the INSTANCE's slice
(`AvatarInstanceData.reserved.x`, host-defaulted to the visible-instance index —
unique art per avatar with zero extra authoring). The footprint converts to a
real mip level (CPU-built chain, clamped at the slice-bleed limit) and the uv
insets by the sampled level's half texel, so art mips cleanly and never bleeds
into a neighbor slice. Triplanar charts resolve to their dominant plane for the
tap (an image ghost-blends where procedural masks don't).

Every pattern is band-limited by the hit's pixel footprint (the pixel cone
spread across distance and grazing angles): smoothstep edges widen with it, the
checker box-filters analytically toward its mean, and marble clamps its fbm
octave count — no shimmer at range, deterministic for capture hashes. Since the
attributes stash on the *winning* shape, charts are exact for hard unions and
approximate under smooth blends (same convention as the screen slab's UV).

Debug mode 12 renders charted surfaces as a checker + footprint heatmap — see
`docs/debug-visualization.md`.

Every pure-math shape primitive is additionally pinned by the shape-gallery self-test
(debug mode 14): one validate-gate frame per culling tier traces all of them through
the production `evaluateSdfShape`/`evaluateSdfShapeGradient` at three loader-admissible
parameter sets each, with in-shader analytic-vs-numeric gradient checks — see
`docs/debug-visualization.md` (KEEP the gallery's parameter table IN SYNC with the
loader contracts when shape validation rules change).

## Shape inspector (debug mode 13, `debug.shape`)

Per-instruction analysis of a creature's stream — the whole-creature debug views can't
say which instruction contributed what, so authoring a creature gets a console surface:

- `debug.shape list [asset]` — CPU-side dump of every packed stream: global slot,
  authoring `name`, opcode, shape type, blend op, material id, and whether the loader
  granted the shape a culling bound (`gated`/`ungated`). No shader involvement.
- `debug.shape isolate <slot>` — the debug kernels skip every other `ShapeBlend`, and
  the survivor renders normal-shaded (raw primitive, no material/lighting). Species
  whose stream excludes the slot render empty.
- `debug.shape highlight <slot>` — normal render with a hot-pink tint exactly where the
  targeted instruction *wins* the running blend (the `shapeWon` machinery, so
  same-material shapes never steal attribution); everything else dims.
- `debug.shape bounds <slot>` — composites the slot's posed culling AABB
  (`shapeBounds`, cyan faces / yellow edges) against the marched surface: the gap
  between box and surface is the per-shape gate's slack, and a surface escaping its
  box is directly visible.
- `debug.shape off` / `debug.shape status`.

The slot is the GLOBAL packed-instruction index (`list` prints it), which uniquely
selects (asset, shape) — instances share instruction ranges, so every instance of the
species shows the inspection. The slot rides bits 9–16 of the debug word and the
sub-mode bits 17–18; like every debug view, the machinery lives behind
`Puck_DEBUG_VIZ`, so shipping kernels are untouched. The mode is parameterized
(needs a target slot) and therefore stays OUT of the validate sweeps, like rain.

The same command also renders a **single bare primitive**, decoupled from any creature
(debug mode 15, `shape-solo`):

- `debug.shape solo <shape>` — one primitive fullscreen with sensible default params
  (`box`, `sphere`, `torus`, `cone`, `roundcone`, …; `MsdfGlyph` is unavailable solo —
  it needs a glyph-atlas slice).
- `debug.shape set <p0> [p1] [p2] [p3]` — tune the four `data0.xyzw` params live (trailing
  params keep their current value, so you can nudge one at a time).

It traces the chosen primitive through the same production `evaluateSdfShape`/
`evaluateSdfShapeGradient` as the gallery, normal-shaded, NaN/Inf params → magenta. The
render early-returns before the world march, which frees the push-constant lanes the four
params ride (`world.xyz` + `misc.w`); the round cone derives its second radius from the
first since `data0` is fully spent. Also a manual tool, out of the validate sweeps.

## Limits

| Limit | Value |
|-------|-------|
| Instructions per program | 192 (`MAX_AVATAR_SDF_INSTRUCTIONS`) |
| `ShapeBlend` instructions per program | 64 (≥ 1 required) |
| Bones per rig | 32 |
| Avatar instances | 64 |
| Rest-pose bound | radius 0.48 + 0.12 animation margin (validated at load; `SDF_BOUNDS_RADIUS` 0.60) |

The loader rejects any program that violates these, an out-of-range opcode /
shape / blend op / group / bone index / material id, or the per-shape parameter
contracts noted above — error messages name the offending instruction index.
