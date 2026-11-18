# Author shaders: the remaining steps

Authors bring their own shading as `shade` programs in `.puck`, lowered to one
canonical op stream that the shading kernels interpret at once and the engine
specializes in the background. The design, its invariant and the owner's
rulings are in [the rendering decisions](../decisions/rendering.md#authored-shading);
the implemented contract (admission, the canonical form, the two tiers, the
publication door, binning, the planner and its checks) is in
[the authored-shading reference](../../.claude/skills/rendering/references/authored-shading.md).
The language, the lowering, the interpreter, specialization by program set,
binning and the work planner are built. This plan holds what remains: the bar
devices' readings, the work bound's calibration and the device-loss quarantine,
the migration of the engine's fixed-function layers into standard programs,
and the two deferred hooks.

The research round's records are beside this plan in
[`author-shaders/`](author-shaders/README.md). They are evidence, not steps;
where one disagrees with the decision or the code, the decision and the code
win.

## The decision matrix

Steps 2 and 4 read this matrix, and step 6 reads it again on the final set.

- **Devices and backends:** the RTX 2060 (Direct3D 12 and Vulkan), the RTX 4070
  (both), the Steam Deck (Vulkan).
- **What decides:** the frame bar as
  [the rendering decision](../decisions/rendering.md#the-pipeline-foundation)
  states it, the worst frame at the highest quality, read by the one recipe in
  [how the bars are read](streaming-and-memory.md#how-the-bars-are-read). A
  column passes when no frame of any run misses the device's bar on any row.
  `host.frames` percentiles, the median of three runs and `world.gpu-timing`'s
  root-view `views` (with `views-filter`, every `views-shade` bin and
  `glass-shade`) stand beside it as diagnostics that say where a column's time
  goes; they never qualify a column, and a slow outlier is kept with its cause,
  never dropped.
- **Where:** the sweep's worst stops and the matrix's scratch stops.
- **Rows:** zero authored programs (the no-program path); one program on every
  surface; the whole standard set on the debug world; a cap-cost program over a
  full-screen surface (adversarial); mixed cold tiers (half the programs
  specialized, half interpreted, as a cold cache leaves them); a nested view (a
  portal and a session view); and, at step 6, the migrated debug world.
- **Columns per row:** base (the fixed layers, while they exist), tier 1
  unsplit, tier 1 binned, tier 2 unsplit (per set), tier 2 binned.
- **Comparing columns:** two columns that both pass are ranked by their
  diagnostics; a difference inside the base's own run-to-run spread is no
  difference.
- **The rule:** the cheapest passing column is taken. If no column passes on
  some device, the interpreter is restricted to edit time on that device (a
  player build ships every program specialized and a runtime miss is reported,
  not interpreted). That is a quality-versus-bar trade this plan does not make:
  it goes to the owner with the readings.
- **A gap in the rule, and how it is read meanwhile.** No device meets its
  bar before the rendering research's last stage, so every column fails and
  the rule chooses nothing. Until this plan is revised the matrix is read as
  [rendering research decision 13](rendering-research.md#13-shade-through-one-path-and-shrink-the-image-history)
  restates it: a column passes when it is no slower than the base column on
  the same device and stop, beyond the base's own spread. A program that
  arrives with no compiled kernel is derived and counted, never refused. The
  owner's call is raised only where a device has a workable column that the
  interpreter alone breaks.

The RTX 4070's columns are read on both backends: the binned path costs less
than unsplit views at the stops measured, so `world.shaders.bins` stays on by
default there. The RTX 2060 and the Steam Deck have no column read.

## The remaining steps

Each step is a vertical slice with a check that shows it worked. A check that
renders a still, reads `world.gpu-timing` or injects a device loss is GPU work
on a granted leg.

**2. The interpreter on the RTX 2060 and the Steam Deck** (GPU leg). The
matrix's tier 1 unsplit column against base on every row, with driver-native
registers, spills and occupancy limiters beside the frames
([GPU cost](../../.claude/skills/rendering/references/gpu-cost.md)). The
RTX 4070 put the three phases past its register budget, which is why
specialization and binning were built; this step says whether either device
needs more (per-program bins, below) or less. Check: both devices' rows are
read under the recipe, and each device's worst frame is stated with its cause.

**4. Binning's remainder** (GPU leg). The matrix's binned columns on the
RTX 2060 and the Deck; Direct3D 12 occupancy per bin, which no capture has
read; and per-program bins only if a device's program bin misses its bar.
Per-program bins are a variant of the built mechanism, not a new one: the
classifier already resolves a pixel's arm through `sdfShadeBinding`, so they
add a specialized filter, per-program shading kernels and more bins
(`SdfWorldPackage.ShadeBins`). Check: the matrix's rule names one column per
device, and `world.shaders.bins off` still records the unsplit columns.

**5. The work bound calibrated, and the quarantine** (GPU leg for the fault
injection).

- *Calibration.* `SdfShadeProgram.MaxWork` is provisional. It is set from the
  slowest bar device's measured throughput on the adversarial row (a cap-cost
  program over a full-screen surface, interpreted), with the margin stated in
  the commit that sets it. Work is submitted work, never elapsed time: the
  bound makes each dispatch a preemption-sized unit and claims nothing about a
  submission of many dispatches. Under the provisional value the debug world's
  largest program prices a job at a few tens of tiles, so a programmed frame
  records several hundred dispatches on either path; calibration sets that
  count.
- *Quarantine.* Each submission already carries the program identities its
  shade jobs recorded (`SdfWorldResidency.InFlightShadeIdentities`), and a
  device loss marks the unproven ones lost; nothing reads the mark yet. The
  step makes it a quarantine: every identity in a submission a loss took is
  shaded with the standard's neutral default program until the document that
  authors it changes that program's canonical stream. The mark is conservative
  (every program in flight, since a driver's breadcrumb does not name the exact
  operation that failed) and sticky (never re-admitted by time, so one bad
  program cannot hang the machine repeatedly). `world.budget` and
  `world.shaders.status` list the quarantined identities with the loss that
  caused each. The neutral default is used only for a quarantined program,
  never for "still compiling", which the interpreter covers.
- Check: the adversarial program's dispatches are chunked on the unsplit path
  (group rows) and on the binned path (list ranges) in `world.counters gpu`;
  `gpu.faults` injecting a loss mid-frame lists the in-flight identities as
  quarantined in `world.budget` on both paths, the frame renders with the
  neutral default, a `world.reload` of an edited program clears its mark and an
  unedited one keeps it across the reload.

**6. The layers migrate** (GPU leg for the stills). Shading has one path: the
fixed-function layers are deleted rather than kept beside the programs.

- *What goes.* The lever `world.shade programs|layers`; the C# records
  `SdfWeathering`, `SdfInset`, `SdfRadialPaint`, `SdfFlow` and the toon style's
  shading half; their palette documents (`PaletteWeatheringDocument`,
  `PaletteInsetDocument`, `PaletteFlowDocument`); their admission rules in
  `SdfMaterialLayers.IsValid`; their rows in `SdfAppearance.Pack`; and their
  HLSL (`shade-layers.hlsli`, `shade-weathering.hlsli`, `shade-flow.hlsli`,
  `sdf-toon.hlsli`'s banding, the `toon` branches of `sdfLightStage` and
  `sdfLightResponse`). `sdfMaterialLoad` is reduced to the base response,
  glass, the program reference, the parameter block, the demand and the lanes.
  The schema is regenerated (`puck schema`), and a slot that still spells
  `weathering` or `flow` is refused by name: one change, no compatibility path.
- *What stays engine code.* The base response the light walk, the probes and
  the bake read; glass (a pass, a group and raster state, with its contact ramp
  `sdfGlassContact` feeding the stamp and its shadow density with the glass
  data); the paints' shape descriptors (a `shape` op's operand); the lanes; the
  flow clock as a stamp input; and the cel outline, which reads neighbour depth
  and normal and so cannot be a per-pixel program, with its style row.
- *The style switch's successor.* The world default moves from `render.toon`
  and `world.toon` to `render.shade` and `world.shade default`, which are
  built. A palette row's `toon` becomes its `shade` (`cel(...)`, or `plain()`
  to stay continuous under a cel default). `toonSelect`
  (`PaletteToonSelectDocument`), which binds a row's style to a state cell and
  is how `moth.style Cel|Plain` swaps the Moth live with no rebake (the owner's
  ruling that she supports both), is replaced by a `shadeSelect` of the same
  shape: a `state` cell and `cases`, each case a `shade` reference with the
  outline's style row beside it, resolved where `WorldSurfaceMaterials`
  resolves the toon case today. Every case's program is declared by the
  document, so it is in the set `world prepare` and the publication door
  compile, and a swap is an appearance upload: no rebake and no compile. The
  migration keeps the capability and turns nothing on: cel shading is parked
  ([the decision](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).
- *Terrain and weather are programs.* The terrain pass's slope, altitude and
  curvature materials and a weather region's coatings are `shade` templates in
  the standard package over the stamp (`surface.normal`, `surface.position`,
  `surface.curvature`, `lattice`, the lanes), not a layer table
  ([terrain pass](terrain-pass.md#slope-altitude-and-curvature-materials)).
  An input one of them needs and the stamp lacks is an op or a stamp member
  added to `SdfShadeOps`, with its declared cost.
- Check: the matrix's final row on every device; the debug world's sweep tour
  stills on both backends judged against each place's reference, with the
  pre-migration stills beside them as evidence of what moved;
  `puck compile --validate` refusing a slot that still spells `weathering`,
  `flow`, `toon` or `toonSelect`; `moth.style Plain` then `Cel` swapping the
  Moth with `world.counters sdf.shade` showing no compile and the bake counters
  no bake.

**After step 6, each its own step:**

- *Probe hits through the surface phase.* The probes and the exposure meter see
  a material's base response by contract, so a colour or an emission a program
  computes does not bounce (snow over rock bounces rock). A program is a pure
  function of the stamp, so a CPU or relight-kernel interpreter of the same
  table could shade a hit. It is taken when a place shows the difference.
- *Image sampling* (the owner's ruling: after tier 2). A `sample` op over
  authored image data, with its taps, filter and level-of-detail bounds
  declared before admission so its cost is charged in `world.budget` like every
  other op.
- *The screen effect hook* (the owner's ruling: deferred). A program over the
  composite's output image, through the post-process package mechanism.

## Unverified premises

- Tier equivalence is claimed only for the program classes the corpus
  (`worlds/debug/shade-tier-checks.puck`) holds. The debug world's own stills
  drift between captures of one tier, so its tiers have not been compared
  directly; that comparison waits for a capture that converges under lockstep.
- The frame cost of the views pass with programs has been read on the RTX 4070
  alone. Nothing about the RTX 2060 or the Deck is inferred from it.

---

[Plans](README.md) · [Decisions](../decisions/rendering.md#authored-shading)
