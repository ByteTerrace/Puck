# Open plan items

The work that is ready to schedule, as work packets: each packet is one lane's
worth of work with one cause or one area, its owner role, the severity of what
it holds, and the next action. The [Next](#next) list ranks the packets the next
integration batch takes; [owner decisions](#owner-decisions) wait on the owner;
the phase-scale work of each programme lives in [its plan](#plans).

How the file stays usable:

- **Filing a finding places it.** A review finding, a lane's leftover or a
  walkthrough flaw goes into the packet that shares its cause or area, as a
  sub-point that keeps its file and line, its failing case and its severity
  (`[high]`, `[medium]`, `[low]`); with no such packet, open a new one with an
  owner role and a next action. A finding never sits in a list of its own.
- **Each integration batch takes the top packets** of [Next](#next), and the lead
  re-ranks the list at that batch by payoff, with anything high first.
- **A finished packet is deleted, not ticked.** A closed sub-point is deleted
  too. Before deleting, move the durable fact it carries (a cause, a measured
  number, a hazard) to its owning home (AGENTS.md rule 9); git keeps the rest.
- **Phase-scale work goes to its plan.** A package or step of a programme is the
  plan's own, in that plan's open items; a packet here points at it.

## Next

Ranked by payoff at the latest batch. Lanes in flight are named; the lead
resolves their edits to this file at merge.

1. [Lighting and GI](#lighting-and-gi): the probe-depth review's high finding
   (depth visibility leaks at every silhouette) with the zero-weight gather
   fallback in the same change, then the trace cost and the wrapped-diffuse sun
   leak. In flight: probe-depth-fix.
2. [Dynamic body scale and the mesh bake](#dynamic-body-scale-and-the-mesh-bake):
   the walkthrough's other high; `SdfMeshInstance.Matrix` already scales at
   draw, so the fix is in the dynamic bake path's identity and detail.
3. [Edit to running, hot reload and boot](#edit-to-running-hot-reload-and-boot):
   a hot-reload code revision as one invalidation contract (emitter revisions
   and the static field), then the persisted caches keyed by the modules as
   built; a product of edited code under the unedited identity is the hazard.
4. [MCP and the operator attachment](#mcp-and-the-operator-attachment): the
   cached wait refusal (`OperatorMcpFront.Record` caches any non-pending wait
   answer), the per-swap state load, then the schema switch.
5. [Test runner and replay](#test-runner-and-replay): the batch loop's one-line
   break (`WorldTestBatch.cs:89`), then boot reuse across differing scenarios
   (in flight: boot-reuse) and `--reproduce`, which still costs as much again as
   the ordinary run.
6. [Collision versus drawn mesh](#collision-versus-drawn-mesh): re-run the mesh
   comparisons on the corrected rounded-Box diagnostic, then the surviving red
   findings by cluster; the reds fail every sweep.
7. [Debug tools](#debug-tools): a pick that addresses the presented view, lists
   it in `panes` and answers under pause, then the diagnostics' windowed checks
   and the developer exhibits.
8. [Sweep and visual checks](#sweep-and-visual-checks): fix the prelude (the
   chess toast, the refused `world.wait 300`), reproduce the companion overlaps,
   then one paired windowed tour when no lane holds the GPU.
9. [Temporal shimmer and cadence flicker](#temporal-shimmer-and-cadence-flicker):
   isolate the temporal-off cadence blink first, then why history never
   resolves the jitter.
10. [Simulation tick off the frame thread](#simulation-tick-off-the-frame-thread):
    the catch-up burst bounded by wall time and the wake margin derived from the
    contact skin, both confirmed in the code and cheap; the thread itself is a
    design consensus the owner has yet to queue.

## Owner decisions

Each waits on the owner: what the engine should do, how it looks and feels, and the owner-only actions. An engineering call is the lead's and sits in its packet as a "Decide" line, made when the packet is taken.

- What the chase camera does with its back to the stair wall: swing the boom,
  rise over the wall, or switch to first person there
  ([motion](#dual-stick-play-and-the-moths-motion)).
- What the back of a door's glass shows: nothing, the fallback colour, or a
  mirror of the front ([rendering plan](rendering.md#open-items)).
- Look calls in the rendering plan: whether the far cascade caches its static
  casters at a coarser snap; whether portal and session views render on the
  frame after a tick (each picture a frame late) to split the tick frame's cost;
  whether the 30 and 15 Hz update tiers become a continuous rate.
- Whether the engine ships or requires the GameInput redistributable
  ([input](#input-channels)).
- The test keep-list, and whether any of the deleted guards over non-code files
  return ([coverage](#coverage-and-test-suites)).
- Whether proof of possession is a product requirement for Entra bearer tokens
  and for initial binding issuance ([security](#security-review)).
- Mesh shipping: nothing is built until a presenting client downloads packages,
  and then on a self-hosted GPU runner (the owner's deferral;
  [rendering plan](rendering.md#open-items)).
- Report the .NET 11 JIT GC-hole to dotnet/runtime, with the BCL-only
  reproduction (outside the repository; [delivery](#delivery-launcher-and-ci)).
- The visual issues the owner found playing with the camera and controller, to
  discuss once the current lanes settle.
- Feel and look sittings: the Moth's walk, run, turn and lean values; the faerie;
  the board room; the lift panel's look and reach boxes; the gallery inspection
  lever's values; first-person feel in the lift and stairwell; the glass
  exhibit on both backends; how indoor rooms read against daylight once the
  image meter drives exposure ([lighting](#lighting-and-gi)).
- Whether a cube projection is wanted for screens on shapes, beside the sphere
  the owner asked for ([curved screens](#screens-on-curved-and-general-sdf-surfaces)).
- The ideas [awaiting review](#awaiting-the-owners-review).

## Work packets

### Sweep and visual checks

Owner: sweep follow-up, with the lead scheduling the GPU slot · medium ·
Next: fix the prelude (dismiss the chess toast, wait in shorter steps),
reproduce the companion overlaps, then one paired windowed tour (about 18
minutes of GPU) when no lane holds the GPU, compared stop by stop.

- Finish sweep-honest's paired motion qualification. Its first final tour
  measures all 23 windowed signals and captures all 64 stops cleanly, and 62
  unchanged stops' motion records match an earlier fresh tour exactly; the
  second tour and its retry each expired after 1,200 s of GPU acquisition
  without starting a World and exited 1 with the windowed leg unmeasured.
  Rendering and runner integrations landed after the first tour, so the
  current-integration windowed reading and the all-64 motion comparison are
  unconfirmed. The sweep-honest hand-back holds the commands and artifacts.
- The windowed tour's pause: holds, a boot pause and a capture-tick pause are
  implemented so that ambient bodies, the lift, the companion and the spin
  exhibit land on the same ticks every run (`SweepRun.cs`); paired motion
  captures have not shown it. A cold Direct3D 12 run stayed at tick 0 until its
  nine-minute deadline with the render stack waiting on an upload fence; that
  cold stall's cause is unconfirmed.
- The prelude's `world.state.cell.set chessDemo enabled 0` leaves its HUD toast
  in every still although `world.row.set hud.defaults enabled false` ran before
  it, and on a loaded machine its `world.wait 300` is refused outright (the
  attachment's answer falls due before the release tick), so every later still
  carries that refusal; dismiss the toast and wait in shorter steps
  (`worlds/debug/sweep.json`).
- [medium] The full windowed sweep records companion overlaps between bodies 0
  and 42: the first recovered tour counted three events, at ticks 861 to 3997,
  after a clean initial watchdog reading, and `watchdog.after-tour` correctly
  turns red. Find which walking or pose transitions let the companion touch her
  and fix its separation (`worlds/debug/addons/companion.wat`); no watchdog
  exception or counter reset. Evidence:
  `sweep-honest-recovered-tour-a/windowed/watchdog-log.after-tour.txt`.
  (owner: companion/contact follow-up)
- Measure boot-once's windowed workload through the integration sweep: its
  qualification was headless, and the original state and physics timings
  regressed, so keep per-file timing and boot and work counts when reading the
  full sweep.

### Lighting and GI

Owner: next lighting batch (rendering, principal-grade review) · one high, six
medium, lows · Next: the high silhouette leak with the zero-weight gather
fallback in the same change, in flight in probe-depth-fix.
The [mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
holds the fixed poses and the approaches already rejected.

- [medium] `SdfShadowVisibilityDeviceLawTests.DirectXPreserves…` and `VulkanPreserves…` fail on `lead/catch-up` with 59530 against an expected 59787. The likely cause is the bright-lines change (an unqueried shadow slot now `SDF_SHADOW_UNQUERIED` 255 and the views filter averaging only queried pixels, commits `e27173ef1` and `1c3509778`): decide whether the law's expectation should follow the corrected visibility (re-derive it from the law's own scene, never by copying the new number) or the change broke a case the law pins. A GPU leg. (from: c7-findings lane)

- Owner walkthrough, depth visibility (check each pose on the probe-depth-fix build before closing): (1) a jagged bright zig-zag in a room corner and a saw-tooth band along the ceiling, present in `indirect` (`body.pose 66.24 4.57 14.73 329`, camera eye (64.061, 5.331, 18.308) target (66.241, 5.57, 14.726)); (2) hard-edged diamond and star-shaped dark patches on the floor round the NOISE and ERODE plinths, black in `shadow` (sun occluded) and present in `indirect`: whole octahedral depth texels switching at once (`body.pose 58.17 4.57 30.87 180`, eye (58.16, 8.403, 29.767) target (58.168, 5.57, 30.868)). Captures w01-*, w02-* in `.claude/scratch/walkthrough/` (the lead's machine).

- Decide (the lead's call): Exposure: a GPU luminance meter (log-average of the
  view's HDR output with a highlight clamp) drives auto-exposure, the probe
  meter kept for local adaptation, because the probe meter cannot see dynamic
  meshes or anchored lights (the lift sits at the EV 7 floor and the Moth basis
  compensates by 2.5 stops). First a calibration test: an authored 18 % grey
  card under a known manual EV reads about 118/255. Settle the colour domain
  with it: `place.comp.hlsl:20` states that the stylized shading is already
  display-referred and no gamma encode follows the filmic curve, so whether the
  back buffer owes an output transfer is a contract to establish, not a defect
  found. Only how the result looks is the owner's.
- Decide (the lead's call): the coat layer's F0 semantic, chosen consistently
  with the material model; and whether baked static copies occupy SDF instances
  at all, the copies represented independently of the bake program's limits.
- Decide (the lead's call): a missed depth ray stores a bounded distance (about
  1.5 spacing diagonals) or a flag, never the reach; the sky comes from the
  radiance rays; an all-zero gather is explicit, never full light. The
  filtering rule against thin occluders, corners and FP16 precision wants its
  own short consensus before it is final.

- Probe depth visibility (from the probe-depth review):
  - [high] Depth visibility leaks at every silhouette: a ray that misses stores
    `min(reach, DepthMaximum = 128 m)`, about forty times the farthest receiver
    a probe serves, and the `distance <= mean` early-out returns 1 before
    Chebyshev runs, so any texel whose bilinear footprint touches a miss admits
    every receiver behind the occluder (one missed tap at 2 % weight gives a
    mean near 3.5 m). Failing case: the teal-room roof's eave
    (`src/Puck.SdfVm/SdfMeshProbeDepth.cs:20,45,84`,
    `sdf-mesh-irradiance.hlsli:82`). Store misses at about 1.5 times the
    spacing diagonal or flag them, and take the sky from rays that still run to
    full reach.
  - [medium] Each probe's first sweep casts 1,024 closest-hit depth rays to the
    full lighting reach, eight times the 128 radiance rays, because the sky cube
    is now measured from them; depth needs about one ray per texel bounded to
    the gather radius, and the radiance rays already compute a sky result that
    is overwritten (`SdfMeshProbeDepth.cs:30-32`, `SdfMeshIrradiance.cs:917-919`).
    The likely cause of the trace-time rise below.
  - [medium] Zero-weight gathers became common (Chebyshev returns exactly 0 at
    zero variance with no floor, the facing term lost its `+0.2`, tiny weights
    flush to zero on the GPU), and a receiver whose eight weights are all 0 falls
    back to `sdfMeshLightVisibility` 1.0, fully lit by every traced static light:
    a point light in the next room shines through a flat wall
    (`sdf-mesh-irradiance.hlsli:142,202-204`, `SdfMeshIrradiance.cs:1250`).
  - [medium] Depth visibility raised the GI trace time: Direct3D 12 from 3.9 s to
    5.2-7.6 s and Vulkan from 4.6 s to 16.8 s (both on a loaded machine), with a
    38.67 MiB probe table per residency. Re-measure on a quiet machine; if
    Vulkan stays about three times Direct3D 12, find why
    (the probe-depth hand-back in the lead checkout's scratch).
  - [medium] The probe-depth lane's Vulkan captures (in the lead checkout's scratch)
    still show small dark marks at mid-height on the stairwell's east wall
    (`own-vulkan-stairwell-lit.png`, the black squares the owner reported), 2
    m-scale blotches on the boardroom floor and a bright vertical streak on its
    right partition (`own-vulkan-boardroom-indirect.png`). The sphere reads clean.
  - [low] The sky share is measured over the 1,024 depth rays while bounce
    radiance comes from the 128 radiance rays, so a small window can count twice
    (`SdfMeshProbeDepth.cs:42-43`); FP16 variance cancellation at 3-5 m exceeds
    `NormalBias` and flips flat-surface visibility per texel (`:84-90`, HLSL
    `:82-87`); the record grew from 72 to 275 words, about 193 MiB reserved per
    residency at High whatever its tier, and each publish compares up to 40 MB
    against the shadow copy on the render thread
    (`SdfWorldTables.MeshIrradiance.cs:11`, `SdfMeshIrradiance.cs:105,328`); the
    rendering `SKILL.md` still describes the deleted `Filter`, lattice-ray
    planes, `sdfMeshProbePlaneVisibility`, `sdfMeshProbeSeparate` and
    `DepthFade`; placement scores 32 candidates with 32 full-reach rays each
    before the depth trace (`SdfMeshIrradiance.cs:945-963`).
- [medium] Wrapped diffuse materials leak sunlight indoors: the surface pass
  skips the shadow-cascade query where n·l <= 0, but a material with wrap (the
  Moth's skin, wrap 0.35) still takes diffuse sunlight there (about 18.5 % at
  n·l = -0.1), and an all-unqueried filter window reads fully visible, so an
  indoor wrapped surface past the terminator is lit by a sun the roof occludes
  (`src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-surface-stage.hlsli:305`).
  Query where the wrapped term is positive, without self-shadowing the wrap away
  outdoors. (from: bright-lines review)
- [medium] The stairwell's flight B enclosure reads black on its side walls in
  `indirect` while its treads and landings are lit
  (`gather-stairwell-flight-b-0-indirect`). Coverage is not the cause:
  `world.probe 12.3 3 37 1 0 0` (the divider's east face) answers with weight
  0.81 from the valid probe of cell (6, 1, 18), whose own cubes read
  `luminance=4.3` beside 40 to 60 across the divider and hundreds in the hall,
  so the enclosure measures almost no bounced light at the medium tier's two
  bounces (its pendant `state.hallLamps.stairs` is off in the sweep). More
  bounces or a finer lattice there, judged with `world.probe`'s cube readings.
- GI visibility remainder: settlement is configured sweeps, not
  history-independent global convergence (a full tour and direct teleports have
  different window and bounce histories; repeating the teleport order on
  Direct3D 12 removes the apparent stairwell backend difference). Also open: a
  general refusal of lighting-finding captures before every bake and the
  irradiance have settled; grazing marks at the teal vertical corner, the Moth
  backdrop and the boardroom jamb feet; bright bands in the painting wing and
  camera-bay roof; curved bands on the level-3 stairwell wall (finite angular
  sampling is a hypothesis, not an established cause for every pixel); the
  garden portal's dark horizontal strip in the probe-depth sweep; the REVOLVED
  floor's diagonal patches and black rectangle, which reproduce in `indirect`
  while that floor reads shadowed in the shadow view; and the wing's
  north-east corner remnants ([rendering plan](rendering.md#open-items)).
- The SPIN exhibit (`gallery.go motion-spin`): a dotted bright seam at the level 3
  ceiling and wall junction reproduces in beauty but not in `indirect`,
  `normals` or `shadow`; behind it at level 2 (chase view) a dotted bright line
  runs along the wall's foot and the window blinds' left edges draw bright
  vertical lines, on both backends. Keeping unqueried shadow slots out of the
  filter left them unchanged, so the cause is elsewhere; geometry and direct
  light are next.
- The black lift and hanging-sign interiors the owner reported: settled
  medium-GI checks with every gallery light on read both interiors without the
  camera-meter fallback, and the meter now traces at a camera with no visible
  lattice probe, but the earlier blackout's cause is not established; reproduce
  it with the original camera and lighting state.
- The plinths' box edges read about 45 degrees of shading error in the GPU bake
  while `puck mesh audit --cpu` scores them clean: `--group "shape-box ("` shows
  the top edges equally clean in the store and the CPU bake (worst corner 45
  and 46 degrees), and what the GPU bake adds is a 1.1 m cluster of 32-degree
  shading normals along the bottom back edge (y 0.051, z 15.756), the face that
  rests on the floor.

### Test runner and replay

Owner: boot-reuse follow-up (runner), replay specialist · medium and low ·
Next: the batch loop's break (`WorldTestBatch.cs:89`, one line), then reuse
one host across differing scenarios (in flight: boot-reuse).

- Decide (the lead's call): boot reuse boots the shared base once, checkpoints
  it, and applies each scenario's own rows, rules and state after the restore,
  behind a defined reset boundary (grants, journals, addons, machines,
  subscriptions, input and sibling instances) shown equal to a fresh host on
  representative scenarios; `--reproduce` stays exact and independent, never
  sampled.

- Boot-once never reuses a boot in practice: every `puck test` run on the
  trimmed set prints one worker process but `host-boots` equal to `world-runs`
  (28/28 hall, 24/24 chess, 20/20 go, every file), because a worker restores a
  checkpoint only between scenarios whose boot shape is identical and each
  generated test world differs. Every scenario still pays a 2-4 s host boot plus
  about 3 s of authored-phase overhead even at a few ticks, now the larger share
  of scenario time. Boot the shared base once, restore its checkpoint, then
  apply each scenario's own rows, rules and state, keeping determinism and
  `--reproduce` exact.
- `--reproduce` cost: a reproduction runs every scenario again, so it costs
  about as much as the ordinary run (chess 1,107 s ordinary against 2,846 s
  with reproduction, go 546 against 1,357, hall 468 against 1,070, in
  single shared-machine samples). Boot reuse above lowers both legs; then
  measure what the replica itself spends.
- [low] `WorldTestBatch`'s loop breaks on `batch.m_failed`, which stops the retry
  of a leg that failed to open but also every remaining leg after any earlier
  failure (a `--reproduce` divergence) wherever the next leg needs a fresh host,
  so those scenarios report "did not finish successfully" without running; the
  same-host branch (`CompleteLeg`) keeps going. Break only when opening or
  starting leg `m_index` failed (`src/Puck.World/WorldTestBatch.cs:89`). (from:
  reproduce-cost review)
- [low] `--cache-dir` redirects only the `puck test` workers: the `puck test`
  process itself still persists compiles to the per-user `compilations`
  directory (`src/Puck.Cli.Worlds/WorldsRoot.cs:48`), and `TestCommandLawTests`
  and CI runs, which pass no `--cache-dir`, write the real per-user
  compiled-world, compile and mesh caches. Make `--cache-dir` cover the compile
  cache too, or narrow the wording in `.claude/skills/verification/SKILL.md:84`,
  `TestCommand.Leg.cs:515` and `docs/reference/cli.md`. (from: reproduce-cost
  review)
- `replay.record` mid-session re-grants document grants revoked at runtime
  (`Host.Addons.Finish` after the checkpoint captures grants), and refuses
  `StartNotCheckpointable` after every pumped addon was removed
  (`WorldPersistence.Checkpoint`). Read from the code in a pre-`main` review and
  not reproduced; confirm with a failing test before fixing.
- `WorldDocumentPaths.RelocateBetween` writes rooted paths into relative-only
  fields when the test directory and worktree sit on different Windows drives,
  failing `puck test` there (read from the code, not reproduced).
- A crossing scenario's sibling verdict is "held on some tick", so a negative
  claim about a visit (flight refused there) is settled on what she brings home;
  and a destination reaped when it empties leaves nothing to export
  ([testing a world](../authoring/testing-a-world.md#crossing-into-another-document)).

### Collision versus drawn mesh

Owner: next geometry and bake lane · red sweep signal · Next: re-run the
rendered mesh comparisons on the corrected rounded-Box diagnostic, then the
surviving red findings by cluster, starting with the hall stairs.

- The windowed collider and mesh disagreements: of the original 637 of 9,202 red
  checks, buried shared stair faces were not exterior boundaries, and the
  comparison now excludes certified opposing planar face interiors while still
  measuring edges and corners. The last Vulkan sweep measured 243 red of 8,514
  exterior checks and two field-versus-mesh failures, kept as 87 red findings
  (24 hall-stairs, 17 Go table, eight camera-course). One stair witness lies
  0.202341 m from the mesh against a 0.036705 m budget; the two Wonderland field
  witnesses measure 0.005692 and 0.005615 m against 0.005305 and 0.005308 m.
  No tolerance was widened. Headless measures the authored field with no mesh
  comparison.
- Re-run the rendered mesh comparisons on the corrected rounded-Box diagnostic
  before attributing the earlier mesh witnesses to the bake: on the old sharp-Box
  base the hall stairs (`flight-a-2-6`) had a nearest-surface gap and the
  Wonderland stairs (`cases-8`) a mesh and field discrepancy just beyond their
  bake precision.
- A solid screen still collides as its world-axis bounds
  (`WorldColliderSet.ScreenBox`), exact only for an axis-aligned slab; it needs
  a rotation from its `Right` and `Up` frame.
- [low] The presentation query field (camera clearance, surface effectors)
  excludes the solid placement `desk-chair`: its evaluator refuses a
  `RepeatPolar` fold (`[world.presentation-field: excluded placement
  'desk-chair' ... RepeatPolar]`, now narrated once per placement), so the chase
  camera and limb probes see no chair. Give the fixed-point query twin the
  fold, or bound the folded shape.
- The bake-side causes live in the [rendering plan's](rendering.md#open-items)
  bake items: a long thin member of a static union baking at the union's capped
  lattice, the sharp-edge rounding of about a tenth of a cell, and the Moth's
  interpenetrating meshes.

### Edit to running, hot reload and boot

- [medium] A `world.load` from debug into a same-capacity input fixture leaves
  authored state-writing rules refused with `the live edit grant refuses
  state:blink` (and the fixture's counters stay zero); a fresh boot of that
  fixture runs them. Reproduced in C7 with `input-genre-controls.puck` temporarily
  set to the debug world's capacity. The seeded `World` Edit grant appears lost
  across load; that cause is an inference, not yet confirmed. Owner: boot/reload
  lane. C7's genre sweeps boot each source fresh.

Owner: hot-reload follow-up (boot and build) · two medium, the rest ordinary ·
Next: the hot-reload code revision as one invalidation contract (the
compositor's emitter revisions and the static-field rebuild), then the
persisted cache identities, then the parse count; then a reboot for an edit
outside `PuckHotReload`.

- Decide (the lead's call): The first crossing's 639 first-use JIT methods: a self-contained composite ReadyToRun publish or a rehearsal transfer at boot.
- Decide (the lead's call): every persisted product is keyed by a hash of what
  it read, and `CodeRevision.Current` folds into that identity while an update
  is applied, so a hot-reloaded process never reads or writes a product of
  edited code under the unedited identity; a built process derives the
  ordinary identities.

- [medium] A hot-reload code revision never invalidates the compositor's emitter
  revisions, and the static-field rebuild also needs a changed program, so
  hot-reloading scene emission with unchanged world revisions keeps the old
  geometry and query field (`src/Puck.World.Client/WorldFramePresenter.cs:567`).
  (from: edit-to-run review)
- [medium] `CompiledWorldCache` counts its synthetic parse before a possible real
  authored parse while writing the cache, so a source-hash hit that keeps
  `DEFN`/`ASST` but regenerates a missing `CURV` counts two parses where the
  next complete-cache boot counts one
  (`src/Puck.World.Schema/CompiledWorldCache.cs:318`). (from: edit-to-run review)
- An edit to a project outside `PuckHotReload` is refused ("can not be edited")
  and `dotnet watch` stops applying changes without rebooting; it should reboot
  as a rude edit does.
- Persisted caches keyed by the modules as built (the compile cache's compiler
  identity and the compiled world's engine build, both module version ids, and
  the mesh bake's closure read from the built IL) cannot see an applied update,
  so a hot-reloaded process could read or write a product of edited code under
  the unedited identity. `WorldSourceIndex` bypasses its persisted declarations
  once `CodeRevision.Current` moves; the others do not yet.
- An applied update leaves the installed SDF pipeline set as it is: the
  pass-pipeline cache is keyed by bytecode, so an edit to the C# that describes
  a pipeline shows only after a reboot.
- The mesh bake's identity reaches past what the bake reads: a fix that changed
  only `SdfMeshInstance`'s motion identity (a draw record, not a bake input)
  sent the next Vulkan boot into a full 333 s rebake (a cold bake of the debug
  world is 314 s on Direct3D 12 and 333 s on Vulkan, 2,022 groups and 1.09
  billion samples, per backend store). Hash only what the bake reads.
- Re-measure the loops on an idle machine against the targets: a
  `Puck.World.Client` edit to ready under 20 s, an HLSL edit settling under
  30 s, boot to ready under 6 s. On the loaded lead machine a hot-reloaded
  Client body edit applies in 45-54 ms after the watch's first edit (14.3 s), a
  light-stage constant edit settles through `world.shaders.reload` in 11.8 s, a
  headless boot reaches `world.status` in 4.6-4.8 s, and the `dotnet run` loop
  after a Client edit took 30.5-42.4 s at 100 % load, 22 s of it the build.
  Known costs left: a cold build pays 20 to 35 s of MSBuild node startup over a
  warm one; a warm SdfVm body edit spends about 3 s evaluating the World's
  projects and 5 to 6 s checking the projects below the edit; the boot's
  largest piece is `System.Text.Json` configuring the `WorldDefinition` metadata,
  mostly JIT ([contributing](../development/contributing.md#edit-to-running)).
- Prepare machine content once per input: every boot compiles the debug world's
  `.puck` cartridges again (`CartridgeSource.Prepare` through
  `HgbCartridgeCompiler`, about 4 % of a headless boot), though the image is a
  pure function of the source, its imports and the compiler. Key a cache by the
  inputs the compile read, as `WorldCompileCache` keys a world compile.
- Measure a ReadyToRun boot. JIT and type loading are about 35 % of a headless
  boot's main thread; the World README's publish recipe
  (`dotnet publish src/Puck.World -c Release -r win-x64 …`) fails its locked
  restore (NU1004: the `-r` reaches every library, whose lock files carry no
  runtime identifier). Fix the recipe, then decide whether projects outside the
  edit loop build ReadyToRun locally.
- The boot's first guide write cost about 27-30 ms of simulate in a rendering
  World; `WorldCodeWarmer` took the first endless-fall crossing from 20-24 ms to
  8-10 ms and the watchdog's first write from 30-37 ms to 12-19 ms. Recheck the
  original windowed cold-write cost; one earlier warm walk's single 32.4 ms
  simulate frame wants a `dotnet-trace` (`dotnet-sampled-thread-time`) capture
  before it is named.
- After a full `world.reload`, a debug World reported `blink-close`'s state
  transaction refused because the live edit grant refused `state:blink`, then
  the companion's sensory and glow rows stayed at their reload seeds while the
  guest flew on. Fresh boots and an addon-row replacement worked; the cause is
  unconfirmed.
- A `--no-incremental` Release build once failed `SHADERPKG_OUTPUT` with a World
  shader package directory holding binaries but no manifest; clearing
  `src/Puck.World/obj/Release/net11.0/worlds` and building again passed.
  Unconfirmed as a packaging race.

### Debug tools

Owner: debug-world tools lane (specialist) · medium · Next: the pick
(`world.view.pick` addresses the presented world view by default, lists it in
`panes`, and answers under `world.rate pause`), qualified in one windowed pass;
then the diagnostics' windowed checks, then the three developer exhibits.

- [medium] Under `world.rate pause` the pick has stayed pending with no answer;
  the hold now claims a paused World answers, bounded by the call's deadline
  (`Picking.cs`), which only a windowed run shows. The default view and the
  `panes` listing are done (`world.view.pick <x> <y>`, `view instance:<name>`);
  the paused run is owed. (from: owner walkthrough)
- Restore and inspect the remaining developer exhibits: the rate range (LoD and
  rate tiers), the traversal course's geometry and the kinematics scope
  (position and measurement). Clean bot verdicts do not show that they look or
  measure correctly.
- Diagnostic views that flag suspect values without covering them
  ([decision](../decisions/rendering.md#a-diagnostic-view-never-covers-its-evidence)):
  - Phase 1 (the preserve-scene path, finding records, `report`, pins and
    exterior marks, capture triplets, pick and goto by finding, with the
    `colliders` and `contacts` views) is implemented; the deliberate mismatch,
    hovering and grounded-on-air scenarios still need the
    [windowed checks](../reference/commands.md#scene-diagnostics). Retained
    dynamic supports, field-provider support witnesses and interpolated body and
    render pose pairing remain explicit unknown coverage.
  - Phase 2: the `probes` view (a final contributing probe whose segment to the
    receiver is blocked) and the `exposure` view (checks against the probe-based
    meter, `SdfMeshIrradiance.TryMeter`).
  - Then `view.annotate` as the human half: the diagnostic pins, contours and
    calipers extend its `AnnotationWriter` with no second overlay writer; a
    circle runs `world.view.pick` inside it and lists what it holds (stamp,
    creation, node, body) and any findings there; each circle is anchored to its
    picked world point, so it follows the thing and `goto` reaches it. A live
    `world.mesh.audit` that circles each bake cluster through it follows.
- The tight-space camera course's three-up `camera-compare` layout and a live
  mode toggle on the Debug page remain to author (`camera-course.puck`).
- `gallery.inspect`'s values (60 lux fills, EV100 7) are first picks tuned on
  one exhibit (fill 150 blows out), and a flat ambient for baked meshes (the
  indirect gains stop at 1) would replace the fills.
- `puck compile --validate` does not run the binding compiler, so a page binding
  an Axis1D source to a digital command (a trigger to `player.wheel.commit`)
  passes it and is refused at boot.
- The rendering plan holds the GPU looks owed at the gallery (the warps II row,
  the hall's light switches) and the `puck mesh audit` gaps
  ([rendering plan](rendering.md#open-items)); the slice's GPU far field and a
  live slice view are [E12's](editor.md#open-items).

### Simulation tick off the frame thread

Owner: specialist or Astra design lane · two medium, the rest ordinary · Next:
the catch-up burst bounded by the wall time it has spent and the wake margin
derived from the contact skin, both confirmed in the code (`FixedStepPump.Advance`,
`WorldPopulation.Sleep.cs:154`); then the thread's design consensus, once the
owner queues it.

- The thread itself is an idea discussed with the owner and not yet queued:
  a design consensus across the families, which the owner's ruling says starts
  only when the owner reviews it and says so. The input lane it waited on has
  landed.
- Decide (the lead's call, for that consensus to confirm): one simulation
  thread owns the pump, the server and every instance host and publishes
  immutable per-tick snapshots into a small ring the frame thread reads
  latest-complete; input is captured on the frame thread into a per-tick queue;
  offscreen and `world.lockstep` run the tick inline under the same pump;
  console, MCP and live-edit commands marshal through the pump's existing
  queue; lifetime and bounded backpressure are settled before any lock-free
  claim, since a collection pauses both threads.

- [medium] The localized wake around a moving carrier uses a fixed 1 m margin that ignores the configurable contact skin: with a valid `contactSkin` of 2 m, a sleeping sphere resting at skin distance above a lift misses the wake box during small car movements and stays asleep instead of riding (`src/Puck.World.Server/WorldPopulation.Sleep.cs:154`). Derive the margin from the contact skin (and the body's own broadphase radius). (owner: next simulation batch; from: lift-lag review)

- The windowed host runs `FixedStepPump.Advance` and the frame on one thread, so
  a frame carrying a tick pays its 4 to 10 ms and the tick sets the frame's p99:
  uncapped on Direct3D 12 a frame without a tick costs about 4 ms and one with a
  tick 10 to 13 ms. Presentation already interpolates (`RenderTicks`,
  `InterpolationAlpha`) and the server and client already split through
  snapshots. To design: a lock-free, allocation-free handoff; input capture on
  the frame thread consumed per tick; offscreen and `world.lockstep` keeping
  exact one-tick-per-frame; collections on either thread not stalling the
  other; console and MCP verbs marshalled to the tick.
- What lands on a stepping frame today holds the standing p99 near 10 ms:
  simulate itself (2-4 ms a tick standing still: contact sweeps and rules;
  `ResolveSweep` is 23.4 % of `FixedStepPump.Step` in the gallery walk), and the
  session and window views whose residencies change with the tick (about 100
  view nodes), which record on tick frames only
  (`ShaderPipelineRenderNode.RenderFrame` 2.8 ms on a stepping frame against
  0.9 ms between). Read the walk's simulate wall clock on a quiet machine.
- Walking the Moth among the gallery exhibits holds every presented frame under
  16.7 ms (`body.pose 30 - 22 90 - -`, `player.move 1 0` for 60 ticks,
  `host.frames`; RTX 4070, Direct3D 12, bakes settled): on a machine other lanes
  held at 80-93 % CPU, warm walks read p99 19.8-23.7 ms, max 22-41 ms.
- The endless fall's first visit (`body.pose 66 - 22.6 0 - -`, fresh windowed
  World) holds no frame over 33.4 ms of work, later crossings none over 16.7 ms;
  it reads a worst frame of 38.9-41.7 ms and a second visit 16.3-16.5 ms:
  - its first frame or two produce 20-30 ms in pieces of 2-6 ms (the window
    feeds' first binding, `WriteOwedMeshes`, `PackDynamicTransforms`, the
    exposure meter's camera trace); trace at a finer interval before naming one;
  - frames 27-40, where the floor and ceiling windows first render at full
    size, read 20-28 ms (produce 16-27 ms);
  - frames whose work is 9-14 ms but whose pacer waits 17-20 ms, or whose
    `Present` blocks 18-26 ms: the GPU reads 76 % busy there (the floor and
    ceiling paintings each draw the world); measure its GPU time per view
    (`world.counters gpu`) and cap the painting views' resolution or recursion.
- Young collections: a gen0 pause is 10-12 ms with 4 MB promoted (2.6 ms marking
  through cards, about 7.7 ms planning and compacting). Server GC with four heaps
  collected rarely but paused 27 ms, so it was not kept; fewer young objects
  referenced from old containers (the server's `StateCell`, `WorldStateRow`,
  `UpsertStateRow` and their strings, the state journal) is what shortens it.
  On a quiet machine one gen0 pauses 10 ms in 1,800 standing frames, and a gen1
  pauses 13 ms on both visits to the endless fall.
- A window's glass resize allocates managed memory: on a long approach to a
  painting the glass resizes about 22 times and one gen2 collection pauses the
  frame thread about 12 ms (RTX 4070, inside the 32 Hz pacing wait). Make the
  resize path allocation-free or step the glass extent with hysteresis, and
  re-measure the approach on the RTX 2060.
- [medium] The fixed-step pump's catch-up multiplies an over-budget tick: a
  host frame clamps its wall delta to a quarter second
  (`LauncherWindowHostedService`, `maxFrameTicks`), and `FixedStepPump.Advance`
  then runs every step that delta owes, so once a tick costs more than its
  1/30 s period each frame runs 7 or 8 steps and lasts 8 ticks' cost (the
  lift before its wake fix: about 120 ms ticks, frames of 0.9-1.3 s, the World
  at a quarter speed). The clamp bounds simulated time, not the frame. Bound a
  burst by the wall time it has spent (drop the rest, rebasing the capture pin
  as a clamp does), so an over-budget tick slows the World instead of the frame.
  Two more readings of the same multiplier: eight catch-up steps cost roughly
  400 ms a frame on a slow gallery boot, where pausing the boot instance took a
  frame from about 366 ms to 19 ms; and a cold-bake windowed Vulkan boot (nine
  lanes loading the machine) read simulate peaks of 1.2-2.1 s in 7 or 8
  catch-up steps over 600 frames averaging 232 ms.
- [low] Every rigid advance zero-fills the step's capture snapshot
  (`WorldBody.CaptureMotion` → `CaptureIntegrationResidue`, about 7 % of a rigid
  body's advance in an EventPipe sample of a lift ride).
- Pacing at 120 Hz with Windows' Dynamic Refresh Rate (the lead machine's
  display now runs 120 Hz with DRR on, VRR 48 to 120 Hz): confirm the 60 FPS bar
  and VRR pacing hold in front of and behind another window, and that
  `world.target display` reaches 120 Hz.

### Dual-stick play and the Moth's motion

Owner: play-feel lane, with the owner's feel sittings · ordinary · Next: the
reversal brake, then a sitting; the rate-limited facing op only if a
movement-facing scheme returns.

- [medium · owner: controls] Finish the standard extraction's windowed comparison
  before extending it with zoom, dash or layouts. The shared design now lives in
  `worlds/standard/`, with Go/Tour, Places and gallery icons refining it in
  `worlds/debug/controls.puck`. Headless composition and the 23 input scenarios
  reproduce; stills remain owed because both bounded GPU attempts expired on a
  live foreign lease. The saved original and paired capture scripts are in the
  controls worktree's `.claude/scratch/controls-evidence/`; the hand-back is
  `.claude/scratch/controls-handback.md`. Batch the comparison into the lead's GPU
  verification window, then resume the three owner items below.

- Binding bar layouts again (the owner's request; with the controls work, which edits the same `controls.puck`). The engine has both switches (`WorldBindingBar.LayoutCell` names the live authored layout; `ModelCell` draws one bar when `single`, every page with the rest as wings otherwise); the debug world authors only `standard` and fixes `barModel.model` at `single`. Recover and author: (1) the linear strips `keys` and `pad` from `controls.puck` at commit `8962c3d5a7` (lines 304-384: `pad` one row, left stick and Back, right stick and press, the face diamond at the right end; `keys` a WASD cross and action keys in a row with camera and function keys above), which the play wheel switched through `state.bar.layout` (`player.state.cell.toggle ... bar layout keys pad`, about line 465); (2) the wings, the WoW addon's modulo layout (commit `e5841bfdad`, `experimental/Puck.Demo/BindingBar/BindingBarLayout.cs`: two mirrored six-slot diamonds, `BarCount` 1 to 5 pushing bars 1-4 outward and downward, at the addon's 600-line reference: button 45 px, centre gap 60 px, anchor 220 px, badge 24/45 at 0.4375) through `ModelCell`; (3) a classical bar with flanking sidebars, never authored, built on the schema's "strip with side columns" (three groups on three anchors); (4) a compact size, which was never a mode (August worlds set `buttonSize 0.075`, `scale 0.72` per world), as a player setting over the layout's scale. Switch layouts, wings and size from play-wheel sectors and a console verb, kept per seat.

- Mouse-wheel camera zoom (the owner's request, folded into the next input or camera lane after input-c7): the wheel moves the seat camera's boom in and out between a world-defined minimum and maximum (authored beside `seatCameraFeel` and the `camera-boom` dynamics row, with sensible standard defaults), eased by the boom's own smoothing; a middle-button click resets the camera (boom distance and orientation to the seat rig's defaults). While the play wheel is open the mouse wheel cycles its rings and never zooms (the owner's ruling: keep that behaviour and make the two play nice): the open wheel takes the wheel input first, zoom resumes only after it closes, and a scroll that opened or closed the wheel mid-gesture neither zooms nor steps a ring twice. Input C7 now supplies the relative Axis2D source `mouse.wheel` (vertical `.y`) and the middle button `mouse.button3`; wire camera zoom and reset through those names and consolidate the existing ring-scroll path so each scroll has one destination.
- A dash for the Moth (the owner's request): a short, nicely animated burst along the move direction (or facing when idle), bound to the gamepad's west face button (`gamepad.buttonWest`: X on Xbox, Square on PlayStation, Y on Switch Pro; the owner's ruling) on the Move page, whose current West action (the bar shows it as `»`) moves to another slot, with a keyboard equivalent, with a cooldown, a rig animation (lean, trail or thruster flare from the Moth's back thrusters) and deterministic simulation (fixed-point impulse, input from the command snapshot). Rule 7: build it on the existing locomotion and rig drivers, not beside them.

- Make the debug world's dual-stick action play fluid. In: the seat camera
  presents its look at the fraction the body is drawn at, so camera and steered
  body turn together between 30 Hz ticks; `ComputePlanarTargetVelocity` takes
  the program's `planarTarget` (strafe and backpedal fractions of the move
  speed); starts and stops ramp through `ShapeVelocity` (`along { engage,
  release }`); a partly deflected stick commands a proportional speed. Open:
  - the owner's feel sitting on the Moth's values;
  - a rate-limited facing op in place of `SnapYawToPlanarIntent`'s instant
    heading, wanted only if a movement-facing or pointer-steer scheme returns
    (the right-stick steer turns the body at the look rate, 0.087 rad a tick at
    2.6 rad/s);
  - a reversal that brakes faster than it accelerates: `ShapeWholeVelocityRow`
    converges at the engage rate whenever the stick is deflected, so a full
    reversal at 40 m/s² takes 8 ticks at 5 m/s.
- The Moth's directional motion is authored rig data
  ([how the moth moves](../../.claude/skills/sdf-authoring/references/rig.md#how-the-moth-moves)).
  Open on it:
  - the owner judges the feel live: hips 0.7 rad on a diagonal, a 0.12 s turn
    trail, the lean gains and the `body-turn`/`body-lean` responses are first
    readings tuned on offscreen captures, as are the stride curves
    (`moth-stride`, `moth-gait`, `moth-run`: 2 steps a second over 1.5 m strides
    at 1.5 m/s, 3.75 over 2.7 m at 5 m/s);
  - starts and stops blend over the driver weights' 0.15 to 0.3 s and the
    replant settle step; a stride that stops mid-swing eases its swing to rest
    rather than finishing the step;
  - a run's stance is shorter than the plant weight's 0.15 s ease: at 2 m/s the
    foot's weight peaks near 0.94 as its window closes, so a planted foot is
    neither fully on its latched point nor level (up to about 10 degrees), and
    late in stance the leg cannot reach the latch and the boot lifts toward
    0.35 m;
  - walking backward, her legs cross behind each other;
  - an animation lane (Codex Astra), judged through `puck motion` contact sheets
    and the studio scenarios (`worlds/debug/motion.scenarios.json`), starts only
    when the owner says go.
- Keep the run channel held through the motion scenarios' steady-run captures:
  `body.press run 1 4` is capped by the default Drive grant to two seconds, and
  `run-steady` and `pilot-run-steady` start capturing at tick 60, when that hold
  expires, so their samples show the return to walking. Re-press within the
  budget or grant the scenario session the duration before judging a 10 m/s run.
- Isolate the one-level residue in tick-exact stills
  ([procedure](../../.claude/skills/puck-world/references/schedules-and-tests.md#tick-exact-debug-capture)):
  the full Direct3D 12 motion comparison passes all 34 scenarios and 538 stills
  with both hashes matching, 484 stills pixel-identical and 54 differing on at
  most nine pixels by one channel level. The enforced bound stays 20 changed
  pixels per still and one level; the source is not isolated or proved
  irreducible.
- The camera: playtest first-person feel in the lift and stairwell, tune per-form
  eye and close-target points; what the chase camera does with its back to the
  stair wall is the owner's call (`world.patrol 12 14 68 38 4` flags (12,34)
  `OFF BODY` on levels 0 to 2: the wall at z=35 shortens the boom to 0.35 m and
  the room-first `fade` hides her, the design working as built).

### Cross-backend parity and GPU robustness

Owner: rendering (GPU leg, lead GPU scheduling) · ordinary · Next: after
Lighting and Body scale land (each would invalidate a recording), a small
representative capture set with explicit settlement rules and world
invocation, then the debug world's contract recorded on the RTX 4070 so
`puck parity` runs again.

- Cross-backend parity (`puck parity`, `experimental/tests/Puck.Parity`) is the
  only automated gate on the shared engine contract (the cross-backend render
  path, the SDF VM ISA). The debug world authors no `captures` rows and has no
  `<stem>.contract.json`, whose census floors and thresholds need a GPU
  recording; the gate's parity step invokes `puck parity` with no world, which
  refuses by name. Author the debug world's captures and contract, record them
  on the RTX 4070, and point the gate at them. The rendering plan's owed GPU
  legs (whole-instance rejection, the ISA renumbering, P10's stations) wait on
  it ([rendering plan](rendering.md#open-items)).
- Settled parity in the lift shaft: a cold Vulkan shaft is near-black with no
  meter until its 496 static draws and the three-sweep GI complete; after it,
  the shaft meter read 4.087 nits against Direct3D 12's 6.576 (both EV100 7)
  while other geometry still baked. Repeat with all geometry settled and verify
  the complete gallery on both backends; the slow boot's catch-up steps are the
  [simulation packet's](#simulation-tick-off-the-frame-thread) multiplier.
- Vulkan's warm crossings cost 21 to 36 ms back to the debug world and 34 to
  143 ms into the garden, view nodes' first submissions after a reconfiguration
  blocking in `vkQueueSubmit`; the crossing and pixel-pass items are in the
  [rendering plan](rendering.md#open-items).
- A cold-bake windowed Vulkan boot allocates heavily (read with nine lanes
  loading the machine): over 600 frames of a cold bake, 95 collections paused
  3.2 s in all (single pauses of 260-456 ms) while the bake landed 45 meshes in
  two minutes; its catch-up spiral is the
  [simulation packet's](#simulation-tick-off-the-frame-thread) multiplier.
  Re-read on a quiet machine; if it holds, find what a cold bake allocates.
  Vulkan's first reading after its cold bake also had eight frames of 14 to
  15 ms frame-thread work, a post-bake transient.
- Vulkan's board-integrated boot: the patrol reports 40 stalled points and four
  hitched points, worst work 940 ms at (49,25), cause unconfirmed (the earlier
  pre-board patrol had five stalled points and worst work 26 ms); standing
  busiest 16.02 ms, first lights Off/On 33.13/30.38 ms.
- Harden the Direct3D 12 binding and bring-up paths a code audit flagged around
  device removal (none shown to have fired): descriptor writes
  (`DirectXGpuBindings.WriteBuffer`, `WriteSampler`, `WriteStorageImage`,
  `ViewSlot`) and `AllocateSet` write into a pool's heap addresses without
  checking that the pool's heaps are still the device's, so a write between
  `DirectXDeviceContext.Recreate` and an owner's `OnDeviceLost` lands in released
  heap memory (stamp each pool with its heaps and refuse the write);
  `DirectXDeviceContext.EnsureCreated` brings a device up without a lock, so a
  background build touching `Device` during `Recreate` can race a second
  bring-up; pipeline, pool, set and command-buffer tokens are bare `GCHandle`
  values with no generation (`DirectXImageViews` shows the generation-checked
  form).
- Repeat the `world.wait` and `world.reload` over MCP against a windowed World on
  the mesh path at quality high, where frames are slowest; only headless runs
  have shown it.

### UI step 1: the engine leaks out

Owner: UI lane · ordinary · Next: finish [step 1](ui.md) against its check,
since the engine's neutral default theme (`WorldThemeSection.Default`; no
phosphor role in `WorldTheme`) and a warp that applies only to
`WorldScreenMaterial.Crt` are in: the CRT inset deleted, the unread diegetic
set and component chrome out of the core sets; then the overlay text's advance.

- Overlay text is spread too wide: each character advances a padded atlas cell,
  not the font's advance. Related: a 12 px mono run's 18 px cell spans the whole
  padded atlas cell (1.729 em), so its em is 10.4 px; decide whether overlay
  mono text renders at its nominal size.
- The CRT material still draws a bezel band inside its glass
  (`WorldScreenMappings.CrtGlass`, an inset of three hundredths of each side,
  mirrored by the glass shading; `WarpOf` gives every other material none), so
  a CRT device's picture is 94 % of the glass the owner ruled is exactly its
  picture, with the bezel outside the glass
  ([decision](../decisions/rendering.md#in-world-devices-and-screens)). Delete
  the inset: a CRT's bezel is geometry authored outside the face.
- Look at the controller bar on the GPU after the modifier-row and opener change
  (`declaredModifiersOnly`, the Move page's LB and RB opener plates, `WH` and
  `GW`) at 3840×2160, and with a PlayStation and a Switch pad; it was seen at
  1280×900 only. In the garden and the night field RB opens no wheel and reads
  unbound.

### Transfers, federation and server

Owner: specialist, transfers and server · medium · Next: build the companion
decision (the companion lands inert in the live debug world today; the lead's call
between the consensus split), then the uncertain crossing, opened by a two-silo
failover witness.

- [medium] A returning companion lands detached: `WorldInstanceHost.Transfers.cs:353-355` overwrites the traveller's `PreferredSlot` with its destination slot (4 in `night`), so `PreferredOrLowestFree` (`WorldTransferEscrow.cs:468-484`) lands it home at slot 4 instead of 42 with `PlacementId = null` and the default look and kit, and its addon (granted `GrantSubject.Body(42)`, slot-indexed in `WorldHandleTable.cs:104-123`) no longer drives it. The companion-driver decision (role contract, home re-adoption by mobility incarnation) is the fix; until then the defect stands. (from: companion-driver consensus, Antigravity's code read)

- [medium] Build the [companion decision](../decisions/companions.md): role and preferred-driver (pinned addon hash) fields on the reservation member; a destination's role declaration (`accept` the visitor's own pinned driver, the same pin, a substituted driver, or nothing), with `worlds/standard/` declaring `accept`; refusal at reservation with a HUD notice to the player when a world declares nothing; a separated companion (left behind, or its player leaves) returns home; home re-adoption by mobility incarnation into its original placement, the away companion counted present so a reload does not replace it; driver grants keyed by placement or role group instead of `body:42`; the faerie's lasting state in registers; `night` declares the companion role with the faerie's pin. (from: companion-driver consensus)
- Resolve an uncertain crossing through the authority directory, not a
  colocated row. A silo row whose crossing log answers `Uncertain` cannot write
  the final checkpoint a deactivation needs, so only a silo restart replaces it;
  if the row fails over to another silo, a source row in the original silo keeps
  its in-doubt transfer bound to the stale local row, which answers `Uncertain`
  forever. Route a transfer's doubt to whichever activation holds the
  destination's fence, and let a blocked row retire without a final checkpoint.
  Done when a law fails the destination over to a second silo while the source
  lives on, and the source settles from the new activation's journal with
  exactly one traveller ([worlds](../architecture/worlds.md)).
- [medium] `body.pose spawn:<id>` naming a point only the home world declares
  refuses, by name, a seat whose body a federated authority holds: no submission
  asks a remote owner to transfer its body, so the traveller has to cross back
  through that world's portal. Carry it home through its committed route
  (`WorldInstanceHost.TryEnqueuePoseHome`). (from: pose-home fix)
- Re-evaluate federation disclosure tier and counterpart trust on live policy
  edits and active `Observe`/`Reserve`/`Route` sessions in `WorldPeerHost`
  (`src/Puck.World.Server/WorldPeerHost.cs:219-227, 475-480, 989-995, 1071-1077,
  1177-1184, 1593-1625`), and handle `WorldDisclosureTier.Frames` in
  `WorldFederationCodec.EncodeDocument`
  (`src/Puck.World.Server/WorldFederationCodec.cs:456-460`) without throwing
  `InvalidOperationException`.
- Pre-`main` review findings read from the code and not reproduced; confirm each
  with a failing test before fixing:
  - `WorldServer.WriteStateCell` throws on a stale `CellKey` instead of
    returning `StaleAddress`;
  - `ResolveDevelopmentRequest` and `ResolveBodyPoseRequest` drop a query's reply
    when host fuel is exhausted, breaking one reply per query;
  - `WorldStateDocumentValues` formats Bool as `1`/`0` in one path and
    `true`/`false` in another; `ValidateStateTransaction` validates the
    unmaterialized definition;
  - `RuleExpressions.Run` swallows an absent value inside `not`, `and` and `or`;
  - `StateSeedReader` builds a state section without lattices, spaces, enums or
    pools;
  - `ArenaSnapshot` and `ArenaUndo` index rows by non-document lane ordinals and
    keep change-window state across `TryRelayout`;
  - `RuleCompiler` checks control audience twice and validates a transaction's
    failure branch before its primary branch's reads;
  - `ValidateBodyScale` and `ValidateRowSelector` throw on a null `bodyState`
    entry.

### MCP and the operator attachment

Owner: MCP lane (specialist) · medium · Next: the cached wait refusal (cache
only a start's terminal outcome), then the per-swap state load, then the
schema switch.

- [medium] Investigate a GPU lease held by an older operator worker while no
  World is advertised and the worker has no direct child process. During the
  controls extraction, two 1200-second windowed starts expired waiting for that
  lease; a normal machine process read confirmed the worker was alive, and an
  exclusive probe confirmed its GPU guard remained held. No foreign process or
  lease file was changed. The cause is unconfirmed; inspect the owning session
  and the `OperatorWorlds.WatchAsync`/`Stop` lifecycle, then release it through
  that session. Evidence and start ids are in the controls hand-back.

- [low] A `puck_world_start` failed with "Access to the path .claude/leases/gpu.queue/000000000062 is denied": a Windows-held ticket file. The lease-rotation change counts an undeletable ticket as live on the new CLI; check the start path (the MCP adapter's lease delegate) handles the same error rather than failing the start. Also from the same lane: `world.view.pick` reports `placement=none` for static hall geometry and for screens (only the chess table resolves to a placement), and the pick answer does not name the prototype or palette material though `SdfPickResult.MaterialName` exists. (from: bars-and-pick lane)

- [medium] `puck_exec` returns every line's full answer, so `world.affordances` and `world.grants` overflow an agent's token limit. Add an output filter to the tool (selected `lines` or `fields`, a byte cap with an explicit truncation mark) so the agents' own instrument stays usable on large answers. (from: c7-findings lane)

- [low] Operator writes no longer toast (`WorldToastPolicy`), but the `mutation.applied` audio cue still fires for them; the same rule applies to cues. A refused grant from an addon or peer without authority carries no actor and still toasts, which is right but bypasses the new `Actor` field. The windowed capture after the sweep prelude (no toast on screen) is owed: `world.hud toast` answers `none` headless. (from: operator-toasts lane)

- An invalid wait argument on a pending operator MCP start caches the argument
  refusal as the start's completed result: `OperatorMcpFront.Record`
  (`src/Puck.Mcp/OperatorMcpFront.cs:101-108`) stores any non-pending
  `puck_world_start_wait` answer under its `startId` and drops the start, so
  later corrected waits and cancellation return that refusal while the World
  keeps starting. Read in the source from one headless boot and by two
  reviewers; run a minimal reproduction, then cache only a start's terminal
  outcome.
- A connected Claude Code session keeps a tool's old output schema when the MCP
  front switches to a backend whose schemas changed: the first `puck_adapter`
  call after the switch failed with "Structured content does not match the
  tool's output schema" until `/mcp` reconnected, and the front's tool-list
  notification does not refresh it. Keep output schemas additive across a live
  switch, or answer with the schemas the client was given until it reconnects.
- [medium] An attached operator session costs the tick a whole state load on
  every placement-response delivery: `WorldSessionSink.DeliverState` (replica
  tier) discloses the definition again from the arena, so its mirror's
  `WorldDocumentStateView.Shape` builds a new `StateArena` from the disclosed
  rows, 280-380 ms a swap on a loaded machine; a lift ride swaps four to six
  times. `StateArena.TryAdmitImport` clears its large cell-key set for every
  row (`HashSet.Clear` about a quarter of the load). Deliver a holdings-only
  change to the session without a fresh disclosure of state, or make the load
  incremental.
- The wanted next steps, each with the hook it extends: a batch capture that
  writes a series of stills and answers with a contact sheet
  (`ConsoleControlSession.CaptureAsync` already writes a presented burst to a
  private directory; `puck motion` owns the contact-sheet writer); camera tracks
  in recordings (`world.view.camera` is `key=value` fields led by `tick=`, so a
  recorder samples one line per tick through
  `WorldSeatCameraCommandModule.Fields`); compact state diffs (read a row set
  twice and answer the changed cells, with `world.state.hash` as the cheap
  equality check).

### Language and authoring

Owner: language lane (transpiler) · medium · Next: the inline console command
hang, then the pre-`main` language findings.

- [medium] A scratch test using `when { seat1: world.state.cell.set marker 0 7 }`
  on one line consumed over two minutes of CPU during preparation without a
  generated world or a diagnostic; moving the console command and the closing
  brace onto separate lines completed the three-scenario reproduction in three
  seconds. Reproduce and bound or repair compilation of an inline console
  command followed by its block's closing brace (a bounded `dotnet-stack` attach
  timed out). (from: scenario-cost)
- Language findings from the pre-`main` review, read from the code and not
  reproduced: a string literal on the left of a relational gate is not flipped
  (`WorldDocumentEmitter.Rules`); a header `when` gate makes a body's `local`
  report PUCK012; `not` loses its source span and recursion depth
  (`PuckParser.Expressions`); unary `-` or `~` before `not` parses at the wrong
  precedence and fails the format round trip; `PuckPrinter` drops `!= 0` from a
  grouped comparison gate.
- Refuse a `garden { … }` expectation that names a document the world does not
  reference at authoring time (`puck lint`, the language server, `puck compile`);
  today `puck test` refuses it when it stages the test world. The compiled rows
  do not hold the answer: a `references` row can arrive through an import
  composed only at load (the debug world's garden reference comes from
  `paintings.puck`), and a sibling can be reached through another sibling's
  references, so it needs the composed document's references from the composer.
- The generated `prototypes.schema.json` says a trim is "authored as one shape
  against the per-stamp shape budget", but no budget exists (`StampShapeCount`
  only reports bake work); correct the XML docs on `ShapeTrimDocument` and its
  siblings in `Puck.World.Authoring` (`CreationDocument.cs:334, 602, 605`) and
  regenerate through `puck schema`.

### Input channels

Owner: input lane · ordinary · Next: typing/chat exhibits and live hardware. The
[input channel decision](../decisions/input-channels.md) owns the model and each
step's completion criteria; C1 to C7 are in.

- [low] `docs/decisions/input-channels.md:775-787` says a tape reports names from its captured definition and raw slots are optional diagnostic detail, while `documents-binding.md` and `replay.md` now say a segment's raw bank is never labelled (`replay.inspect` prints `segment p1 2s [@3=1] by console`). Reconcile the decision with what the tape can honestly name. (from: c7-findings lane)

- Cross-check the Triton's reports against several independent sources (the owner's ruling: SDL is one second opinion, never the specification), then rewrite the input reference's Triton section to cite them: community reverse engineering ([iczero/steam-controller-stuff](https://github.com/iczero/steam-controller-stuff): 0x7b periodic, puck only; [CouchTurtle/sc2-research](https://github.com/CouchTurtle/sc2-research): describes 0x7b, the one Triton input report SDL3 lacks, with 0x42 and 0x43 layouts; [anderstvoss/virtualgamepad#159](https://github.com/anderstvoss/virtualgamepad/issues/159): a 12-byte vendor report, cadence unknown; [mwdmwd/sc26re](https://github.com/mwdmwd/sc26re), [ded811/CounterPuck](https://github.com/ded811/CounterPuck), [jahrekza/steam-controller-plasma-widget](https://github.com/jahrekza/steam-controller-plasma-widget)), the Linux `hid-steam` 2026 controller patches (input reports 0x42, 0x43, 0x45 and 0x79 for wireless events; merge state unconfirmed), and SDL. Decide 0x7b from that agreement and a live capture on the owner's receiver: parse it if its layout is agreed and verified (likely puck link status), otherwise keep ignoring it with the sources cited; check whether 0x79 (wireless events) reaches our parser. (from: the owner)

- The in-world computer runs programs (the owner's direction): the desktop share (live passthrough, not replayable) is one program and a typing tutor is another, switched on the computer itself (a launcher screen, or a pad button or key chord while engaged), with the screen showing which program is live. The tutor is world-native, on C5 and C6: lessons from the home row to words and sentences on its screen, live per-character feedback, words per minute, per-key accuracy, streaks and a run timer, judged from input history so a replay reproduces every keystroke. The in-world keyboard follows the real one: keys sink on press and the next expected key glows, driven by physical key position (scan code) so its layout matches the real keyboard whatever the OS layout, while typed text follows the OS layout; key state is in input history, so a replay animates the same presses. Lighting the real keyboard's keys (an RGB keyboard's next key) is an optional later step on the existing device-output path (pad LEDs, haptics). Order after C7: the keyboard's press animation, the tutor program, program switching, then real-keyboard lighting.
- Chat typed in the world: a HUD prompt that enters a text-focus mode and
  submits through the existing `chat.whisper`/`chat.log` verbs and inbox grants
  (`ChatCommandModule`).
- Hardware runs owed (note what each shows in its commit;
  [device input](../reference/input.md#limits-and-hardware-evidence)): the
  Triton receiver's live streaming roster (including while neutral) and deliberate
  `0x7b` ignore policy, shown synthetically in C7 but with no attached hardware;
  the
  DualSense and Switch Pro over Bluetooth, the DualSense Edge, one DualSense on
  USB and Bluetooth at once, the classic Steam Controller, the Triton's reports
  `0x45`/`0x47`, its pairing events, its wired, Bluetooth and Nereid transports,
  its trackpad haptics (which pad each side code of reports `0x82` to `0x85`
  drives) and the firmware watchdog setting (lizard emulation staying off without
  the 3-second re-assert), the Switch Pro's HD rumble at other carriers and its
  Home LED patterns, and an Xbox pad with a headset attached.
- GameInput (owner decision): without its redistributable the Xbox path cannot
  see the Series X|S Share button or the Elite paddles, cannot identify a pad by
  device rather than XInput slot, and is capped at four pads; the inbox
  GameInput v0 runtime reaches only the impulse-trigger motors.
- The engaged screens on the GPU
  ([engagement](../../.claude/skills/puck-world/references/engagement.md#the-framed-screen--the-camera-frames-the-engaged-screens-picture-as-an-emulator-shows-it)):
  - the framed screen: in a 1280×900 window the deck's picture settles 720 px
    tall at 3×, whole rows on whole pixels, black around, no scanlines,
    source-exact colours; it refits through a wide, a tall and a maximized
    window; leaving fades the CRT look and the room back in; both bricks; a click
    on screen 16's framed picture opens the desktop as a passthrough source.
    Open beside it: the matte cuts at the ease's settle rather than opening with
    it, and a `views.graphs` post pass (film grain) is not faded by the screen
    fidelity;
  - the desktop with a pad ([desktop](../../worlds/debug/desktop.md#with-a-pad)):
    engaged on screen 16, the left stick moves the system cursor and stops at the
    display's edges whichever window is in front, A opens the desktop and clicks
    (held, it drags), Y and B right- and middle-click, the right stick scrolls,
    LB and RB go back and forward, the d-pad presses arrows, X leaves and brings
    the World's window to the front. Unproven: whether a pad still reads while
    another window is in front; minimize, restore and close, and X after
    minimizing. The engine has no on-screen keyboard, so a pad cannot type text,
    and the `engagement` context family is published only by the windowed
    simulation, so a headless boot never switches to the page.

### Security review

Owner: an independent reviewer (Argon) · unconfirmed · Next: the researcher
brief's priorities ([security review](../development/security-review.md)).

- Complete the independent review: federation interruption, recovery and
  revocation schedules; authorization and disclosure composition; deployed Azure
  identity, storage and key policy; aggregate hostile-input budgets; and
  cryptographic portability beyond the Windows and Linux fixtures. Local checks
  do not establish deployed security or native-runtime exploit resistance.
- Bound aggregate retained bytes across worlds, guests and compiled modules, and
  give a single native WAT conversion or compilation a CPU deadline or a
  peak-workspace bound. Per-host federation, per-document addon and work,
  transfer-history and process-wide compilation-concurrency ceilings bound their
  counts, not the number of hosted worlds or the bytes all hosts retain
  ([priorities](../development/security-review-reference.md#independent-review-priorities)).
  `WasmModuleLoader` uses only the existing LRU's entry capacity and
  `AddonInstance` retains its module after store disposal; weighted LRU support
  exists in `ContentAddressedLruCache`, but eviction cannot release a module's
  byte charge while a guest retains it.
- Verify deployed SAS delegation with an ordinary authenticated caller
  (`src/Puck.Azure.Functions/HttpTriggers/GenerateBlobSasUri.cs:36`,
  `Services/DefaultBlobSasUriService.cs:190`,
  `Middleware/AuthorizationMiddleware.cs:32`): the endpoint uses the caller's
  on-behalf-of storage credential; reproduce storage acceptance outside that
  caller's permissions before calling it a vulnerability.
- Initial binding issuance from caller-writable key blobs
  (`src/Puck.Azure.Functions/HttpTriggers/AttestKeys.cs:57, 147`,
  `Services/DefaultBindingService.cs:109`): an authenticated user can supply a
  public key under their own prefix and trigger issuance when `binding.cbor` is
  absent; the subject is that caller. Establish the proof-of-possession policy
  and a concrete authority crossing before classifying it.
- The Entra bearer-token trust model
  (`src/Puck.World.Azure/EntraWorldAuthenticator.cs:137`,
  `src/Puck.World.Server/WorldRemoteAuthority.cs:1225`): the challenge and
  session are compared but not signed by the bearer, so replay across challenges
  needs a still-valid bearer token; the client pins its remote transport key. No
  bypass was reproduced.
- Azure deployment policy, independent of Front Door: the API-host storage
  condition omits tag and queue-message actions
  (`src/Puck.Azure.Resources/abacConditions.bicep:50`); establish the identity's
  actual role actions first. Authenticated Functions callers can distinguish
  missing share recipients (`HttpTriggers/Shares.cs:70`) and receive health
  exception messages (`HttpTriggers/HealthCheck.cs:56`). No infrastructure or
  deployment change is authorized by this item.
- The withdrawn stub whitespace proposal (`src/Puck.Launcher.Stub/StubInstall.cs:35`)
  stays reverted: it trimmed both sides of the rejected-version comparison with no
  regression witness; reopen only on an executable reproduction. Low priority.

### Coverage and test suites

Owner: suites lane · ordinary · Next: measure the two unmeasured suites' classes.

- Reach 100 % line coverage of live `src/` with the smallest suite plus the
  owner's keep-list. CI measures it (`puck artifacts coverage`) and fails under
  100 %. Every CPU suite run alone under coverage and merged covers 408,582 of
  548,832 measured live lines (74.45 %), short in 2,626 files; the largest gaps
  are `Puck.World` (16,997 of 22,357 uncovered: the console command modules
  above all, then `WorldAudioDirector`, the wheel, cursor, HUD and overlay
  feeds, the probes host, `WorldRenderRoot` and half of `WorldBootComposition`),
  `Puck.World.Server` 11,877, `Puck.Maths` 9,434, `Puck.Platform.Windows`
  8,732, `Puck.World.Schema` 8,679, `Puck.Cli.Release` 6,699,
  `Puck.World.Client` 6,655, `Puck.Vulkan` 5,451, `Puck.Cli.Runs` 4,387 and
  `Puck.World.Transpiler` 3,550. No run loads `Puck.Azure.Functions`,
  `Puck.Launcher.Linux`, `Puck.Launcher.Windows` or `Puck.World.Browser`. What
  remains: measure `Puck.World.Schema.Tests` and `Puck.World.Server.Tests`
  (about 80 s a class under instrumentation; a cheaper per-test attribution
  would pay for itself), refine kept classes to their covering methods, measure
  the GPU-trait classes on a GPU to fill `build/CoverageHardwareOnly.txt`, and
  test or delete every uncovered live line (the coverage job's `coverage.txt`
  lists them). `WorldScreenBinder.Device.*` still holds planning a CPU test can
  reach once a fixture authors what it plans (the infinity layers' follow, the
  nested cameras' levels, a session's own residency, a window session's
  registration); move each part out with the test that reaches it.
- The keep-list (owner approval): exact results and determinism
  (`Puck.Maths.Tests` `LawTests`; World.Server `MusicReplayReDerivabilityLawTests`,
  `CrossingReplayLawTests`, `ReplayForkLawTests`,
  `WorldStateHashCompositionLawTests`, `BodyContinuationHashLawTests`;
  World.Games `ContinuationHashLawTests`; State `ArenaHashLawTests`,
  `DivisionAndPowerLawTests`; Physics `OrderingLawTests`,
  `FixedRigidWorldOrderingLawTests`, `FrictionLawTests`; World.Schema
  `MirroredSolidFieldDeterminismLawTests`; AGB.Forge
  `ColourIdleEquivalenceLawTests`), the C#/HLSL sync contracts (SdfVm
  `SdfKernelHeaderReadLawTests`, `SdfLightAndSkyLayoutLawTests`,
  `SdfPassBindingLawTests`; SignedDistance `SdfEncodingProbeLawTests`; Shaders
  `ShaderRegisterBindingLawTests`, `ShaderFrameBlockLawTests`), and the classes
  verbs run by name (`EntraWorldAuthenticatorTests`, `WorldSiloDefinitionLawTests`,
  `WorldSiloLifecycleLawTests` for `puck artifacts test-world`). The trim also
  deleted guards over non-code files (workflow graph, MSBuild path spelling,
  lock-file ownership, JSON newline spelling, affected workflow readers); the
  owner says whether any return.
- Find what removed live run directories during one concurrent CPU run: with
  every CPU suite under coverage four at a time, `Puck.World.Tests` and
  `Puck.World.Silo.Tests` crashed at exit (exit `-532462766`) because their
  `puck-protoref-process-*` directories were gone when `VocabularyHooks`'
  process-exit handler resolved them, and one
  `WorldTransferTurnRecoveryLawTests` row failed the same way; four live
  directories of four processes went within about three minutes. The
  `build/RunDirectory.cs` sweep is not shown to be it (logged sweeps removed only
  exited owners' directories). Next time, log every deletion under the temporary
  root while the suites run before changing the policy.
- Cut the two heavy World suites further: a Nexus tick costs about 80 ms, nearly
  all the contact solver walking the SDF field through `FixedInterval`, whose
  `Int128` quotient is the hottest leaf, and each of the three
  `NexusSeekingLawTests` rows pays about 770 ticks (about 80 s of CPU). A
  cheaper tick lowers every Nexus law and the game; fewer ticks per row would
  drop coverage. The emulator batteries' share is in the
  [machines plan](machines-and-cartridges.md#open-items).
- Decide the test framework by a spike: xUnit v3 against TUnit (source-generated,
  parallel per test) on `Puck.World.Server.Tests`, by wall time, discovery time
  and CPU on an idle machine.
- Measure on an idle machine, serially: the cold and the warm local build, the
  cold and the warm test pass
  ([verification](../../.claude/skills/verification/SKILL.md)).
- CI's steady state is about twenty minutes end to end: the producer is one
  serial job of about seventeen minutes (SDK and `wasm-tools` installs about
  3.5, the solution compile 5 to 16 by shader cache hits, derivations, packing,
  publishing and the startup check about 4.5, the capture of compiled outputs
  0.5 to 2.5), then four shards of 6 to 8 minutes each. Packing, publishing and
  the determinism record could overlap, and a shard could restore only its own
  assemblies' outputs ([CI and releases](../development/ci.md#build-and-validate)).
- Finish the listing-order rollout (nothing a listing's order reaches differs
  between hosts): laws for the listing fixes under a shim that serves
  directories reversed and shuffled (the CI artifact archive, `puck lint` and
  `puck embed` output order, the orphans `puck schema` reports, the replay
  tape's list, the operator attachment's candidates, the compile cache's
  eviction and the emulator suite catalog's cases; only the probe-kind catalog
  and extension discovery refusals have laws, in `DirectoryOrderLawTests`); a
  clean run of every ledger and generator check (`schema`, `architecture`,
  `registry`, `compile --tree --check`) under a reversed and a shuffled listing,
  which a `puck` verb running a verb under a shuffled listing would keep; and
  the federation hello, federation and crossing laws skip where QUIC is missing.

### Delivery, launcher and CI

Owner: delivery lane · ordinary · Next: the launcher's version pruning (the MCP
development install grows 340 MB a CLI rebuild).

- The launcher cleans up past versions (the owner's ruling): after an install is
  proven, keep `current`, `last-good` and the most recent N other versions
  (N from the user's settings), delete the rest, and drop `objects` no kept
  version references. Today nothing prunes: `UpdateOptions.KeepVersions`
  (default 2, also `host.update.keepVersions`) is only validated and echoed by
  `update.status`. The MCP development install (`.claude/mcp-install`) reached
  8 versions and 3.8 GB before a manual prune; there `last-good` also stayed two
  days behind a `current` that served a whole session, unexplained.
- Stop committing the built addon guests: the eleven `wasm/*/dist/*.wasm`
  modules are compiled from the Rust crates beside them, and a derived product
  is a cache ([decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
  Deriving them on a miss puts the Rust `wasm32` toolchain on every machine and
  CI job that loads an addon, so measure that cost against edit-to-running
  first. A hand-written fixture module (the transpiler sample's 8-byte
  `Samples/addons/rules.wasm`) is source.
- Give the counters workloads `.puck` sources: `tests/Puck.Counters/debug.world.json`
  and `avatar.world.json` are committed JSON derived from `debug.puck`.
- Finish the shared shader cache ([freshness](../reference/shaders.md#freshness)):
  no GitHub run has yet shown the restored Windows build, the silo image build
  and a repeated `shader-bytecode` job compiling nothing (`0 compiled`); a local
  cache evicts only when someone runs `puck shaders cache prune`; toolchain
  identity covers standard release locations, not every native-loader override;
  the cache laws' red legs are proved by hand, not yet through `puck laws prove`.
- Retire the JIT GC-hole workaround: `DirectXCommandListRecorder.RecordBackBuffer`
  is compiled with `NoOptimization` until the pinned SDK carries a JIT without
  the defect ([toolchain cautions](../development/contributing.md#hardware-and-toolchain-cautions));
  on each SDK bump, scan a running World's jitted methods for the pattern before
  removing the attribute. Reporting it to dotnet/runtime is the owner's.

### Maths

Owner: maths lane · ordinary · Next: the float duration fields' schema migration.

- Decide (the lead's call): Production `IsPrime`: whether a hashed-witness
  table may decide primes below 2^47 in one strong round. machine-prime's
  512 KiB table is why it leads Puck there (about 1.5x on primes); a table is
  capped near 32 KiB and judged cold as well as warm against a fixed small
  witness set ([primality](../reference/prime-exploration.md#primality)).

- Classify the public Maths surface the retired ratchet grandfathered without a
  law or waiver (algebra wrappers, coordinates, continued-fraction certificates,
  research APIs): bind existing evidence where it reaches the member, give
  precise category waivers where warranted, and delete unreachable code.
  Whole-surface completeness uses authored declarations, never a saved
  uncovered set.
- Eradicate duplicated mathematics and logic (the owner's ruling), never trading
  away ergonomics or performance; the first pass landed its shared kernels
  ([maths-usage](../../.claude/skills/maths-usage/SKILL.md)). What remains:
  - the callers adopt them: inline copies still stand in the simulation, SDF,
    presentation and tooling projects (the degree conversions, the two 2π values,
    the `v / length` clamps, the rounded-length range tests, the per-call
    cumulative scans, the hand-rolled wraps and packers); each adoption that
    moves a simulation value is a correction attributed in its own commit;
  - make the float duration fields decimal in one schema migration
    (`RigidRestHoldSeconds`, observer `UpdateSeconds` and its ceiling, flock
    `UpdateSeconds`, `Autonomy.MotionSeconds`/`SteeringSeconds`, motion
    `LatchSeconds`, transfer and topology `HoldSeconds`, timeline seconds,
    `ActionStateSlot.Initial` and its bounds, every
    `WorldSimulationTickConversion.DurationTicks(float)` caller), and carry
    segment durations as ticks or decimals on the wire (`WorldBody.EnqueueRun`
    converts `WorldCommand.EnqueueSegment.Seconds` through `FromDouble`);
  - one per-shape bound radius (`SdfProgram.TryGetLocalBound`, the certificate's
    `TryShapeSphere`, `SdfSolidGeometry`): they differ in input precision and
    slack, so merging re-derives the certificate slack proof or moves the packed
    GPU bounds;
  - contact programs do not scale a shape's `smooth` radius by the stamp scale
    while render programs do (`CreationStampEmitter.EmitFixed`); decide which is
    correct;
  - the oriented static box in `FixedSurfaceQuery` keeps the quaternion
    `RotateInverse` frame change beside `FixedOrientedBox`'s axis-dot frame;
    unifying them moves bits in one;
  - audit which projects still need `Puck.Hosting` now that `EngineTicks` is
    gone, and rename `WorldAuthorityCheckpointCadence.EngineTicks`;
  - a second whole-repository sweep (every project, non-maths duplication
    included) once the owner calls for it.
- Re-run the select probe on each new .NET major, and when dotnet/runtime pull
  request 134039 (borrow recognition for the compare-and-mask fold) merges or
  in-loop if-conversion lands: time one exact Lucas ladder of 61 steps above
  2^63 pinned to a core once tiering has finished, the final subtraction as the
  inlined mask, the non-inlined select and a plain ternary, and check the JIT
  listing for `cmovb` in the loop. Switch `ScaledResidueRing64.Subtract` to a
  ternary the day it wins and retire `SubtractBySelect`.
- Make the external prime comparisons reproducible as `puck bench` workloads:
  machine-prime 1.5.7 `is_prime` per bucket (2^32 to 2^63) and input class
  (primes, composites with no factor through 163 or 727, random odd words),
  checked against a deterministic Miller-Rabin oracle; Intel (Alder Lake) and
  Zen 2 timings are unmeasured.

### Dynamic body scale and the mesh bake

Owner: specialist lane (rendering, sdf-authoring) · one high from the owner's
walkthrough · Next: find where the dynamic (articulated) bake path keys and
bakes by world scale, since `SdfMeshInstance.Matrix` already applies `Scale` at
draw and a local-instance asset bakes once; extend that path to the body.

- Decide (the lead's call): a dynamic body's geometry and detail are defined in
  its creation space; its body scale rides the draw transform, bounds, normals
  and clipping, with no re-bake per scale and no avatar-specific exception; a
  detail tier by screen size comes later.

- [high] Crossing the Wonderland door (scale 0.25) misses the mesh cache for 180 groups of the Moth (`[sdf.mesh-bake] miss ... geometry new to its set`; 3679 dispatches, 13.4 M samples, 14.65 s) and the full-size meshes keep drawing until it finishes, so the Moth reads large and warped; after it the small Moth is visibly degraded: white slivers along every stripe edge of the back plates, lumpy squashed boots, ragged pads, because the bake's detail is set in world units and a 0.25-scale body gets a mesh four times coarser relative to itself. A dynamic body's bake must not depend on its world scale: bake in local space at its own detail, scale at draw, detail tier by screen size. Poses: `body.pose 6.16 0.07 30.43 23` at scale 0.25, orbit `view.override camera look 6.75 0.32 30.75 6.16 0.24 30.43` and `6.40 0.30 31.05 6.16 0.24 30.43`; captures w07-* in `.claude/scratch/walkthrough/` (the lead's machine). (from: owner walkthrough)
- Bake detail beside it: static placements' plain round shapes still bake at
  15 mm (`ShapeDetail` reads no round section); giving them `BodyDetail`'s
  curvature term rebakes the world, so measure its cost first
  (`puck mesh audit --cpu`). A dynamic group refines its whole bound toward its
  finest member: `SdfMeshBakeGroup.PlanGroups` records per-member regions only
  for static instances and merges every member's detail into one
  `DetailLow`/`DetailHigh` box (`src/Puck.SdfVm/SdfMeshBakeGroup.cs:310-321`),
  where per-member regions would let a thin member declare its own detail.

### Temporal shimmer and cadence flicker

Owner: specialist or Sol lane (rendering, P15 temporal reconstruction) · two
mediums from the owner's walkthrough · Next: isolate the temporal-off cadence
blink first (it pollutes the temporal-on reading), then record the rejection,
clamp and epoch causes at the given pixels to find why the history never
resolves the jitter on these surfaces.

- [medium] Standing still, the far wall of the level 3 gallery shimmers: over region 0.3 0.15 0.38 0.1, 28-34 % of pixels change every presented frame (max |dY| 121.6) with an exact 8-frame period (the jitter sequence) under `world.temporal on`, against 0.03 % under `world.temporal off`; history is likely rejected or clamped every frame on that speckled band (`body.pose 56.43 13.57 32.23 79`, eye (60.372, 15.8, 32.982) target (56.427, 14.57, 32.234); bursts w04-burst-on/, w04-burst-off/ in `.claude/scratch/walkthrough/` (the lead's machine)). The lift door threshold on level 3 shows the same, 10-16 % per frame (`body.pose 16.15 13.57 37.28 191`, eye (15.508, 16.895, 33.842) target (16.155, 14.57, 37.279)). (from: owner walkthrough)
- [medium] The lift door threshold also blinks with temporal off: pairs of frames about 11 presentations apart change about 3 % with max |dY| 26, then hold still, a cadence-driven refresh (the far shadow cascade's 30 Hz update, a GI update tier, or a nearby screen's refresh) independent of temporal reconstruction (bursts w06-burst-on/, w06-burst-off/). (from: owner walkthrough)

### Panes: split screens the player drives

Owner: specialist lane (puck-world views, rendering graphs, input) · ordinary · Next: after the bars-and-pick lane lands (it touches pane listing and pick), a machine-screen source for a pane. The owner's direction: a player can run as the Moth in one pane with the desktop in another, the HGD in a third and a fourth camera angle in another.

- What exists: `views` layouts of normalized `slots`, each the owning seat's view, a named authored `camera`, or a `views.graphs` `instance` (the studio's 2x2 `motion` layouts; `desktop.puck`'s `desktop` layout shows the `source.capture` graph in a pane); `view.override layout <name>` switches.
- A machine's screen as a pane source: the machines render as `source$machine$<id>` graph nodes, but the source packages a `views.graphs` row can name are `source.capture`, `source.passthrough` (with pointer and scroll) and `source.qr`, so no pane can show the HGD's picture. Add the package (find the existing machine-screen producer first, rule 7).
- Switching layouts as a player: a binding or controller-bar action that cycles or selects the world's layouts, not only the `view.override` console verb.
- Pane focus: one pane holds input focus and receives it (the seat's pad and keys drive the Moth until the player focuses the desktop pane, which then takes mouse and keyboard as passthrough, or the HGD pane, which takes the pad); focus is shown on the pane, moves by a binding or a click, and the in-world computer's programs (desktop and typing tutor) use the same focus.
- Decide (the lead's call): focus is simulation state, one pane at a time,
  moved by a binding or a click, so its transitions are deterministic and each
  input stream has one destination; the in-world computer's program switching
  reuses it rather than a second focus.
- Arranging panes at runtime (moving, resizing, choosing a pane's content) beyond the world's authored layouts: decide once focus and switching land.

### Screens on curved and general SDF surfaces

Owner: specialist lane (rendering, sdf-authoring, puck-world) · ordinary · Next: one projection end to end, the sphere the owner asked for (the authored projection frame on the renderer's exact hit, and the same projection in fixed point for the pointer), then cylindrical and cube. The owner asked for screens on a sphere's surface and on general SDF shapes.

- Decide (the lead's call): a screen on a shape is an authored projection frame
  evaluated on the exact hit, with the same projection in fixed point for
  pointer input; a UV atlas from the bake only if a shown shape defeats
  projection.

- Today a screen is a flat slab: a rectangle with right and up axes (`ScreenSlab`, `WorldScreenMappings.Normalized`, the screen validator's orthogonal basis); the CRT face kind only shades a curved look on that slab.
- Projection kinds beside the planar one: spherical (latitude and longitude), cylindrical and cube, so any shape's surface (a globe, a curved display, a pillar, a dome) shows a screen through an authored projection frame; the renderer already has the exact hit and normal on any shape.
- Pointer input on such a screen uses the same projection in fixed point in the simulation (a screen hit for input is computed there, never trusted from the host), so a click on a globe lands on the same picture point live and in replay.
- General shapes: a frame-projected decal first (gentle curvature); a UV atlas from the mesh bake only if projection proves not enough.

### In-world editing

Owner: specialist lane (puck-world, build and hot reload, input) · ordinary · Next: after the Edit to running packet's invalidation contract, the panes focus and the in-world computer's programs. The owner's direction: edit from inside the game, on an in-world screen or pane, and see the change hot reload.

- Record the code revision in replay tapes and checkpoints (`Puck.Abstractions.CodeRevision`); a replay or restore across a revision refuses with the reason, since rule 4 holds at a fixed code version and an applied simulation edit makes the run before and after it two programs.
- A rude edit (a new field, a signature the runtime refuses) resumes rather than reboots: checkpoint the World, rebuild, boot, restore, so the player lands where they were instead of at boot (today about 28 s and the place is lost).
- An editor program on the in-world computer (beside the desktop share and the typing tutor), on text screens, the in-world keyboard and pane focus, talking to a language server: `.puck` first (`puck lsp` exists), then C# through a Roslyn language server. Saving writes the file, the watch applies it, and the editor shows the outcome on its screen (applied, rude edit and resumed, or the compiler's errors).
- `.puck` edits in game are the sooner win: they are data, replayable and deterministic, and need no compiler.
- Development only: editing C# runs arbitrary code, so it needs the SDK on the machine and an explicit grant to the owner's seat; published builds do not hot reload.

### Debug world content

Owner: debug-world content lanes, each started by the owner's direction · one
medium, the rest ordinary · Next: the black slats behind the chess table now (a
material defect needs no taste call); new content and art on the owner's call.

- The faerie: the companion becomes small, abstract and original (nothing that
  reads as Ocarina of Time's) beside the moth, its animation rich, varied and
  reactive, dodging a swat through one generic threat input (a point and
  velocity, `companion.threat`) that a body or the seat's pointer drives today
  and a camera-tracked hand later. The owner accepted the seed-eye build as a
  first pass; the specifics come with the owner's visual-issues discussion.
- Camera as controller (the owner's stretch goal): the faerie flies through a
  portal into a camera world whose content a real camera drives through the
  probes system and which renders on the portal's face. Probes stay for it, with
  no expiry. A track advanced by the tick rather than the stopwatch would let a
  recorded track steer state deterministically (the `beacon` probe already
  drives presentation only).
- The Moth's look against the [concept pack](../game/art/moth-concept-pack/README.md):
  - keep rebuilding the suited Moth toward the mech-suit sheets 01, 02, 03 and
    10; three passes are in ([moth.md](../../worlds/debug/avatars/moth.md#geometry-and-motion));
  - rebuild her back thrusters to references 07, 08, 09, 12 and 13 (proportions
    from the straight rear view; each wing a hollow casing around a dark core,
    leaning in at the top, with domed shoulders; the shell a chain of separate
    band plates hinged at the top edge and swung out so each lower edge overlaps
    the plate below; crisp creases and soft domes; the opening facing in and
    down along a steep diagonal with a gunmetal trim and each nozzle flush and
    square to it; the spine a stack of chamfered blocks ending at the
    thrusters);
  - rebuild her boots as one form after `01-suited-turnaround.png` and the boot
    row of `02-construction-details.png`, judged by side, back and three-quarter
    captures at the sheets' framing (`body.pose 48 0 -48 0 0 0`,
    `view.override camera orbit 48 0.95 -48 9 <yaw> 0 14`). Today the cream
    shell bakes at 14.9 mm cells and the sole at 12.6 mm under 4 to 8 mm detail,
    the shell, sole, ankle hub and heel parts each bake alone so every junction
    is a hard overlap or gap, the sheets' flared lilac shin, cuff, hinged knee
    guard and recessed cyan heel light are missing, and the heel needs a housing
    designed around the downward nozzle the air jump's plume leaves (the bake
    detail these parts get is the
    [mesh-bake packet's](#dynamic-body-scale-and-the-mesh-bake)).
- The suit toggle's handheld: only body 0 holds the brick (`pilotHold` declares
  the one key `"0"`); she holds it only while captured standing still, so a
  mirrored engagement that walks or carrying it about wants a hold pose that
  blends with her gait; and the look's glass shell reads near white in her hands
  where the rest's brick reads orange in the same run (a pooled creation's
  glass may lose its tint).
- The board game room: the owner reviews its look (the tables share the chess
  table's wood, the Go board a flat kaya face, the Reversi board green felt);
  more games for the free table spot at (65, 30.6), and addon seats at the
  tables.
- An elements-and-combat yard toward MMO action combat
  ([combat decision](../decisions/engine-design.md#combat-is-world-content-on-generic-queries-fields-and-input);
  the quarantined `arena.puck` district is the rules' prior art):
  - spatially scoped pair queries on `WorldInteraction.Distance` over
    `FixedSpatialNeighborhood`, replacing the Cartesian scan, reused by targeting
    and area effects;
  - action input on the 32-axis, 64-button wire through the ordered events,
    history and patterns (C3 to C6): an eight-slot hotbar with pad and keyboard
    parity, dodge and target lock, cycle and clear; audit `AddonAbi.MaxChannels`
    (eight protocol descriptors) before widening it;
  - the yard itself, `worlds/debug/combat.puck` (provisional site the north
    courtyard at x 24 to 50, z 40 to 66 through a level-0 doorway), with a
    target-and-strike station, an elements station (brazier, basin, frost source)
    and terrain hazards, world-authored statuses and looks, a combat-only HUD,
    `debug.go combat`; targets are training automata and the Moth casts through
    her gauntlets (the owner's ruling);
  - multiplayer: two clients on one authority with input acknowledgements and
    speculative local movement and action timing replayed on correction, then
    indexed entity and field subscriptions for interest management.
- Weather, particles and a climate wing (the owner asked about rain, snow and
  sand; [weather decision](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces)):
  six sealed, glass-walled bays east of the hall (provisional annex x 72 to 122,
  z 12 to 28): calm, rain, snow, sand, ash and embers, each a local region with
  start, stop, intensity and reset. Slice 1 (named regions) is in; the rest, each
  finished before the next:
  2. the annex shell with the calm and rain bays: directional blocker maps (glass
     lids block), the demand-sized particle pool, the wetness coating, puddles
     from fields and basin masks, runoff; rain wets, fills and quenches, and
     stopping clears through drying;
  3. analytic particle quads after the opaque resolve, ordered glass panes,
     regional media through shared depth segments, endpoint bloom, with both
     backends' readings and edit-to-running for C#, raster and compute edits;
  4. supported-surface cover, grounded track stamps, depth resistance, and the
     snow bay (deposit, tracks, slowing, melt, freeze);
  5. wind erosion, lee drifts and wet cohesion, dust haze, and the sand bay;
  6. shared world recipes (rain quenches and fills, fire melts snow, cold freezes
     water, fire and sand make glass), the ash and embers bays, all six cycling
     independently;
  7. the combat yard and the hall's window band on outdoor regional weather;
  8. all six bays active at a steady 60 FPS on the RTX 4070 at the highest
     settings on both backends, replay hashes identical across quality tiers,
     then the Steam Deck.
- "Puck Beats HGB" (the owner's goal): a generic construct that learns and beats
  arbitrary HGB games on the machine debugger addon, possibly stretching the
  embeddings system. Training runs offline (a verb or harness, since float
  optimisation cannot live in a deterministic guest), the learned policy frozen
  into an addon that is a pure function of its inputs; start with one committed
  test cartridge with a known win condition.

### Agent tooling and skills

Owner: the lead · ordinary · Next: the Monitor hook check.

- The agent-policy hook may not cover Claude Code's `Monitor` tool:
  `.claude/settings.json` matches `Bash|PowerShell`, and whether a Monitor
  PreToolUse payload carries `tool_input.command` is unverified. Log one
  payload (a `*` matcher writing it to a file); if it carries `command`, widen
  the matcher to `Bash|PowerShell|Monitor`.
- Repair the eval cases whose with-skill and without-skill scores differ by under
  0.1 (noise), each by inspecting the prompt, grader and replies before touching
  the skill ([evaluating a skill](../development/documentation.md#evaluate-an-agent-skill)):
  puck-world `w07-failed-build-stale-binary`; orchestration
  `o05-partner-message`, `o07-write-review-into-a-gpu-tree`,
  `o11-second-round-findings`, `o16-brief-prewrites-the-design`; review-passes
  `r06-reviewer-claims-absent-from-the-tree`, `r07-write-pass-cannot-build`,
  `r09-range-given-as-two-dots`, `r21-write-pass-while-a-run-builds`; rom-forge
  `f05-world-rule-refuses-cartridge-forms`; verification
  `c08-two-dot-is-not-ancestry`. Then re-run every skill the instruction audit
  edited with `--ablation with-without`, including orchestration cases o33 to
  o40.
- Close the graders that still fail with the skill loaded: puck-world w03
  (`bom-less-utf8`, `pixel-versus-replay-proof`, `announcement-on-stderr`) and
  w04 (`compiled-in-memory`, `folds-through-the-rule-frame`), and verification
  v08 (`citations-boots-world`) and v12 (`shows-by-running`). Decide for each
  whether the skill lacks the teaching or the case is wrong, fix that, and rerun
  the suite with a budget that judges every run.

### Quarantined gates

While the debug world is the one live world
([decision](../decisions/engine-design.md#one-live-world-while-the-campaign-runs)),
these gates have no live fixture. Their fixtures, laws and ledgers sit at their
repository paths under [`experimental/`](../../experimental/README.md), and none
runs until the owner calls for qualification; then each is a packet.

- Real-World canaries (`puck canary`, `experimental/tests/Puck.World.Canaries`):
  the behavioural proofs of the World's load-bearing seams on the real
  executable, the one gate for game behaviour. With no canary directory,
  `puck canary`, `puck affected` and `puck gate` select an empty set. The
  coverage map waits at `experimental/tests/Puck.Affected/canary-coverage.json`.
  When they return, the runner's listener work finishes: a single companion
  authority still receives a pre-picked UDP port, because its document names its
  own endpoint as `host.authority` before it boots (the World would adopt its
  bound `--listen` endpoint when `host.authority` is absent), and the
  stub-manifest branch still builds `Puck.Launcher.Stub` in place.
- Cross-backend parity (`puck parity`, `experimental/tests/Puck.Parity`), with its
  path-sampling and binding-graph contracts; its return is the
  [parity packet](#cross-backend-parity-and-gpu-robustness).
- Cross-host determinism (`puck determinism`,
  `experimental/tests/Puck.Determinism/determinism.json`): every scenario's tick,
  system and document hashes. CI's Windows recording and Linux comparison are
  deleted.
- Counted render work (`puck counters`): the floor workload (`counters.puck`), the
  Nexus and courtyard workloads, the four sky workloads and the sky-default
  matrix, under `experimental/tests/Puck.Counters`. The verb's defaults are the
  debug world's shadow workload, and the seated-avatar workload stays live. The
  dynamic-resolution step budget is a constant derived from the floor
  workload's last recorded counts (`WorldDynamicResolution.StepBudgetPerPixel`).
- Release qualification's functional canaries and its island and ink-pipeline
  workloads; the live profile (`tests/Puck.Qualification/release.profile.json`)
  runs only the debug world's stability matrix on both backends.
- World baselines: `state` (each shipped game's canonical state export and tick
  cost) and the tictactoe state hashes of `browser-parity`; `browser-parity`
  itself stays registered over its fixture document.
- `puck test` verdict worlds (`experimental/tests/Puck.World.Verdicts`) and the
  test blocks the quarantined worlds carried.
- The laws that booted a quarantined world or fixture, each moved to
  `experimental/tests/<suite>/`. `puck bench startup`, CI's published-host
  startup check and `puck bench world` boot the debug world; `puck world
  prepare`, the silo image, the Azure release and `puck official build` start
  from it, and production hosts it as `debug`.
- The dashboard portal's real-engine tests (`engine-wasm.test.cjs`,
  `native.test.cjs`) compose no world while the debug world is the only live one;
  they return when the live tree authors a fragment and a basis.

### Awaiting the owner's review

Ideas discussed with the owner and not yet queued. None starts until the owner
reviews it and says so (the owner's ruling).

- **Ground cover and vegetation.** Forests are authorable today (a tree creation
  with a `Noise` or `Scatter` placement distribution under the one-pixel rule),
  but dense ground cover is a gap: presentation-only GPU instancing of grass and
  low plants placed by density masks from terrain material, slope and height and
  fading into the ground material; wind motion by vertex displacement; billboard
  or impostor tiers for distant forest; bending and trampling through the weather
  decision's track layer; terrain material blending by slope and height.
- **Tonemapping.** One curve exists (`render.tonemap` `Filmic`: Uchimura's Gran
  Turismo fit, per RGB channel, SDR peak 1.0, in `place.comp.hlsl`), and the
  debug world authors none, so its sky, sun and emissives clip at white.
  Proposed: Khronos PBR Neutral for the gallery as an inspection tool, a
  luminance-based filmic option that keeps hue, the choice per world and camera
  like bloom, a colour-grade look as a world's or camera's material, and a
  display-aware curve for HDR10 and scRGB once an HDR display can verify it (the
  lead machine's Dell S2722DGM is 8-bit SDR, 240 nits). The default curve and the
  gallery's look are the owner's call.
- **The bloom look.** The debug world authors scatter 0.04 and radius 0.08 for the
  owner to judge in the World.
- **A maths room** in the gallery (tabled by the owner): a 2D grid board and a 3D
  floating lattice whose cells `Puck.Maths` computes in the simulation. The
  audience, animation, interactivity, which maths leads and where it goes are the
  owner's questions; per-cell variation within the shape budget, computing in
  rules with new builtins rather than an addon, and a per-tick update's cost are
  the technical ones.
- **An optional self-restricting addon** (the owner's idea): it detects whether a
  player is automating their gameplay with Puck, identifying the game from a
  catalog such as SteamDB and judging through the addon system's observation
  grants whether Puck's automation (input driving, the machine debugger, Puck
  Beats HGB) acts on it. The catalog is external data outside simulation state,
  and the addon is opt-in.

## Plans

Each programme's phase-scale work, with its packages' remaining parts, lives in
its plan's open items.

- [State and language](state-and-language.md#open-items): C1 ceilings as prices,
  the state-evidence recapture, S6, S7, S9's waves 2 to 4, S10, module
  namespacing and the deferred games.
- [Runtime and delivery](runtime-and-delivery.md#open-items): the world-source
  migrations, asset packages, the ledger, compiled worlds, the presentation
  view, release pairs and the evidence package.
- [Owned memory for Puck's own host](memory-pooling.md#delivery-packages): M1 to
  M3, then R1 to R3; planning work, implementation not started.
- [Machines and cartridges](machines-and-cartridges.md#open-items): the
  programme's packages, the Humble corpus failures, committed coverage for the
  commercial-ROM stages and the emulator batteries' cost; the
  [Humble Gaming Deck](humble-gaming-deck.md#open-items): accuracy, boards,
  cabinets, the disk system.
- [Play](play.md#open-items): the finder, MCP's surfaces, the population, the
  content and federation waves, housekeeping.
- [Rendering](rendering.md#open-items): the frame-rate bar on the RTX 2060 and
  the Steam Deck, the mesh bake and its defects, glass, GPU capture findings, P1a
  to P20's remaining parts.
- [UI](ui.md#open-items): six steps and the text step.
- [Editor](editor.md#open-items): E2 to E4, E6 to E8, E11 to E14's remainders.
- [Milestone 1 acceptance laws](acceptance-laws.md#open-items): law 6's
  arrangements and the variants of laws 2 to 5.
- [The valley, the moons and the flight between](valley-and-moons.md#phases):
  eight phases from a measured baseline to the round trip.
