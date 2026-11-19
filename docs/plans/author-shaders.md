# Author shaders: the remaining steps

Authors bring their own shading as `shade` programs in `.puck`, lowered to one
canonical op stream that the shading kernels interpret at once and the engine
specializes in the background. The design, its invariant and the owner's
rulings are in [the rendering decisions](../decisions/rendering.md#authored-shading);
the implemented contract (admission, the canonical form, the two tiers, the
publication door, binning, the planner and its checks) is in
[the authored-shading reference](../../.claude/skills/rendering/references/authored-shading.md).
The language, the lowering, the interpreter, specialization by program set,
binning, the work planner and the shared program identity are built. A world
holds each program once in its `shades` table under the program's content
identity, and every use (a palette row, a look case, `render.shade`) names it by
key, so a style on many creations costs one program in the compilation, the
world definition and its projection
([one program, one row](../../.claude/skills/rendering/references/authored-shading.md#one-program-one-row)).
A phase the shading gains later, such as the ink phase
[the ink plan](ink.md) adds, joins the definition the table already holds. This plan holds what remains: the bar
devices' readings, the work bound's calibration and the device-loss quarantine,
the migration of the engine's fixed-function layers into standard programs,
and the two deferred hooks.

The research round's records are beside this plan in
[`author-shaders/`](author-shaders/README.md). They are evidence, not steps;
where one disagrees with the decision or the code, the decision and the code
win.

## The decision matrix

Steps 2 and 4 read this matrix, and step 6 reads it again on the final set.

- **Devices and backends:** the RTX 4070 (Direct3D 12 and Vulkan) runs first;
  the RTX 2060 (both) and the Steam Deck (Vulkan) run once the RTX 4070 meets
  both bars.
- **What decides:** a column passes when it is no slower than the base column
  (the fixed layers) on the same device and stop, beyond the base's own
  run-to-run spread, read by the one recipe in
  [how the bars are read](streaming-and-memory.md#how-the-bars-are-read).
  List building, dispatch and shading are measured separately (with one
  material and with a mix), alongside `host.frames` percentiles, the median of
  three runs and `world.gpu-timing`'s root-view `views` (`views-filter`, every
  `views-shade` bin and `glass-shade`), keeping every slow outlier with its
  cause.
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
- **The rule:** take the cheapest passing column per device (adding
  per-program bins only on a device whose readings require them).
  - *When a specialized column passes:* measure what a player sees when a
    program arrives without a compiled kernel yet (derivation time, temporary
    appearance and install cost). A missing kernel is derived and counted like
    any cache miss, never refused; until installed, the surface uses the
    interpreter where readings show it fits, or neutral shading otherwise.
    Restricting runtime interpretation goes to the owner only when a device
    has a workable specialized path that the interpreter alone breaks, naming
    the runtime authoring use lost.
  - *When base and every specialized column all miss the bar:* shading
    programs are not the cause; fix the non-shader cause named by the
    per-phase readings first.

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

**6. The layers migrate** (GPU leg for the stills; runs after
[colour conversion](rendering.md#colour-conversion--scene-light-to-display-values-once)
so captures are judged once). Shading has one path: the fixed-function layers
are deleted rather than kept beside the programs.

- *What goes.* The lever `world.shade programs|layers` (programs already run by
  default); the C# records `SdfWeathering`, `SdfInset`, `SdfRadialPaint` and
  `SdfFlow`; their palette documents (`PaletteWeatheringDocument`,
  `PaletteInsetDocument`, `PaletteFlowDocument`); their admission rules in
  `SdfMaterialLayers.IsValid`; their rows in `SdfAppearance.Pack`; their HLSL
  (`shade-layers.hlsli`, `shade-weathering.hlsli`, `shade-flow.hlsli`); and the
  rule that a program omitting the surface phase keeps the layers, which the
  standard's `cel` relies on until then. The toon style's shading half has
  already gone: the `cel` program is the one band. `sdfMaterialLoad` is reduced to the base response,
  glass, the program reference, the parameter block, the demand and the lanes.
  The schema is regenerated (`puck schema`), and a slot that still spells
  `weathering` or `flow` is refused by name: one change, no compatibility path.
- *What stays engine code.* The base response the light walk, the probes and
  the bake read; glass (a pass, a group and raster state, with its contact ramp
  `sdfGlassContact` feeding the stamp and its shadow density with the glass
  data); the paints' shape descriptors (a `shape` op's operand); the lanes; the
  flow clock as a stamp input; and the cel outline, which reads neighbour depth
  and normal and so cannot be a per-pixel program, with its style row.
- *The style switch, as built.* The banding is the `cel` program; a palette
  row's `toon` and the world's `render.toon` are the style row beside it (the
  styled mark and the ink), and `render.shade` is the world default's program.
  `toonSelect` cases are each a style row and a program (`celLook`,
  `plainLook`), declared by the document so `world prepare` and the publication
  door compile them, and a swap is an appearance upload. `world.toon off`
  suppresses every style. Cel is the debug world's treatment, chosen by its
  materials
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
  `puck compile --validate` refusing a slot that still spells `weathering` or
  `flow`; `moth.style Plain` then `Cel` swapping the
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

### Image history split and measured shading cuts

Beside the shader migration, four memory and bandwidth cuts target the
largest passes (`views` at ~3.7 ms and 255 registers on RTX 4070 Vulkan;
temporal resolve at ~3 ms, with 392 B/px of history before its split):

1. **Split the temporal image history** (built by the first wave's `history`
   lane). The history surface held 47 words (188 B) a pixel, 40 of them an
   8-frame jittered window, plus 8 B of colour, all double-buffered: 392 B/px
   (~1.45 GB at 2560 by 1440), copying the whole window every frame. Now the
   surface keeps only the six words that follow motion in two copies
   (`HistorySurfaceWords`), and the phase mask and eight-phase window
   (`HistoryWindowWords`) live in one retained buffer the resolve updates in
   place where the pixel's history position equals its own, restarting the
   window for a pixel whose history comes from elsewhere: 228 B/px, about
   0.84 GB for the main view. What remains: read temporal-supersampling
   literature before the window's layout or sample count changes again; report
   how many nested views keep temporal history, since each pays the same bytes
   per pixel; and, if restarting on motion costs quality in presented motion,
   keep only the resolve-time saving and report why.
   - *Check (owed to a GPU leg):* holds through camera motion, newly revealed
     surfaces, view cuts and freshly allocated history.
2. **Planar per-pixel visibility record** (measured). Test storing visibility
   record fields plane by plane (removing strided reads in resolve, filter and
   composite); keep whichever layout measurement favours since `views` reads
   every field.
3. **Decoupled probe lookup per view** (measured). Probe lookup takes roughly
   a third of `views`. Measure it separately, then share the lookup mechanism
   at its own rate per view, reusing results only for receivers at compatible
   position and facing (glass against sky or in front of a wall gets its own
   samples; watch for stair-stepping at visibility edges).
   - *Check:* lit pane over sky, lit pane over a dark wall, and two panes at
     an angle each shade correctly.
4. **Exposure meter on a small fixed grid** (measured). Stop meter cost from
   scaling with resolution; drop on devices where compiling the meter out
   saves under 0.1 ms. Keep the meter (centre-weighted percentiles) distinct
   from screen-glow mean colour, sharing sampling only: a small bright patch
   and an evenly dim picture can have the same mean and very different
   percentiles.

A probe
lookup at a lower rate shows stair-stepping at visibility edges, and we found
no system that shades below full rate without also filtering (not checked).
Nothing here has been measured off the RTX 4070. Not checked: the history's
layout (a research map) and that a screen's glow is a mean of its picture (a
reviewer's reading). Burns and Hunt's "no more than 10 % of frame time" covers
building per-material tile lists only, and their tile-size remark concerns a
separate 16 by 8 lighting kernel, not Puck's 8 by 8 material tiles.

## Unverified premises

- Tier equivalence is claimed only for the program classes the corpus
  (`worlds/debug/shade-tier-checks.puck`) holds. The debug world's own stills
  drift between captures of one tier, so its tiers have not been compared
  directly; that comparison waits for a capture that converges under lockstep.
- The frame cost of the views pass with programs has been read on the RTX 4070
  alone. Nothing about the RTX 2060 or the Deck is inferred from it.

---

[Plans](README.md) · [Decisions](../decisions/rendering.md#authored-shading)
