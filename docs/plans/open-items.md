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

1. [Frame time on the lead machine](#frame-time-on-the-lead-machine): with the
   display lit the debug world holds p50 16.67 ms at every stop, but a frame
   carrying a 30 Hz tick (8 to 14 ms) misses the blank; cut the tick's cost on
   the frame thread.
2. [Lighting and GI](#lighting-and-gi): the remaining local and far-field
   coverage, enclosure, floor and wrapped-diffuse sun findings; the quiet trace
   timing is settled (Vulkan matches Direct3D 12).
3. [Bake detail](#bake-detail): measure static curvature refinement and local
   dynamic-member refinement; the remaining scale-normal finding is in
   [Lighting and GI](#lighting-and-gi).
4. [Edit to running, hot reload and boot](#edit-to-running-hot-reload-and-boot):
   a hot-reload code revision as one invalidation contract (emitter revisions
   and the static field), then the persisted caches keyed by the modules as
   built; a product of edited code under the unedited identity is the hazard.
5. [MCP and the operator attachment](#mcp-and-the-operator-attachment): the
   cached wait refusal (`OperatorMcpFront.Record` caches any non-pending wait
   answer), then the schema switch.
6. [Test runner and replay](#test-runner-and-replay): the batch loop's one-line
   break (`WorldTestBatch.cs:89`), then delete complete-boot reuse and make the
   per-tick authority hash incremental, as the quiet measurement supports.
7. [Card tables in the board game room](#card-tables-in-the-board-game-room):
   the owner's direction; Klondike first on the revived rules and SDF faces,
   headless, its look in the lead's window.
8. [Residency and streaming](#residency-and-streaming): the measurements, then
   near-first ordering of bake, store loads and landing; the card tables are
   its test case.
9. [Collision versus drawn mesh](#collision-versus-drawn-mesh): re-run the mesh
   comparisons on the corrected rounded-Box diagnostic, then the surviving red
   findings by cluster; the reds fail every sweep.
10. [Debug tools](#debug-tools): a pick that addresses the presented view, lists
    it in `panes` and answers under pause, then the diagnostics' windowed checks
    and the developer exhibits.
11. [Sweep and visual checks](#sweep-and-visual-checks): fix the prelude (the
    chess toast, the refused `world.wait 300`), reproduce the companion overlaps,
    then one paired windowed tour when no lane holds the GPU.
12. [Temporal shimmer and cadence flicker](#temporal-shimmer-and-cadence-flicker):
    qualify the original temporal-off blink at a quiet refresh cadence;
    reconstruction now converges on both backends.
13. [Simulation tick off the frame thread](#simulation-tick-off-the-frame-thread):
    the thread itself, a design consensus the owner has yet to queue.

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

- [medium] The windowed `--tour` does not repeat to its 2 % threshold on a long-running World: an identical re-run of the same build on the same World put 17 stops past 2 % (wing-door-far 99.3 %, revolved-floor 41.7 %), and the GI counters differ with World history (`sdf.mesh-gi.camera-traces` 304 against 573), so a cross-build comparison through it cannot separate a change from lighting history. Settle the lighting state before each stop (indirect and exposure converged, a fixed camera-trace history) or compare against a baseline taken in the same World session. (from: bake-scale lane)

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
- Temporal-shimmer's required cross-family renderer review and quiet-machine
  frame/memory qualification remain with integration. Its C1 source captured
  all 70 tour stops across three bounded Direct3D runs against the 64-stop base;
  45 exceed 2 %. Every name, percentage and inspected explanation is in the
  lane's `tour-final-comparison-notes.md`. New valley geometry, changed motion
  ticks and a held-lift baseline explain some differences; broad static-lighting
  differences remain unisolated. A same-World shader comparison at
  `gather-teal-door-far` changed only 0.008941 % beyond 16 RGB levels between
  original and current resolve, so that sampled broad lighting change is not
  explained by the resolve alone. This is not attribution for every stop.
  The final merge of `lead/catch-up` at `45d6a960c` adds five granary stops beyond
  that measured plan. Rerun the integrated tour, standing/walking frame readings
  and gallery/lift body patrol. History grew from 6 to 47 uint words per output
  pixel: 164 extra bytes per allocated history surface, or 180.18 MiB at
  1280x900. Measure the actual view/slot allocation; the loaded paired shader
  readings establish no quiet-machine 60 FPS or VRAM qualification.

### Frame time on the lead machine

Owner: simulation and host performance specialist · high · Next: take the 30 Hz
tick off the frame-time budget (its cost first, by a thread-time trace of the
rim's tick, then the [simulation tick](#simulation-tick-off-the-frame-thread)
packet), and re-read these stops on a quiet machine.

- [high] Steady 60 FPS is not held: frames that carry a tick miss the vertical
  blank. The 7 to 8 FPS readings were the display powered off (the
  [hazard](../development/contributing.md#hardware-and-toolchain-cautions); the
  window now holds a display request). With the compositor running, a windowed
  Direct3D 12 World at 1280x900, highest settings (shadows high, temporal on,
  native scale), `world.lockstep off`, reads p50 16.67 ms at every stop, and
  over 20 ms in 34 of 585 iterations at the lobby (p99 25.0), 62 of 581 at level
  1 (p99 25.9), 20 of 593 at the boardroom (p99 24.5), 32 of 589 at the cards
  (p99 25.1) and 218 of 534 at the valley rim (avg 18.7, p99 28.9); the machine
  carried other lanes' builds (50 to 75 % CPU). Each slow iteration is one
  fixed step of 8 to 14 ms (about 12 ms at the rim) plus produce of 6 to 7 ms on
  the frame thread. The GPU is not the limit: at P0 the lobby's frame is
  `world` 8.0 ms, the session views about 2 ms together and `beacon-eye`
  0.4 ms (`world.gpu-timing timeline`). Vulkan under the same load reads avg
  17.5 ms at the lobby and 19.8 to 26.5 ms elsewhere, with produce 8 to 11 ms
  against Direct3D 12's 6 to 7. (from: frame-time)
- [low] A reading taken while the compositor is not composing still looks like
  a slow frame. `GUID_CONSOLE_DISPLAY_STATE` read "off" for whole readings at
  60 FPS, so it cannot flag one; a present-side signal (the frame-latency wait
  reaching its bound, or the queue idle behind the back buffer) would. The
  Linux windows hold no display request (Wayland's idle inhibit, X11's
  screensaver suspend). (from: frame-time)
- [low] `WorldGpuTiming` (`world.gpu-timing` and its `timeline`) has no CPU
  test over a fake runtime, so its lines rely on a live render root.
  (from: frame-time)
- [low] The hall lift ride's `host.frames` (owed by the sim-bounds lane) is still
  unread: `gallery.level 0` stands her before the door, not in the car, so the
  reading taken (`gallery.lift 3`, 300 ticks: 128 ms average, simulate up to 4
  steps and 44 ms) is the lift travelling without her. Pose her inside the car
  first. (from: quiet-measure)

### Lighting and GI

Owner: next lighting batch (rendering, principal-grade review) · Next: remaining
coverage, enclosure, corner and exposure findings; quiet GI qualification at integration.
The [mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
holds the fixed poses and the approaches already rejected.

- [medium] After bake-scale, member normals stay unit length while root normals receive the inverse scale, so a triangle spanning root and animated-member vertices interpolates a lighting that changes with body scale (at 0.25 the root normal weighs four times the member's) (`src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-mesh.hlsli:150`). Likely the cause of the thin bright lines under the quarter-size Moth's back-plate edges filed by the same lane: normalize both after the scale, or apply the same transform to both. (from: bake-scale review)

- Owner walkthrough, depth visibility: both poses checked on probe-depth-fix's
  settled Vulkan build. At (1) `body.pose 66.24 4.57 14.73 329`, eye
  (64.061, 5.331, 18.308), target (66.241, 5.57, 14.726), the original large
  corner zig-zag and ceiling saw-tooth are no longer visible, but ceiling
  hotspots remain and the wall clips white in `indirect`; this is not a clean
  exposure qualification. At (2) `body.pose 58.17 4.57 30.87 180`, eye
  (58.16, 8.403, 29.767), target (58.168, 5.57, 30.868), hard diamond/star
  patterns remain around the NOISE and ERODE plinths in `indirect`. Whole
  octahedral texels switching was a hypothesis, not an established cause; the
  current local reconstruction still needs this case isolated. Original
  captures w01-*, w02-* are in the lead's walkthrough scratch; the new
  `owner-corner-vulkan` and `owner-noise-erode-vulkan` captures are in the
  probe-depth-fix hand-back. Keep the floor finding open.

- [low] A quarter-size Moth (Wonderland) shows thin bright lines under her back
  band plates' lower edges that the full-size Moth at the same relative framing
  does not, though both draw one mesh (`world.debug-view normals` matches): a
  shading length in world units does not follow a body's scale (candidates: the
  cascades' depth bias and texel, the surface pass's AO and shadow tap radii,
  `SdfMeshIrradianceProbe.NormalBias`). Pose `body.pose 6.16 0.07 30.43 23 0 0`
  at scale 0.25 with `view.override camera look 6.40 0.30 31.05 6.16 0.24 30.43`;
  the full-size twin is `view.override camera look 7.12 0.99 32.91 6.16 0.75 30.43`
  at scale 1. (from: bake-scale)

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
- Probe depth visibility (from the probe-depth review):
  - [x] [high] Depth visibility leaks at every silhouette: a ray that misses stores
    `min(reach, DepthMaximum = 128 m)`, about forty times the farthest receiver
    a probe serves, and the `distance <= mean` early-out returns 1 before
    Chebyshev runs, so any texel whose bilinear footprint touches a miss admits
    every receiver behind the occluder (one missed tap at 2 % weight gives a
    mean near 3.5 m). Failing case: the teal-room roof's eave
    (`src/Puck.SdfVm/SdfMeshProbeDepth.cs:20,45,84`,
    `sdf-mesh-irradiance.hlsli:82`). Store misses at about 1.5 times the
    spacing diagonal or flag them, and take the sky from rays that still run to
    full reach.
  - [x] [medium] Each probe's first sweep casts 1,024 closest-hit depth rays to the
    full lighting reach, eight times the 128 radiance rays, because the sky cube
    is now measured from them; depth needs about one ray per texel bounded to
    the gather radius, and the radiance rays already compute a sky result that
    is overwritten (`SdfMeshProbeDepth.cs:30-32`, `SdfMeshIrradiance.cs:917-919`).
    The likely cause of the trace-time rise below.
  - [x] [medium] Zero-weight gathers became common (Chebyshev returns exactly 0 at
    zero variance with no floor, the facing term lost its `+0.2`, tiny weights
    flush to zero on the GPU), and a receiver whose eight weights are all 0 falls
    back to `sdfMeshLightVisibility` 1.0, fully lit by every traced static light:
    a point light in the next room shines through a flat wall
    (`sdf-mesh-irradiance.hlsli:142,202-204`, `SdfMeshIrradiance.cs:1250`).
  - [x] [medium] Depth visibility raised the GI trace time and reserved a 38.67 MiB
    probe table at every tier. Bounded depth work removes 85.94 % of its rays.
    Adjacent loaded-machine pairs: Direct3D 12 High 11.487 to 7.437 s, Medium
    1.698 to 1.032 s, whole-protocol process CPU 102.65625 to 75.9375 s;
    Vulkan High 15.527 to 13.247 s, Medium 1.932 to 1.123 s, CPU 97.625 to
    75.421875 s. Radiance work matches. One table/volume now fits its tier:
    11.02 MiB Medium, 33.05 MiB High. Actual retained uploads are reported
    separately; whole-process private memory does not uniformly decrease.
    Raw pairs and allocation diagnostics are in the probe-depth-fix hand-back.
  - [x] [medium] The probe-depth lane's Vulkan captures (in the lead checkout's scratch)
    still show small dark marks at mid-height on the stairwell's east wall
    (`own-vulkan-stairwell-lit.png`, the black squares the owner reported), 2
    m-scale blotches on the boardroom floor and a bright vertical streak on its
    right partition (`own-vulkan-boardroom-indirect.png`). The sphere reads clean.
    Closed by explicit misses and compatible local surface reconstruction, a
    distance-based hemisphere fade and shared radiance quadrature. The final
    `matched-directx` and `capture-vulkan` fixed lit/indirect pairs in the
    probe-depth-fix lane's scratch were individually inspected: the broad roof
    band, repeated floor poles, thin far-corner streak and stairwall squares are
    gone; the sphere stays continuous. The final Vulkan stair tread has a sharp
    dark bow-tie patch, recorded separately below. The separate dim-enclosure
    and grazing corner findings remain open.
  - [x] [low] The sky share is measured over the 1,024 depth rays while bounce
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
- [medium] The controlled final Vulkan stairwell lit/indirect pair has a sharp
  dark bow-tie patch on a middle tread, absent in its baseline. The original
  east-wall squares are no longer distinct, but the enclosure is darker.
  Isolate geometry/moving bake from probe interpolation before attributing the
  tread patch. All requested captures report settled bakes and irradiance;
  the Vulkan sweep nevertheless flags the cumulative moving-bake cap counter
  increasing from 3 to 41. Its cause is unisolated. Evidence:
  `capture-vulkan/report.json` and `tour/probe-stairwell-*.png` in the
  probe-depth-fix hand-back.
- [medium] Far-field irradiance coverage: bounded local depth support exposes
  unmeasured distant surfaces beyond the camera's probe window. The matched
  probe-depth-fix tour shows black far walls at `finding-teal-roof`,
  `gallery-entrance`, `devices`, `desktop`, `rate-range`, `camera-course`,
  `stage`, `jump`, `flight` and `noclip`, and a larger dark pane through the
  `garden` return portal. Several gallery/window views show the same far wall.
  Live Vulkan readback confirms one authored south-wall receiver (69.5, 4, 12),
  normal (0, 0, -1), answers with weight 0 although its boundary probes at
  x=31 are bright and reference-clear=1. Their roughly 38.5 m separation is
  beyond the 5.246 m local support. The near receiver (18.5, 4, 12) answers
  with weight 0.525. The far wall also reads black in a fixed indirect view;
  this isolates missing coverage for that receiver. Collision probing was
  unavailable in that World's contact configuration. Supply coarse far-field
  coverage while retaining local visibility. Not every changed tour pixel,
  nor the garden return's scope/history, has been separately isolated.
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

Owner: test runner and replay specialist · medium and low ·
Next: the batch loop's break (`WorldTestBatch.cs:89`, one line), then delete
complete-boot reuse and make the per-tick authority hash incremental (both
measured below).

- [low] `Fixtures.FreshServer` (`tests/Shared/World/Fixtures.Server.cs:112`) deserializes without `WorldStateSettlement.SettleBeforeAdmission`, so a boot from a raw document with a seeded responsive placement can show the response's prototype while colliding as the authored one, which the real loader never does. Settle in the fixture as the loader does. (from: the tip-reds lane)
- [medium] A deep tree in a kept `%TEMP%` run directory crashes every later test
  process: `RunDirectory.Sweep` deletes stale `puck-*` directories with .NET's
  recursive `Directory.Delete`, which overflows the stack past about 3,500
  levels (a launcher-pruning depth law's red run left one at 5,000). Delete with
  the pruning lane's iterative, link-refusing confined removal (not yet landed
  here), and have the sweep refuse a directory it cannot remove rather than
  crash. (from: the
  prune-redesign lane)
- [medium] Delete complete-boot checkpoint reuse and keep the warm worker
  (decided on the quiet measurement). Under the complete identity no two
  scenarios of a file share a composed document: alchemy boots 8 hosts for 8
  scenarios, Go 15 for 15, and boot-reuse-2's suite 83 for 84, so restoring the
  pristine checkpoint saves at most one warm boot (about 3.3 s) per suite run.
  What does pay is the worker process staying alive: its first boot is 8.5-9.2 s
  and every later boot in it 3.1-3.5 s. The reuse path still costs on every run:
  the coordinator composes and canonicalizes every staged `.world.json` of every
  scenario to compute `BatchShape` (`src/Puck.Cli.Worlds/Test/TestCommand.Batch.cs`),
  part of the 30-55 s of coordinator preparation per file below, and the
  scenario-restore machinery (`WorldPersistence.ScenarioCheckpoint`,
  `WorldInstanceHost.RestoreScenario`, the seat-route and input-timeline resets
  in `WorldTestBatch.StartLeg`) carries the hazards the verification skill lists.
  Boot every scenario fresh in the warm worker, delete the shape comparison and
  the scenario restore, and re-time alchemy and Go. Quiet readings (load under
  15 % before each run, about 25-38 % during, the run's own work), `--jobs 1`,
  whole-process-tree CPU by job object:

  | File, mode | Runs | Wall s, median (spread) | CPU s, median (spread) | Host boots |
  |---|---:|---:|---:|---:|
  | alchemy, ordinary | 3 | 113.1 (113.0-115.8) | 208.9 (205.6-209.0) | 8 |
  | alchemy, `--reproduce` | 3 | 214.5 (213.9-215.8) | 351.2 (350.8-353.1) | 8 |
  | Go, ordinary | 1 | 243.2 | 515.7 | 15 |
  | Go, `--reproduce`, traced | 1 | 604.7 | 1,065.0 | 15 |

  Chess, the whole-suite `--jobs 1`/`--reproduce`/`--jobs 2` runs and two more
  Go runs were not reached in the measurement window. (from: quiet-measure)
- [medium] `--reproduce` costs about twice the ordinary run, and the cost is
  per-tick hashing and recording, not host reuse. Recording slows the authored
  phase itself (alchemy's 1,204-tick scenario 14.0 s ordinary against 20.1 s
  recorded; Go's 2,804-tick scenarios 15 s against 31 s under the trace), the
  replay then repeats it (replay time equals recorded authored time), and a
  replica has a fixed 2.4-4 s floor even for a 16-tick scenario. A
  `dotnet-sampled-thread-time` trace of Go's reproduce worker (481 CPU-s on the
  tick threads) splits it: `WorldReplayProbe.Verify` 51 %, the authored steps 49 %;
  `WorldPersistence.AppendStateHashComponent` 36.5 % of all of it
  (`WorldPopulation.AppendContinuationHash` 16.4 %, `StateArena.ComputeHash`
  13.9 %), `WorldReplayTape.NoteTick` 20.7 % (with
  `WorldAuthorityCheckpointCodec.WritePopulationEntry` 10.3 %), the session
  sinks' snapshot delivery to the garden and night mirrors 16.7 %
  (`WorldDocumentStateView.ApplyFieldCells` 11.4 %), replica arena construction
  and load 7 %, and the simulation itself (rules 13 %, bodies and contact sweeps
  about 15 %) the rest. Make the per-tick hash incremental (fold only changed
  arena columns and changed body continuations), and find whether a headless
  test worker's session mirrors have any reader before delivering to them;
  then re-time Go.
  Raw: `.claude/scratch/quiet-measure/` in the quiet-measure worktree (ignored).
  (from: quiet-measure)
- [medium] Hall's test fixture uses `documentId: "hall-tests"`, so the garden
  and night references admit a separate full `debug` authority. Its root's
  stopped-machines import does not stop that companion's six machines;
  each root tick advances four authorities. Reused Hall scenarios reach their
  first completed tick in tens of milliseconds, and Alchemy's one-tick cases
  spend 53–154 ms in authored work excluding export: the measured seconds are
  not a fixed three-second authored startup delay. Have the scenario-fixture
  lane remove the avoidable companion workload while preserving the scenarios'
  claims, then measure its CPU contribution. (`worlds/debug/hall-tests.puck`;
  from: boot-reuse profiling; the machine contribution is not isolated.)
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

- [high · owner: lead integration] The combined debug document at integration
  45d6a960c is refused by `puck compile worlds/debug/debug.puck --validate`:
  PUCK030, `chessAI` has 1,037,029 work units available but needs 1,075,835 for
  progress. This appears after the card-table merge and blocks the latest
  probe-depth-fix World smoke qualification; the preceding integrated build
  booted on both backends. Reconcile the composed recurring work and search
  allowance without disabling chess. The focused probe/tooling builds and
  96 cases still pass. The diagnostic is in the probe-depth-fix hand-back's
  `integrated-latest-debug-compile.log`.

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
- HLSL on a quiet lead machine is inside the 30 s ruling: in a windowed
  Direct3D 12 World, a new constant in `passes/sdf-light-stage.hlsli` went from
  save to `world.shaders.reload` answering `state=applied` (`sdf-world-views`
  alone) in 8.04 and 8.24 s, harness round trips of about 1-1.5 s included; a
  revert to bytecode already in the shader cache took 2.65 s. The C# loop
  (`dotnet watch`, windowed) was not reached in that window and is still owed.
- Re-measure the C# loops on an idle machine against the targets: a
  `Puck.World.Client` edit to ready under 20 s, boot to ready under 6 s. On the
  loaded lead machine a hot-reloaded
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

Owner: specialist or Astra design lane · ordinary · Next: the thread's design
consensus, once the owner queues it.

- [low] The pump now sheds owed steps once a burst spends a step's period of wall time, but nothing counts the shed steps: add a drop counter to `host.frames` (and `world.rate`) so a World running slower than real time says so. Owed in the lead's windowed window: a hall lift ride's `host.frames` with frames near one tick's cost. (from: sim-bounds lane)

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

- [medium · owner: lead verification] Finish the controls visual checks in the
  lead's windowed batch. The owner's controls brief makes this a GPU-free lane:
  the earlier extraction comparison is owed alongside the new zoom/reset,
  dash lean and thruster flare, all bar layouts, wings and compact/full stills;
  it is no longer a prerequisite to implementing those controls. Headless
  readbacks pass, all 23 input scenarios reproduce with their original 84
  verdicts/export ticks, and the three dash scenarios reproduce including thin
  wall contact. Run the saved before/after extraction scripts and the unrun
  camera, bars and dash scripts under the controls worktree's
  `.claude/scratch/controls-evidence/`; inspect the same workload on Vulkan,
  read standing/walking frame time and patrol the stage. The hand-back is
  .claude/scratch/controls-handback.md (lead checkout, ignored). Judge the dash's
  feel and the authored secondary wings spacing; their appearance remains
  unconfirmed. The durable
  controls and commands live in the [standard package](../../worlds/standard/README.md)
  and [Moth controls](../../worlds/debug/avatars/moth.md#controls).
- [high · owner: lead integration] Land the controls review's follow-up
  branch. It fixes R1 (high): the sliced analytic sweep measured each slice's
  edge rule from that slice's start, so a body lowered onto a nearly flat fillet
  read it as a riser (`RoundedBoxContactLawTests.ANearlyFlatFilletBelowTheFootRemainsWalkable`
  failed on the landed lane); R2 (medium): it sliced and spent the 64-slice
  budget in clear space, so a small fast body stopped short and read `Unproved`;
  R3 (medium): `player.camera.boom` refused input while a wheel was open, an
  engine policy duplicating the standard's hold-page bindings. The high blocks
  the owner's main push until this lands.
- [low · owner: play-feel lane] A timed impulse integrates only inside
  `IntegratePlanarAndVerticalVelocity` and `IntegrateScratchVelocity`
  (`WorldBody.Step.cs`, `IntegrateOverlay`). On a body whose motion program has
  neither op, `planarImpulse` neither moves it nor drains `m_overlayRemaining`, so
  the burst waits and fires when the body next runs an integrating program.
  No shipped program lacks one (`walk`; `rangeLoop` is a producer). Failing case,
  unrun: a kit whose program has no integration takes a rule's `planarImpulse`,
  then selects a walking kit. Drain or refuse the overlay there.
- [low · owner: play-feel lane] `dashPose` (`worlds/debug/avatars/moth.puck`)
  is one cell keyed `"0"`, so only body 0's dash drives the torso lean and pod
  flare; a second seat's Moth dashes unlit. `airPose`, `airWings` and
  `bootBoost` share the pattern; key all of them per body together.
- [low · owner: play-feel lane] `player.binding-bar model <value>`
  (`WorldBindingCommandModule.BindingBar.cs`) stores any text, and every value
  but `single` stacks (the `ModelCell` contract): `player.binding-bar model
  singel` echoes `model=singel` and `world.binding-bar` reads `stacked true` on
  a headless debug World. Refuse an unknown model by name.
- [low · owner: play-feel lane] A requested boom distance replaces the orbit
  op's distance outright (`SdfCameraProgram.Evaluate`, `look.Distance ??
  orbit.Distance.Resolve`), and `WorldSeatViewState.AuthoredDistance` reads the
  first orbit op's literal. A scalar-bound orbit distance stops responding after
  the first scroll; the Moth's is a literal 4.2. Unconfirmed in a world: none
  binds one yet.

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

- [high] Ordinary travellers can be duplicated by a resent commit: a
  destination that re-leases the same transfer key after a crash lets a resent
  commit (`MayResend` is true for transfer-id-keyed kinds) land a body the
  source already restored after a `Missing` status; and `Missing` is final only
  if the destination tombstones the key it answered for. The companion custody
  spec's lease-once and tombstone rule (`Leased(K)`/`Dead(K)`) fixes both; apply
  it to every traveller, not only companions. Unconfirmed against a run; the
  companion protocol's model check found it and a design review is checking
  the code. (from: the companion-redesign lane)
- [medium] A rule firing costs the world host far more than the work sheet
  prices it. Each firing is its own transaction: `WorldRuleHost.PrepareStateCommit`
  runs `WorldServer.ValidateStateTransaction` and the commit runs
  `CommitStateTransaction`, and both clear and flag a per-row array, walk every
  row, and re-run `WorldInputRuntime.TryValidate` (a `TrySelect` per input owner)
  and `TrySynchronize` whatever the firing wrote. A thread-time sample of a
  headless debug World put an idle tick's rule pass near 3.6 ms for about 5,600
  evaluations and 94 firings, about 15 µs a firing with roughly three quarters
  of it in the input runtime, while a chess judge candidate (the search's
  arena host, no world transaction) ran near its price at the sheet's 1.7 ns a
  unit. Make validation and commit proportional to the rows the firing touched
  and revalidate input only when a row input selection reads changed, then price
  what remains per firing. (from: rule-budget lane)
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

Owner: MCP lane (specialist) · medium · Next: the schema switch, then the pick
answer.

- Time the MCP front's swap to a newly activated build, from a private install
  root (`puck mcp --prepare-install --install-root <worktree>/.claude/mcp-install`):
  the quiet measurement did not, since activating in the shared root reaches every
  session's tools. The placement swap needs no more work: three quiet `hallLights
  level1` swaps read a worst simulate of 40.0-44.5 ms per iteration against 40.5
  ms without one. (from: quiet-measure)

- [medium] `RemoteMcpTests.StateVectorWrite_GrantedPrincipalSucceedsAndUngrantedIsRefused` (`tests/Puck.Mcp.Tests`) fails on a 15 s `HttpClient` timeout, with or without the mcp-batch change (81 of 82 pass). Also: `world.grants` and `world.affordances` answer in one long `[verb: {json}]` line, so the `puck_exec` filter's `lines` and `match` only narrow them as JSON data; use `world.grants <grantee>` with `maxBytes`. (from: mcp-batch lane)

- [medium] Investigate a GPU lease held by an older operator worker while no
  World is advertised and the worker has no direct child process. During the
  controls extraction, two 1200-second windowed starts expired waiting for that
  lease; a normal machine process read confirmed the worker was alive, and an
  exclusive probe confirmed its GPU guard remained held. No foreign process or
  lease file was changed. The cause is unconfirmed; inspect the owning session
  and the `OperatorWorlds.WatchAsync`/`Stop` lifecycle, then release it through
  that session. Evidence and start ids are in the controls hand-back.

- [x] [low] A `puck_world_start` failed with "Access to the path .claude/leases/gpu.queue/000000000062 is denied": a Windows-held ticket file. The merged lease path now counts an unreadable or undeletable ticket as occupied, opens holder metadata with deletion sharing, and always releases its exclusive guard even when metadata cleanup is delayed. The guard, rather than an alive adapter PID in leftover metadata, proves ownership. Sixteen focused lease cases pass, including lane rotation and the Windows delayed-deletion reproduction. (from: bars-and-pick and probe-depth-fix lanes)
- [low] `world.view.pick` reports `placement=none` for static hall geometry and for screens (only the chess table resolves to a placement), and the pick answer does not name the prototype or palette material though `SdfPickResult.MaterialName` exists. (from: bars-and-pick lane)


- [low] Operator writes no longer toast (`WorldToastPolicy`), but the `mutation.applied` audio cue still fires for them; the same rule applies to cues. A refused grant from an addon or peer without authority carries no actor and still toasts, which is right but bypasses the new `Actor` field. The windowed capture after the sweep prelude (no toast on screen) is owed: `world.hud toast` answers `none` headless. (from: operator-toasts lane)

- A connected Claude Code session keeps a tool's old output schema when the MCP
  front switches to a backend whose schemas changed: the first `puck_adapter`
  call after the switch failed with "Structured content does not match the
  tool's output schema" until `/mcp` reconnected, and the front's tool-list
  notification does not refresh it. Keep output schemas additive across a live
  switch, or answer with the schemas the client was given until it reconnects.
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

- [low] The work sheet still prices a few shapes above what one tick runs, each
  a candidate for an enforced bound like `maxFirings` rather than an inference:
  `companion-interest-*` is about 170 running-minimum rules, every one able to
  fire in a tick (about 380,000 units, real worst-case work that one
  nearest-point reduction would replace); a write whose key isn't literal counts
  against every pinned cell of its row in `CountWriters`; a transfer between
  piles a live zone table picks prices its disclosure term by the square of the
  piles (see the card tables). (from: rule-budget lane)
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

- [low] `worlds/hosting/granaries.extensions.json` grants its `console` client
  `mutate` over `section:state` to write six feed rows; scope it to those rows if
  the extension grant vocabulary allows. (from: the lead, landing granaries)
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

### World admission under GPU contention

Owner: operator-adapter lane · medium · Next: separate the GPU queue wait from
the boot deadline, or give admission a ceiling that covers the granted queue.

- `OperatorWorlds.MaxStartSeconds` in `src/Puck.Mcp/OperatorWorlds.cs` caps a
  start's whole build/lease/attachment admission at 1800 seconds. Several
  granted twenty-minute GPU batches can exceed that before a World launches.
  Temporal-shimmer's latest-source Vulkan start and its one retry both expired
  at `gpu-lease`, after building successfully and without launching a World.
  Repeated fresh admissions lose queue position instead of making progress;
  finish the pending lane's evidence after capacity changes. Keep the active
  windowed batch limit separate from time spent waiting for admission.

### Delivery, launcher and CI

Owner: delivery lane · ordinary · Next: measure deriving the eleven addon guest
`.wasm` modules from source against edit-to-running.

- [high] The launcher's version pruning (the owner's ruling: keep `current`, `last-good` and the newest N, delete the rest) is rebuilt by handle: `Puck.Confinement`'s `ConfinedDirectory` and `InstallRegistration`, and `InstallPruner`, with the invariants in the [launcher README](../../src/Puck.Launcher/README.md#pruning-past-versions) and a law per in-model review finding in `InstallPruneLawTests`. Its threat model is settled ([decision](../decisions/runtime-and-delivery.md#releases)): it defends against cooperating processes and crashes, not the install root's own user, and the round-3 review's in-model findings are fixed on its branch. It awaits the lead's landing review. Left outside the confinement: `ContentAddressedUpdateStager` deletes a corrupt cached object by path (`File.Delete(ContentAddressedStore.ObjectPath(...))`) and `ContentAddressedStore` writes objects by path, both under `update.lock`. (owner: delivery; from: prune-redesign)

- Stop committing the built addon guests: the eleven `wasm/*/dist/*.wasm`
  modules are compiled from the Rust crates beside them, and a derived product
  is a cache ([decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
  Deriving them on a miss puts the Rust `wasm32` toolchain on every machine and
  CI job that loads an addon, so measure that cost against edit-to-running
  first. A hand-written fixture module (the transpiler sample's 8-byte
  `Samples/addons/rules.wasm`) is source.
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

### Bake detail

Owner: specialist lane (rendering, sdf-authoring) · ordinary · Next: measure
the static round-section term's cost with `puck mesh audit --cpu`.

- Static placements' plain round shapes still bake at
  15 mm (`ShapeDetail` reads no round section); giving them `BodyDetail`'s
  curvature term rebakes the world, so measure its cost first
  (`puck mesh audit --cpu`). A dynamic group refines its whole bound toward its
  finest member: `SdfMeshBakeGroup.PlanGroups` records per-member regions only
  for static instances and merges every member's detail into one
  `DetailLow`/`DetailHigh` box, where per-member regions would let a thin
  member declare its own detail.

### Temporal shimmer and cadence flicker

Owner: integration rendering follow-up · medium · Next: exercise the original
off-mode blink with refresh holds active on a quiet machine. The reconstruction
item is closed: both windowed backends now converge at the packet's two poses;
receiver-plane depth, exact stationary motion and the bounded eight-phase
window are documented in the rendering skill. In 16 presented frames, gallery
max mean abs dY fell from 1.2785 to 0.0049 on Direct3D and from 1.0677 to 0.0082
on Vulkan, against adjacent off controls 0.0073 and 0.0093. On-mode changed-pixel
maxima remain 414/0.945 % and 434/0.990 %, respectively; this is not exact zero.
The corrected sill changes at most 1 pixel on/6 off on Direct3D, 3 on/4 off on
Vulkan. The lane's `handback-current.md` retains all before/after bursts and
the completed 70-stop comparison manifest.

- [medium] The original off-mode sill burst has two paired flashes 11
  presentations apart, 3.18–3.42 % changed with max abs dY 26.28 (w06-burst-off
  frames 1/2 and 12/13 under `D:/Source/ByteTerrace/Puck/.claude/scratch/walkthrough/`).
  Region 0.36 0.78 0.32 0.1; `body.pose 16.15 13.57 37.28 191 0 0`;
  `view.override camera look 15.508 16.895 33.842 16.155 14.57 37.279 55`;
  lift held open at level 3. The old shaft-lintel lids coincided with the slab
  top. All 481 and 553 pixels with abs dY > 5 in the two original flashes lie
  inside that projected lid (2-pixel filter margin). A matched material-ID
  comparison, with temporal, shadows, AO and indirect off, shows triangular
  competing material winners before separation and a uniform floor after it.
  The authored lintels now end at the slab underside; their overlap is fixed.
  The natural paired blink was not reproduced even in an old-geometry
  120-frame burst under load. The final runs report `shadow-far.held=0`, so
  they do not qualify a cadence refresh. Two events do not establish a period;
  no particular far-shadow, GI or screen refresh is proved responsible.
  A quiet Direct3D 12 run at this pose (lift at level 3, `world.temporal off`,
  bakes settled) did not reproduce it: a 120-frame presented burst over that
  region changed at most 5 pixels and max abs dY 0.72 in any frame (41 of 119
  pairs changed at all). That World presented at 7-8 FPS, about 3.5 ticks a
  frame ([frame time](#frame-time-on-the-lead-machine)), so this is not yet the
  refresh cadence the item asks for; one burst, Direct3D only. Re-run it on both
  backends once the frame time is fixed, before closing the item. (from: owner
  walkthrough; quiet-measure)

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

### The valley's terrain

Owner: geometry and bake lane (sdf-authoring, rendering, physics) · ordinary ·
Next: the lead's window over the sweep tour's `valley` stops, then contact on
the drawn relief. The measured state is in the
[plan](valley-and-moons.md#phase-1-the-valley-as-built).

- Valley review findings (Codex review of the valley merge `be0f38692`):
  - [medium] The superellipsoid's product-first rounding misclassifies small superellipsoids and disagrees with the HLSL: exponent 2, all radii 1/1024 m, p = (1/256, 0, 0) gives −1/1024 m on the CPU and +3/1024 m on the GPU, so contact and the drawn surface disagree at small sizes (`src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs:836`). One formula, exact at both ends of the radius range, on both sides.
  - [medium] Noise bake detail forces whole-surface refinement: `OwnFinal` clamps its factor to at least 2, so a small creation declaring 1.5625 m spacing still subdivides its existing ~15 mm lattice (`src/Puck.World.Client/WorldPlacementStamper.cs:770`).
  - [medium] `physics.contact` adds three or four shared `CompareExchange` updates to every surviving convex query whether or not a counters reader exists (`src/Puck.Physics/FixedStaticContactWork.cs:51`): count per thread or per tick and publish on read.

- Contact rides the unrelieved masses: creation noise is render-only, so a body
  on a wall floats over or sinks into the drawn relief by up to its amplitude
  (4 m on the sectors). The fixed-point render reading already evaluates
  `NoiseDisplace` deterministically; analytic contact needs a collider whose
  surface is the relieved field, with a step bound for the sweep.
- A standing body creeps downslope on a curved superellipsoid: 2 cm a second on
  a 22 degree slope and 7 cm a second on a 58 degree one (`body.where` over 30
  ticks at rest, north sector).
- A scoped static creation's instance bound is a sphere about the creation's
  origin (`WorldPlacementStamper`'s scoped branch), 770 m in radius for a sector
  centred on the ring's centre, so the bake's eight-cells-a-coarse-axis cap, not
  the relief's declared 1.56 m, sets its 2.8 m cell. A bound about the shapes
  would let the declared detail through; measure the triangles it costs first
  (about 3.5 times a sector's 266,000).
- Creation limits sized for props bound terrain: noise amplitude at most 4 m
  (`CreationNoiseDocument.MaxAmplitude`) and smooth blends at most 0.5 m
  (`CreationDocument.MaxSmooth`), so a 300 to 430 m wall reads as a few smooth
  masses with metre-scale rock.
- The CPU evaluator's capsule reads its segment parameter through a Q16 inverse
  squared length (`SdfCapsule`), the precision loss the superellipsoid had for a
  large radius.

### Card tables in the board game room

Owner: debug-world content lane (puck-world, puck-dsl, sdf-authoring) ·
ordinary, the owner's direction · Next: the lead's windowed look at the Klondike
table (`worlds/debug/cardtable.md`), then step 2.

The owner wants proper card tables in the gallery: physical cards on a table,
played by sitting at it like the chess, Go, Reversi and Alchemy tables. The
Klondike table stands north of the Go table in the board game room
(`worlds/debug/shapes.md`); the look is the owner's review.

- The lead's window over the Klondike table showed three defects (court figures standing through the cards laid over them, unreadable pips on the dark ground, a layout crammed at the far edge); the standard deck's two variants, the flattened court figures and the centred 10.6 cm layout answer them headless. Next: the lead's windowed look, close and wide, with the script in `worlds/debug/cardtable.md`.
- Landed: the deck (`worlds/standard/cards.puck`, a library any author imports,
  52 faces and a back as SDF creations), Klondike and the `solitaire.puck`
  collection (`worlds/debug/klondike.puck`, `solitaire.puck`), the table
  (`cardtable.puck`) and its `test` blocks (`cardtable-tests.puck`).
- Prior art still quarantined for the next steps: `experimental/src/Puck.World/Assets/worlds/games/`
  holds Spider (one, two or four suits), FreeCell and poker with a hand-strength
  judge, and backgammon, dominoes, mancala, hidden ranks and word spy for later
  tables; `experimental/tests/Puck.World.Games.Tests` holds laws (Solitaire deal
  and rules, poker hand strength) as prior art for `test` blocks.
- The cards are posed bodies, not rigid ones: a fan of cards a few millimetres
  apart is not an arrangement a solver holds, so the rules' door judges each
  request over the piles and the table follows; no settled table is read.
  Letting a hand lift a card with `body.carry` and judging where it lands would
  be a second input path beside the cursor.
- The rules' work-budget cost is the table's price: Klondike with its table is
  about 200,000 units of the sheet. With the batched Go reads and the one-piece
  chess moves priced by their `maxFirings`, the debug world's sheet is about
  2,550,000 of the 4,250,000 ceiling; FreeCell and the game switch add about
  215,000 (2,765,000 read from `world.budget` on a headless World), the chess
  search needs about 600,000 of the remaining 1,485,000, which leaves Spider about
  885,000.
  A transfer between named piles costs a few thousand where one between piles a
  live zone table picks costs about 140,000 (the disclosure term grows with the
  square of the piles the table reaches), so every deal step, gather step and
  move is a rule of its own pinned on its literal piles, and the table reads one
  pile a tick (`kSync.scan`), which the sheet prices as the dearest pile.
1. Spider as the same table's game switch (`solitaire.puck`; FreeCell landed
   as `freecell.puck`, `solitaire.game 2`, `freecell-tests.puck`): it lays its
   piles on the `kSlots` graph after FreeCell's (`fSlotCount`) and answers the
   seat through its own mailbox. Its shape: 104 cards need 52 more bodies
   (`cardBodies` 54 to 106, the bodies still the population's top slots) and a
   60-cell-wide grid for ten columns (the 2 cm cell fits them at 12 cm and the
   felt grows to 1.32 m); a body wears one face for good, so it is the
   four-suit game (card `k` wears `card-<suit of k % 52>`, and the one- and
   two-suit games would need a look a body can swap); the rules are ten
   columns of any-rank-on-the-next-rank moves of a same-suit run (90 move
   rules), ten stock deals of one card a column, and a run K to A of one suit
   leaving to a completed pile.
2. Poker: seats, computer players and the hand judge; its own step.
- Residency: none of it waits on streaming; at 52 to 104 cards a table it is a
  stress case for nearest-first bake order
  ([streaming](#residency-and-streaming)).

### Residency and streaming

Owner: rendering lane (rendering, verification) · ordinary · Next: step 1, the
measurements, then step 2, near-first ordering.

The design is settled in [residency](../decisions/residency.md), from a
three-family consensus: only presentation follows the observer; portals are
edges, worlds and optional authored areas are nodes; one selector per
presentation orders and bounds residency from every demand point; a door never
waits for loading. The steps, each taken when its measured need appears:

1. Measure: time until everything near the spawn has landed (cold and warm),
   mesh bytes by level, `ProbeCache` bytes after a hall walk and a valley flight,
   program build and replan time against placement count, store read rate
   against the landing rate.
2. Near-first: order lattices (`SdfMeshPathBake.TryTakeForFrame`), store loads
   and owed meshes (`SdfWorldTables.MeshRegion`) by priority from demand points;
   all three run in document order today. The card tables against a lobby spawn
   are its test case.
3. GI on the same demand points: the probe window follows every view, not
   `Views[0]` (`SdfMeshIrradiance.cs:208`); the irradiance scene and its pending
   gate are scoped to what the window reaches; `ProbeCache` (`:1365`) is
   byte-bounded.
4. Mapped demand through portal faces, replacing the blanket baking share
   (`WorldViewGraphHost.ResetFootprints`).
5. Eviction under one byte budget across residencies, with coarse levels owning
   compacted vertices; removes the throw past `GpuRegion.MaxByteCount`.
6. Authored areas (a volume with openings, on an existing volume vocabulary),
   tried first on the gallery's floors with the stairwell, lift shaft and window
   bands as openings (the owner's suggestion): time until the occupied floor has
   landed, with and without the areas, and floor-first eviction under a lowered
   budget.
7. Terrain tiles as selector units ([valley](valley-and-moons.md) step 4), then
   the moons.

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
    [mesh-bake packet's](#bake-detail)).
- The suit toggle's handheld: only body 0 holds the brick (`pilotHold` declares
  the one key `"0"`); she holds it only while captured standing still, so a
  mirrored engagement that walks or carrying it about wants a hold pose that
  blends with her gait; and the look's glass shell reads near white in her hands
  where the rest's brick reads orange in the same run (a pooled creation's
  glass may lose its tint).
- The board game room: the owner reviews its look (the tables share the chess
  table's wood, the Go board a flat kaya face, the Reversi board green felt);
  addon seats at the tables; card tables have their own packet
  ([card tables](#card-tables-in-the-board-game-room)).
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

- The granary board ([granaries.md](../../worlds/debug/granaries.md)): the owner's own
  run against the live resource group is outstanding (the command is in that guide; the
  fixture configuration stands in for it everywhere else). A piece's look carries its
  account's kind only, since a deal's variants read one row; SKU and region are rows the
  lectern reads. The pieces are dealt placements, not bodies, so a seated player inspects
  and cannot yet pick one up; moving a piece (`deal.preserve`, `reflow`) is unused here.

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
