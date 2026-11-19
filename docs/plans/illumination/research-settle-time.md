# Why the lighting takes minutes to settle after a boot

A research record for [the illumination plan](../illumination.md), not a plan: it has no steps of its own, and
what it finds is scheduled through the plan's build order and
[open items](../open-items.md#lighting-and-gi). It loses to the code; recheck a symbol before building on it.

**Labels.** *Code* is read in source at the tree this record was written on and cited by symbol. *Log* is a
World's own stderr from a windowed boot of the debug world on an RTX 4070, at that boot's build of the
integration branch; each run is named in [the runs](#the-runs-read) below. *Leg* is one of this lane's own boots
with added logging, described in [this lane's legs](#this-lanes-legs). *Inference* is reasoning from these and
says so. Wall-clock seconds come from the volume's own `[sdf.mesh-gi] done … seconds=` line, which counts
from the moment the volume first owed work (`SdfMeshIrradiance.Owe`); those readings were taken beside other lanes'
work and say how long, not how fast. The counts beside them (scene builds, probes traced and relit) do not depend
on load.

## The answer in plain terms

The volume itself is not slow. When its static scene stands still it traces every probe once and relights the
window a few times in about a minute: run B's Medium window (49,152 probes) traced 49,152 and relit 296,832 in
56 s, and run A's High window (73,728 probes), switched to after the scene had settled, traced 73,728 and relit
589,824 in 60 s.

What takes minutes is that the scene does not stand still. The scene the probes trace is built from the
meshes the views draw, at the detail tier each view's camera asks for. After a boot that detail keeps moving
for minutes (first details over the floors, then finer tiers, then coarser ones as the mesh region's budget
presses), and every move is a new scene. In run A the two large residencies built their scenes 40 times before
the main volume's first `done`, which came at 577 s, having landed 93,355 traces and
915,005 relights for a 49,152-probe window: about twice the traces and three times the relights that a still
scene needs. The work that never landed does not show in those counts at all, because a scene change throws
away whatever batch was in flight.

Four mechanisms turn a detail change into minutes of lighting work, and a fifth doubles the work to do:

1. **Every detail change is a geometry change.** The scene's key is each static draw's mesh, transform and
   material, and a new tier is a new mesh.
2. **A wave of changes retraces the whole window.** Past 64 changed draws the change is named as one box that
   holds them all, which every probe meets.
3. **Every geometry change relights every probe from scratch.** A probe reuses its point-light cube only under
   the very geometry it was relit under, so after any change each probe traces its shadow rays to every point
   light in range again.
4. **A change discards the work in flight.** The batch being traced or relit when the scene moves is dropped
   whole, and its queued GPU jobs are cancelled.
5. **A second full volume lights the same world.** When the session screens are in view, the shared session
   residency that renders the boot world for them owns its own 49,152-probe volume, and in run A it drew more of
   the shared background budget than the main volume did.

Under all five sits a thin grant. At boot the frame's own GPU time overruns the display period, so background
work gets its floor of 1 ms a frame, about 18 ms of GPU a second (leg 1). A still-scene settle needs only a few
seconds of GPU, which that floor stretches to a minute or two; each wasted rebuild costs the same again.

The rest of this record gives the numbers for each question the brief asked, then [the ranked
fixes](#the-ranked-fixes).

## The scene rebuilds and what triggers each

**What a rebuild is.** `SdfMeshIrradiance.RefreshScene` compares the frame's static-draw key with the installed
scene's and starts `StartSceneBuild` when they differ. The key (`StaticKey`) is an order-independent sum of
`DrawKey`, a hash of each static draw's `(Mesh, ObjectToWorld, Material)` *(code)*. `SdfMeshIrradianceScene.Build`
reuses a geometry it already holds by its mesh (`GeometryKey.Of`) and builds the rest, so a rebuild costs CPU
for the new meshes' hierarchies, a new top level over every draw, and a GPU upload *(code)*.

**What moves the key.** A static draw's `Mesh` is what the bake shows for its group (`Group.Shown`, read by the
dresser through `FrameAccess.MeshOf`), so the key moves whenever the bake swaps a group's mesh *(code)*:

- a group arriving with nothing to draw lands its floor first, then its first detail
  (`SdfMeshPathBake.Tiers`, `FirstDetailOwed`);
- `SdfMeshPathBake.Retier` plans a group again at a finer tier the camera's demand asks for, at most
  `SdfMeshTier.MaxInFlight` (64) baking at once, and any number at once that the library already holds
  (`StartReplacement`'s `Library.TryTake` lands at once and does not count against the 64);
- under budget pressure the same decision coarsens spare groups and the selector raises the one detail bias,
  which coarsens every group past its rung (`SdfResidencySelector`, `SdfMeshTier.Biased`);
- a finer tier the mesh region refuses is evicted to the floor (`SdfMeshPathBake.Evict`).

So the lighting's geometry is the camera's detail, not the world's. Run A's main scene went 613 M → 524 M →
499 M → 352 M → … → 151 M triangles (instanced, `SdfMeshIrradianceScene.TriangleCount`) over its rebuilds
while the bake coarsened under the budget: the bake's own `done` line fell from 41.5 M to 19.9 M triangles over
the same period *(log, run A)*.

**What paces a rebuild.** Only a frame whose bake still owes opaque static meshes is paced:
`SdfMeshIrradiance.Stage` calls `RefreshScene` at once when `bakePending` is false, and otherwise only when
`ScenePendingDue` holds (no build running, the installed scene's traces listed done, and
`PendingSceneInterval`, four seconds, since the last build began) *(code)*. `bakePending` is
`SdfMeshPathBake.IrradiancePending`, which a retier wave turns on while its replacements bake or land and off
once their meshes reach the dressed frame. *Inference:* the wave's last swap therefore rebuilds the scene on the
next frame whatever the traces have done, and a retier decision comes every `SdfMeshTier.MeasureFrames` (8)
frames, so on a moving or freshly booted view the pacing gate is open for exactly the frames that rebuild.

**How often, in the runs read.** Large-scene builds (6,300 to 7,000 draws) before the main volume's first
`done` (or in the whole run, where none came), and how many of them added more than 64 new geometries. Each new
geometry is at least one changed draw, so every build in that column crossed the union rule below:

| Run | Backend, store | Large-scene builds | Over 64 new geometries | Geometry triangles built | Main volume's first `done` |
|---|---|---|---|---|---|
| A | Direct3D 12, shared store | 40 | 31 | 135.6 M | 577 s |
| D | Direct3D 12, shared store | 11 | 6 | 61.6 M | 912 s |
| F | Vulkan, shared store | 46 | 30 | 117.5 M | none within the run |
| G | Vulkan, shared store | 45 | 33 | 116.0 M | none within the run |
| H | Vulkan, empty store (cold) | 36 | 29 | 117.0 M | none within the run |
| C | Direct3D 12, empty store (cold) | 3 | 2 | 38.7 M | 206 s |
| B | Direct3D 12, shared store | 2 | 2 | 25.5 M | 56 s |

The counts include both large residencies (the world and the shared session), which alternate in the logs.
Runs B and C are the controls: their scenes were built once per residency and barely again, and they settled in
56 s warm and in 206 s while a whole cold bake (48,422 bake dispatches, 1.03 G samples) shared the GPU budget
with the lighting.

## Relight scope per rebuild

**Which probes retrace.** A scene build records the world boxes of the draws it added or removed
(`SdfMeshIrradiance.ChangedBoxes`), and the selection kernel (`sdf-probe-select.comp.hlsl`, with
`SdfMeshIrradiance.Affected` its CPU twin) retraces a probe only when a box overlaps its placement region or
meets one of its rays before the recorded hit *(code)*. More than `MaxChangeBoxes` (64) changed draws are named
as the one box that holds them all, so the selection job stays bounded *(code)*. A retier wave across the world
is that case: the union box holds the whole window, and every probe retraces. Run A had 31 such rebuilds in
577 s *(log)*.

**Which probes relight, and how deeply.** Every probe of the window relights after any geometry change, whether
or not it retraces, and none of them keeps its point-light part: `reuseLocal` in `SdfMeshIrradiance.Process`,
and the same test in `RelightGpu`, requires `previous.Geometry == geometry` *(code)*. A relight that cannot reuse
its local cube traces, from every hit texel, one ray toward each shadowing point light whose range reaches the
probe (`SdfMeshIrradiance.RelightRays`: `lights × (HitCount + 1)`, with `HitCount` 144) *(code)*. Beside the
debug world's stage a probe has up to 78 such lights, about 11,300 rays, against 145 per directional light when
it reuses ([the mesh-GI reference](../../../.claude/skills/rendering/references/mesh-gi.md) records the 78).
So the relight after a detail change is the most expensive relight the volume ever does, on every probe,
whatever the change touched.

**Per rebuild, in probes.** At Medium a rebuild that crosses the union rule owes 49,152 traces and 49,152
full-depth relights, and its bounce then takes further sweeps; at High, 73,728 of each *(inference from the
code above and `SdfMeshIrradiance.LevelsOf`)*.

## Trace job count, latency and admission waits

**Work thrown away.** The worker works through its list a batch of `BatchProbes` (384) at a time *(code)*. A
scene change advances the geometry version (`AdvanceGeometry`), and then:

- `Batch` drops the batch's results under the gate when `Stands` fails, after both its trace and its relight
  have run (the `if (!Stands(snapshot: in snapshot))` before landing);
- `RelightGpu` drops the batch before handing its relight over, when the trace landed but the scene moved;
- `ListGpu` throws on a moved scene, ending the listing;
- `SdfMeshProbeGpuWork.TryTake` cancels a queued query whose scene the volume no longer keeps
  (`KeepsGpuScene`, `KeepsGpuPublication`) *(code)*.

So a rebuild costs the in-flight batch whole: its placement trial rounds, its trace jobs and any of its relight
jobs that ran. *Inference:* a 384-probe batch of fresh probes beside the stage predicts 384 × ~11,300 ≈ 4.3 M
relight rays, cut at most `RelightRaysCeiling` (1 M) a job and further to the frame allowance, with at most
`SdfGpuWork.SlotCount` (4) jobs in flight; a batch therefore spans several frames' round trips, and a scene
that rebuilds every few seconds lands few of them. The hall-indirect lane's reading (finest level's traces held
at 768 to 1,152 probes for over two minutes, two or three batches) is this.

**Admission waits.** A query also waits:

- behind a scene upload, which runs only with no query in flight and admits none while it runs
  (`m_sceneUpload` in `TryTake`) *(code)*;
- until every mesh the scene names stands in the mesh region (`SdfWorldTables.TryProbeScene`); one that waits
  past `AdmissionWait` (2 s) is cancelled and its coordinator lists again *(code)*. A scene built from a tier
  that is still landing in the region waits here, and a coarsening wave puts new meshes in the region exactly
  when it changes the scene;
- for room in the frame allowance, while other background work (the bake's lattices, the mesh region's landing)
  is queued ahead (`SdfGpuWorkAllowance.Admits`) *(code)*.

The selection job's 33 s latency the brief cites sat behind the boot's 348 MB scene upload; *inference:* at the
upload rate below, sharing it with the mesh region's own landing, that upload alone holds every query for
seconds, and the rebuilds that follow it repeat the wait each time.

**Counts owed.** `world.residency`'s `trace-jobs=[kind:jobs/units/latency]` per kind and the
`sdf.mesh-gi.gpu-cancelled` counter were not read for this record ([what was not run](#what-was-not-run)).

## Upload sizes

- **The rate.** Background uploads share `SdfGpuWork.UploadBytesPerSecond`, 4 slots × 1 MiB × 60 =
  240 MiB a second of wall time, across every residency's scene uploads and every mesh region's landing
  (`SdfGpuWorkBudget`'s upload credit) *(code)*.
- **The first scene.** 348 MB at boot (the hall-indirect lane's `world.residency` reading).
- **Every later scene re-uploads its whole prefix**: the top level's nodes, its braid entries and one record per
  draw (`SdfMeshIrradianceScene.PrefixWords`), whatever geometry it shares *(code)*. At 6,800 to 7,000 draws the
  prefix is 12.6 M words, 50 MB; over run A's 40 large rebuilds the prefixes summed to 254 M words, 1.02 GB
  *(log, the `prefix-words=` field)*, about 4 s of the upload rate on its own, during each of which no query
  ran.
- **New geometry.** Each new geometry uploads its packed nodes, triangle ordinals and braid frames
  (`CreateGpuExport`). Run A built 135.6 M geometry triangles across its rebuilds *(log)*; at about two words a
  triangle *(inference from `PackedNodeWords` and seven-triangle leaves)* that is on the order of 1 GB more.

## What the allowance grants against what lighting could use

- **The grant.** Background GPU work takes at most `SdfGpuWork.GpuShare` (a quarter) of the display period,
  less whatever the frame's own GPU time leaves short of nine tenths of the period, never under
  `SdfGpuWorkBudget.LeastFrameSeconds` (1 ms) a period (`SdfGpuWorkBudget.ShareOf`) *(code)*. At 60 Hz that is
  between 1 ms and 4.2 ms of GPU time a frame, shared first come by every residency's bake, landing, scene
  upload and probe work.
- **The authored probe budget never binds.** The standard's `render.indirect.budget` grants 122,880 relights and
  122,880 traces a second (`worlds/standard/quality.puck`) *(code)*. The stable High settle in run A averaged
  about 1,200 traces and 9,800 relights a second *(log, 73,728 and 589,824 over 60 s)*, so the GPU share, not the
  probe budget, is what limits the volume.
- **What the grant was at boot (leg 1).** The frame's own GPU time read 16 to 28 ms a frame against the
  16.67 ms period for the whole run, so the share sat at its floor, 0.060 (1.00 ms a frame), from the first
  poll to the last (`work-budget=[gpu-share:0.060,…,frame-gpu:25.75ms,…,gpu-granted:13504ms,gpu-charged:13507ms`)
  *(leg 1)*. Over the run's 740 s of service the budget granted 13.5 s of GPU time, about 18 ms a second, and
  background work charged all of it; the service's jobs were held 38,805 times by the frame's deadline against
  2,079 times by the budget (`deadline-holds`, `budget-holds`). So at boot the binding limit is the frame
  overrunning its period, which leaves background work the 1 ms floor.
- **What lighting could use.** Leg 1's whole first fill and 27 sweeps of relights cost little GPU time: 49,152
  traces in 0.27 s (`gpu-trace-nanoseconds` 274,499,200) and 1,342,663 relights in 12.45 s
  (`gpu-relight-nanoseconds` 12,454,226,784), about 5.6 µs a trace and 9.3 µs a relight; the ray-query
  structures took 0.54 s more *(leg 1)*. *Inference:* a still-scene settle of the Medium window (one trace and six
  to eight relights a probe) is about 3 to 4 s of GPU time, which the 18 ms a second the floor grants stretches
  to minutes, and the jobs' round trips (a relight job's mean 88.9 ms from submission to answer, a trace's
  43.3 ms) add to it. The moving sun needs 2,496 relights a second to hold level 0's age target, about 23 ms of
  GPU a second, more than the grant: level 0 printed `[sdf.mesh-gi] behind` 47 times in leg 1, relighting 1,400
  to 2,100 probes a second.
- **The allowance does not explain the hall's minutes on its own.** Leg 1 published every level within two
  minutes under the same floor (below); the boots that took ten churned their scene as well.
- **The cold boot's bake shares the same grant.** Run C's scene hardly moved, and its settle still took 206 s,
  against run B's 56 s warm, while the cold bake drew on the same budget *(log)*.

## Does the lighting depend on render detail tiers, and could it light from the floor

**It does, entirely.** The scene is the static draws' meshes, which are the tier each group shows for the view
(above). The trace also reads those meshes' words in place from the mesh region (`TryProbeScene`,
`SdfMeshIrradianceScene.CreateGpuExport` references triangles in "an already placed mesh region"), so it can trace
only what the region holds *(code)*. Ray queries build their bottom levels from the same placed meshes
(`SdfMeshProbeGpuWork.Rays`), which is why `world.rays off` made no difference: the churn is upstream of the walk.

**A stable proxy exists for most groups.** Each group's floor is its coarsest tier; a group arriving with
nothing to draw lands it first and keeps it with its own claim while a finer tier draws (`Group.KeptFloor`),
named ahead of the draws so its words stay in the mesh region (`ServeAhead`), and a refused finer tier falls back
to it (`SdfMeshPathBake.Floor.cs`) *(code)*. The floor is chosen by the plan, not by any camera, so a scene of
floors moves only when a group arrives, leaves or is re-authored. Its limits *(code and the plans)*:

- not every group has one: a group taken whole from the library, or replanned with nothing shown, keeps none
  until it arrives again; `world.residency`'s floor line counts them (`floorless=`, `DescribeFloors`);
- whether a floor is good enough to light from is unjudged. [The plan's stable trace
  mesh](../illumination.md#after-the-gpu-trace) and [streaming decision
  9](../streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand) favour a trace tier
  sized by the probe spacing, with the floor wherever the spacing asks for nothing finer, after a bake-time check
  that the floor keeps thin walls, recesses and separations.

**Inference on the saving.** A scene that ignores view retiers builds about once per arrival wave. Runs B and C,
whose scenes moved two or three times, settled in 56 s warm and 206 s cold, against 577 s and 912 s for runs A
and D, and no settle at all within runs F, G and H.

## What an incremental relight would need

What "proportional to change" needs that the volume lacks today, in the order the evidence above puts them:

1. **Changes that do not collapse.** The selection kernel walks a probe's rays over every remembered box on one
   GPU thread, which is why `MaxChangeBoxes` unions a large change. Kept as boxes, a wave of thousands needs a
   structure the kernel can query in logarithmic time (a box hierarchy or a coarse grid of change cells), so a
   swap near the hall retraces the hall's probes and not the valley's.
2. **A probe's own geometry version decides, not the volume's.** A batch traced under geometry *n* that lands
   under *n + 1* is still a correct measurement of *n*; landing it with its version and letting the next
   selection's change test decide whether it retraces keeps the work instead of dropping it (`Batch`,
   `RelightGpu`, `ListGpu`, the query cancellation in `TryTake`).
3. **Local cubes kept across changes they do not reach.** A point light's shadow segments from a probe's hits
   are tested against the change boxes as the rays are (`Affected` already extends rays by the sheet thickness);
   a probe none of whose segments meets a box keeps its local cube, so only the directional part and the bounce
   relight.
4. **An incremental top level.** The prefix is uploaded whole per scene; a top level whose unchanged subtrees keep
   their addresses would upload only what moved, and would let queries keep running against the old root until
   the new one stands (they already keep the old root during an upload, `CompleteSceneUpload`; the cost is the
   exclusion and the bytes).
5. **Bounce follows on its own.** A geometry change near a probe changes what its neighbours' relights read; the
   existing sweep convergence (`ConvergedShare`, the sweep rule in `Work`) already carries that outward, so no new
   mechanism is needed there.

With a camera-independent trace scene (fix 1 below), items 1 to 4 matter only for real geometry changes
(an author's edit, a world arriving, a door that moves the static set), which is where they belong.

## The ranked fixes

Each fix names its expected saving, the evidence for it, and the check that would show it worked. Savings are
read from the runs above; none is measured on the changed code.

1. **Trace a scene the view does not move.** Build the irradiance scene from a per-group trace mesh chosen by the
   plan (the floor where the plan's lighting check admits it, a probe-spacing tier where it does not), not from
   `Group.Shown`; keep that mesh resident in the region as floors are now. This is the plan's stable trace mesh
   and streaming step 9; the evidence says it is the whole of the problem's first order.
   *Saving:* the post-arrival rebuilds (40 in run A, 46 in run F) drop to the arrivals; the first `done` moves
   from 577 to 912 s (Direct3D 12) and "never within the run" (Vulkan) toward the stable runs' 56 s warm and
   206 s cold. *Check:* a warm boot logs no `[sdf.mesh-gi] scene` line after the bake settles while the camera
   holds, and a wave of camera tier changes logs none (the plan's own check); the main volume's first `done`
   beside run B's.
2. **Keep work a change does not invalidate.** Land a batch with the geometry version it traced against, and
   leave the retrace decision to the next selection, instead of dropping it in `Batch` and `RelightGpu` and
   cancelling its queued jobs. *Saving:* every batch in flight at a rebuild (one to three at 384 probes each, with
   their relight jobs) instead of zero; the hall-indirect lane's stuck 768 to 1,152 traces is the signature.
   *Check:* `sdf.mesh-gi.gpu-cancelled` and the traced count across a forced rebuild
   (`world.detail 1` then `world.detail auto`) before and after: traced keeps rising through the rebuild.
3. **Keep local cubes across changes that miss them.** Test each probe's point-light shadow segments against the
   change boxes and reuse its local cube when none meets one (`reuseLocal`'s geometry clause). *Saving:* a relight
   after a change costs directional rays (145 a light) instead of every point light in range (up to about 11,300
   a probe beside the stage), for every probe the change does not reach. *Check:* a relight job's
   `reused=` field in `[sdf.gpu-work] slow` and `world.residency`'s relight units over the rebuild; the
   point-light law's lit values unchanged within `ConvergedShare`.
4. **Name a large change without unioning it.** Replace the one-box union past `MaxChangeBoxes` with a structure
   the selection kernel queries per ray in logarithmic time. *Saving:* a world-wide retier wave retraces only the
   probes whose rays the swapped draws meet, instead of all 49,152 or 73,728; 31 of run A's 40 rebuilds crossed
   the union rule. *Check:* `world.residency`'s selection `most-job` boxes and tests stay bounded at a wave of
   several hundred changes, and the traced count per rebuild falls below the window.
5. **Size the shared session volume by what shows it.** The shared session residency renders the boot world into
   session screens yet runs a full Medium volume: in run A it landed 266,606 traces and 1,596,160 relights
   against the main volume's 93,355 and 915,005, first come on the same budget *(log; the volume is told apart
   by its scene's draw count, the only other residency near 6,600 draws being `routed$session$18$0$shared`)*. Size each residency's
   lighting by the display share its views take (the plan's step 6 pool and streaming decision 9's lighting
   floor: a small glass asks only the coarse levels), or have a session residency whose disclosed scene matches
   the world's read the world's volume. *Saving:* up to about two thirds of the background probe work at boot in
   run A. *Check:* the session residency's `done` line shows only coarse levels' probes, and the main volume's
   ages fall in `world.budget`.
6. **Pace every rebuild by trace progress, not only while the bake owes meshes.** Apply `ScenePendingDue`'s gate
   whatever `bakePending` says. *Saving:* bounds the livelock while fixes 1 and 2 are built, at the cost of
   lighting from a scene up to one trace behind the drawn one; it is superseded by 1 and 2 and is worth landing
   only with them. *Check:* no two `[sdf.mesh-gi] scene` lines less than one window trace apart.
7. **Upload only the top level's change.** An incremental prefix, item 4 of [what an incremental relight would
   need](#what-an-incremental-relight-would-need). *Saving:* 1.02 GB of uploads and about 4 s of exclusive upload
   time over run A; small once fix 1 lands.
   *Check:* `trace-scene-last-change-bytes` for a one-draw change near the draw's own share.
8. **Let a loading boot spend the GPU on its lighting.** Before the first view is shown (or while a view is
   withheld for its lighting), run the volume's work unbudgeted as a converging frame's drain does. *Saving:*
   the GPU-share-limited part of the settle (the 56 to 60 s on a stable scene); its size is owed from
   `gpu-trace-nanoseconds` and `gpu-relight-nanoseconds`. *Check:* the stable settle's seconds against its
   summed job GPU time.

## The runs read

Every run booted `worlds/debug` windowed on an RTX 4070 from a lane worktree on the integration branch; the
World's stderr was kept by the `puck` server under its per-run directory, and the commit that adds this record
names each run's log. The backend is read from the device line (`[gpu-optional] … raytracing-tier=` on
Direct3D 12; the `[backing] sparse-…` line on Vulkan), the store from the compiled-world path.

| Run | Backend | Store | What it shows |
|---|---|---|---|
| A | Direct3D 12 | shared | The 577 s first `done` after 40 large rebuilds; then High from scratch on a settled scene in 60 s |
| B | Direct3D 12 | shared | Two large builds; first `done` 56 s |
| C | Direct3D 12 | empty (cold) | Three large builds while a cold bake ran; first `done` 206 s |
| D | Direct3D 12 | shared | 11 large rebuilds; first `done` 912 s; the session volume's 628 s |
| F | Vulkan | shared | 46 large rebuilds; the main volume never reports `done` |
| G | Vulkan | shared | 45 large rebuilds; never reports `done` |
| H | Vulkan | empty (cold) | 36 large rebuilds; never reports `done` |

## This lane's legs

Each leg booted `worlds/debug` windowed with `--devices none` from this lane's build of the integration tip, with
throwaway logging added to `SdfMeshIrradiance` (deleted before this record was committed): each scene build's
changed draws split into new places, gone places and same-place swaps, whether the union rule fired, whether the
bake was pending and the seconds since the previous build; each list the worker abandoned at a geometry change,
with its progress; each level's first publication; all stamped with the process's uptime. A stdin script read
`world.residency` and `world.counters sdf.mesh-gi` every 1,200 ticks.

**Leg 1: Direct3D 12, shared store, the spawn view held still, Medium, the day moving.**

| Uptime | Event |
|---|---|
| 46 s | First scene: 6,364 draws, the floors (`lt-bake-pending=True`) |
| 59 s | Second scene: 6,971 draws; 8,247 changed draw entries, of them about 4,122 same-place swaps (floor to first detail) and 3 new places; the union rule fired, so every probe retraced; the bake was not pending, so nothing paced it |
| 71 s | The worker listed 49,152 traces under the new scene |
| 75 s, 95 s, 115 s | Levels 0, 1 and 2 published for the first time |
| to 840 s | No further scene build; level 0 behind its age target under the moving sun, 47 times |

The scene upload for the second scene was 295,877,828 bytes, and the one selection job behind it waited
11,446.8 ms from submission to answer (`trace-jobs=[…,select:1/49152u/11446.8ms,…]` at the first poll). The
shared session residency and the nested worlds staged nothing in this leg (`groups=0`): nothing looked at their
screens.

## What was not run

The brief asked for this lane's own warm and cold legs on both backends, with `dotnet-trace`, PIX,
`world.residency` and the convergence checks, through a build that logged each rebuild's trigger (changed draws
split into same-place swaps and new or gone places, whether the union rule fired, whether the bake was pending,
the seconds since the previous build) and each abandoned list's progress. That build was made, and every
windowed start was refused by host admission for memory (the windowed World's learned peak against the free
memory the host had), so no leg ran. These readings remain owed and would firm up fixes 2 to 5 and 8:

- per rebuild: the trigger split above, and `world.residency`'s `trace-jobs`, `trace-scene-last-change-bytes`,
  `work-budget` (share, credit, granted against charged) and the floor line (`floor-kept`, `floorless`,
  `floor-bytes.static`), on a warm and a cold boot on each backend;
- `world.counters sdf.mesh-gi`'s `gpu-cancelled`, `gpu-trace-nanoseconds` and `gpu-relight-nanoseconds` over the
  same boots, for the GPU time a stable settle actually needs (fix 8);
- why the Vulkan runs churn more than the Direct3D 12 ones on the same world: Vulkan's mesh region keeps a
  contiguous destination where Direct3D 12 maps one (`SdfResidencyGrant.Mapped`), which plausibly presses the
  budget and the bias sooner *(inference, unverified)*.
