# Why the lighting takes minutes to settle after a boot

A research record for [the illumination plan](../illumination.md), not a plan: it has no steps of its own, and
what it finds is scheduled through the plan's build order and
[open items](../open-items.md#lighting-and-gi). It loses to the code; recheck a symbol before building on it.

**Labels.** *Code* is read in source at the tree this record was written on and cited by symbol. *Leg* is one of
this lane's own windowed boots with added logging ([this lane's legs](#this-lanes-legs)). *Log* is another lane's
World stderr from a windowed boot of the same world on the same machine (an RTX 4070), at that boot's build of the
integration branch ([earlier runs](#earlier-runs)). *Inference* is reasoning from these and says so. Seconds of
wall time are uptimes stamped by the legs' logging or the volume's own `[sdf.mesh-gi] done … seconds=`; they were
taken beside other lanes' work and say how long, not how fast. The counts beside them (scene builds, probes traced
and relit, jobs, bytes, GPU nanoseconds from the jobs' own timestamps) do not depend on load.

## The answer in plain terms

The volume's own work is small. A boot that looks at the spawn and holds still traced the whole Medium window
(49,152 probes) once and published every level within two minutes: levels 0, 1 and 2 at 75, 95 and 115 s of
uptime, with 0.27 s of GPU time for all its traces and about 9 µs of GPU a relight (leg 1). At High in the gallery
hall, the same: 73,728 probes, level 0 at 89 s (leg 2).

The minutes come from a boot that sees another copy of the same world. In the gallery lobby the big painting
shows the boot world through the shared session residency (`routed$session$18$0$shared`), and the painting wing
brings the garden. Each of those residencies then holds its own copy of the world's meshes in the one memory
pool and runs its own full lighting volume on the one background budget (leg 3, standing at
`debug.go wonderland`):

- **The meshes press the pool and the detail churns.** The main world's mesh budget fell from 3.75 GB to
  1.36 GB as the session residency named 0.87 to 1.15 GB of its own copy, the shared detail bias rose from 0 to
  6 half rungs, and both residencies retiered and coarsened. Each swap is a new irradiance scene: the session's
  scene went 581 M → 243 M → 173 M triangles, the main's 504 M → 614 M, every change past 64 draws, so each one
  retraced its whole window and threw away the traces in flight (43,008 of 49,152 at one session rebuild).
- **The second volume takes the budget.** Of the 18.8 s of GPU the budget granted background work over the run,
  the session residency's jobs used 16.1 s and the main world's 2.5 s. The main volume landed no probe for its
  first five polls, never finished its first fill in 900 s (41,856 of 49,152 probes cached at the end), and its
  trace jobs waited 3.1 s on average from submission to answer.
- **The grant is the floor.** At boot the frame's own GPU time (14 to 28 ms) overruns the 16.67 ms period, so
  background work gets its least, 1 ms a frame, about 18 to 23 ms of GPU a wall second, in every leg.

On Vulkan the same boot is worse, because the mesh budget falls further: the world's to 578 MB, the bias to 14
half rungs, 2,121 of the world's groups coarsened, and the two large scenes rebuilt 30 times (27 of them past the
union rule) before the bias settled. The world's volume cached no probe for its first eight polls and published
level 0 at about 500 s (leg 4).

The earlier runs that took 577 s and 912 s to a first main `done`, and the Vulkan runs that never reached one,
show the same signature: the session residency's scene (6,600 to 6,800 draws) built beside the main one, 11 to 46
large rebuilds, most of them past the union rule.

The mechanisms, each with its numbers below:

1. **Every detail change is a geometry change.** The scene's key is each static draw's mesh, transform and
   material, and a new tier is a new mesh.
2. **A second residency of the same world doubles the meshes on one pool**, which raises the shared detail bias
   and starts coarsening waves in every residency.
3. **A wave of changes retraces the whole window.** Past 64 changed draws the change is named as one box that
   holds them all, which every probe meets.
4. **Every geometry change relights every probe from scratch.** A probe reuses its point-light cube only under
   the very geometry it was relit under.
5. **A change discards the work in flight.** The batch being traced or relit when the scene moves is dropped
   whole, and its queued GPU jobs are cancelled.
6. **Every residency runs a full volume, first come on one budget.** A painting world of 74 draws traces 49,152
   probes as the main world does, and whichever residency is pumped first takes the grant.
7. **The grant is 1 ms a frame while the frame overruns its period.**

The rest of this record gives the numbers for each question the brief asked, then [the ranked
fixes](#the-ranked-fixes).

## The scene rebuilds and what triggers each

**What a rebuild is.** `SdfMeshIrradiance.RefreshScene` compares the frame's static-draw key with the installed
scene's and starts `StartSceneBuild` when they differ. The key (`StaticKey`) is an order-independent sum of
`DrawKey`, a hash of each static draw's `(Mesh, ObjectToWorld, Material)` *(code)*. `SdfMeshIrradianceScene.Build`
reuses a geometry it already holds by its mesh (`GeometryKey.Of`) and builds the rest, so a rebuild costs CPU for
the new meshes' hierarchies, a new top level over every draw, and a GPU upload *(code)*.

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

So the lighting's geometry is the camera's detail and the pool's pressure, not the world's.

**What paces a rebuild.** Only a frame whose bake still owes opaque static meshes is paced:
`SdfMeshIrradiance.Stage` calls `RefreshScene` at once when `bakePending` is false, and otherwise only when
`ScenePendingDue` holds (no build running, the installed scene's traces listed done, and `PendingSceneInterval`,
four seconds, since the last build began) *(code)*. `bakePending` is `SdfMeshPathBake.IrradiancePending`, which a
retier wave turns on while its replacements bake or land and off once their meshes reach the dressed frame. Every
rebuild after the first in every leg logged `lt-bake-pending=False`, so none of them was paced *(leg 1 to 3)*.

**What triggered each, in the legs.** The logging split each build's changed draws into same-place swaps (a new
mesh under the same transform) and new or gone places.

| Leg | Uptime | Scene | Changed draws | What it was |
|---|---|---|---|---|
| 1, 2, 3 | 29 to 46 s | main, 6,360 draws | first | The floors |
| 1, 2, 3 | 44 to 60 s | main, 6,971 draws | 8,245 to 8,247, of them about 4,120 swap pairs | Floor to first detail, every boot; union, all 49,152 retraced |
| 3 | 111 s | session, 6,808 draws | 7,165: 4,965 new places, about 1,100 swap pairs | The session's own arrival; union; 23,040 traces abandoned |
| 3 | 204 s | main, 6,984 draws | 5,011: about 2,499 swap pairs | Finer tiers for the lobby view (504 M → 614 M triangles); union; the main volume was relighting, so all 49,152 retraced |
| 3 | 224 to 273 s | garden, 74 draws | 18, 16, 14 | Coarsening under the rising bias; each abandoned the garden's list |
| 3 | 266 s | session, 6,766 draws | 6,974: about 3,487 swap pairs | Coarsening (581 M → 243 M triangles, bias 4); union; 43,008 of 49,152 traces abandoned |
| 3 | 275 s | session, 6,732 draws | 1,358: about 679 swap pairs | Coarsening (→ 173 M, bias 6); union |
| 4 | 83 to about 360 s | world and session, 6,668 to 6,980 draws | 293 to 8,810 each | 30 large rebuilds, 27 past the union rule, 2 to 56 s apart; the world's scene 577 M → 353 M → 310 M → 174 M → 155 M triangles as the bias climbed to 14 |

Legs 1 and 2 built no scene after the first detail: with only the world's residency staged (every other
residency read `groups=0`), the detail bias stayed 0 and the region budget at 3.75 GB *(leg 1, 2)*. In leg 3 the
bias read 0, then 2 by the second poll, 4 by the sixth and 6 by the seventh, in the world, the session and the
garden alike; the world's `region-budget` fell 3,750,754,532 → 2,142,037,574 → 1,361,642,372 bytes while its own
`region-named` stayed near 1.3 GB, and the session named 1,133,913,312 bytes, later 871,358,252 *(leg 3)*. On
Vulkan the world's `region-budget` fell to 578,415,426 bytes by the fourth poll, the bias climbed one to three half
rungs a poll to 14 by the twelfth and settled at 12, and the world coarsened 2,121 groups and the session 1,008
*(leg 4)*. *Inference, unverified:* Vulkan's mesh region keeps a contiguous destination where Direct3D 12 maps one
(`SdfResidencyGrant.Mapped`), so the selector holds back replacement room on Vulkan and each residency's budget is
smaller for the same pool.

**In the earlier runs.** Large-scene builds (6,300 to 7,000 draws, both large residencies) before the main
volume's first `done`, or in the whole run where none came, and how many added more than 64 new geometries (each
new geometry is at least one changed draw, so every one of these crossed the union rule):

| Run | Backend, store | Large-scene builds | Over 64 new geometries | Geometry triangles built | Main volume's first `done` |
|---|---|---|---|---|---|
| A | Direct3D 12, shared | 40 | 31 | 135.6 M | 577 s |
| D | Direct3D 12, shared | 11 | 6 | 61.6 M | 912 s |
| F | Vulkan, shared | 46 | 30 | 117.5 M | none within the run |
| G | Vulkan, shared | 45 | 33 | 116.0 M | none within the run |
| H | Vulkan, empty (cold) | 36 | 29 | 117.0 M | none within the run |
| C | Direct3D 12, empty (cold) | 3 | 2 | 38.7 M | 206 s |
| B | Direct3D 12, shared | 2 | 2 | 25.5 M | 56 s |

In runs A, D, F, G and H the session residency's scene built beside the main one; in B and C it did not. Run A's
main scene fell 613 M → 151 M triangles over its rebuilds while the bake's own triangles fell 41.5 M → 19.9 M:
the coarsening of leg 3, run to its end *(log)*.

## Relight scope per rebuild

**Which probes retrace.** A scene build records the world boxes of the draws it added or removed
(`SdfMeshIrradiance.ChangedBoxes`), and the selection kernel (`sdf-probe-select.comp.hlsl`, with
`SdfMeshIrradiance.Affected` its CPU twin) retraces a probe only when a box overlaps its placement region or meets
one of its rays before the recorded hit *(code)*. More than `MaxChangeBoxes` (64) changed draws are named as the
one box that holds them all, so the selection job stays bounded *(code)*. Every large rebuild in the legs fired
it (`:union` in the log): the floor-to-detail swap on every boot, and each coarsening wave *(leg 1 to 3)*.

**Which probes relight, and how deeply.** Every probe of the window relights after any geometry change, whether or
not it retraces, and none of them keeps its point-light part: `reuseLocal` in `SdfMeshIrradiance.Process`, and the
same test in `RelightGpu`, requires `previous.Geometry == geometry` *(code)*. A relight that cannot reuse its local
cube traces, from every hit texel, one ray toward each shadowing point light whose range reaches the probe
(`SdfMeshIrradiance.RelightRays`: `lights × (HitCount + 1)`, with `HitCount` 144) *(code)*. Beside the debug
world's stage a probe has up to 78 such lights, about 11,300 rays, against 145 a directional light when it reuses
([the mesh-GI reference](../../../.claude/skills/rendering/references/mesh-gi.md) records the 78).

**Per rebuild, in probes.** A rebuild past the union rule owes the whole window's traces (49,152 at Medium, 73,728
at High) and the same number of full-depth relights, and its bounce then takes further sweeps *(inference from the
code above and `SdfMeshIrradiance.LevelsOf`)*.

## Trace job count, latency and admission waits

**The jobs, per residency, in leg 3's 900 s** (`world.residency`'s `trace-jobs=[kind:jobs/units/mean latency]`
and `gpu-work=[…]` at the last poll):

| Residency | Traces | Relights | Selections | Camera samples | Service GPU time |
|---|---|---|---|---|---|
| world | 428 jobs, 97,096 origins, 3,077.7 ms | 1,116 jobs, 523.6 M rays, 402.9 ms | 1 job, 12,038.9 ms | 323, 1,887.2 ms | 2,454 ms |
| session | 1,343 jobs, 350,129 origins, 127.5 ms | 20,340 jobs, 1,474.8 M rays, 104.0 ms | 54 jobs, 2,481.2 ms | 3,963, 120.7 ms | 16,059 ms |
| garden | 213 jobs, 63,657 origins, 774.3 ms | 635 jobs, 12.7 M rays, 2,455.0 ms | 62 jobs, 177.3 M tests, 363.4 ms | 227, 2,516.2 ms | 335 ms |

The world's probe cache read 0 for the first five polls, 38,400 by the ninth, and 41,856 at the end; the session's
reached 49,152 by the ninth *(leg 3)*. In leg 1, alone, the world's jobs answered in 43.3 ms (trace) and 88.9 ms
(relight) on average.

On Vulkan, over leg 4's 900 s: the world's service 22,641 jobs and 7,244 ms of GPU, the session's 30,387 jobs and
12,981 ms, the garden's 3,090 jobs and 809 ms, of 21.0 s granted. Selection grew with the remembered changes: the
garden's 305 selection jobs tested 4.6 G probe-ray-box pairs and the session's 206 tested 2.4 G
(`select:305/4607892174u`, `select:206/2406076982u`), because each selection walks every probe's rays over every box
newer than its trace, up to `MaxChanges` (16) changes of `MaxChangeBoxes` (64) boxes *(leg 4, code)*.

**Who goes first.** Every residency's service draws on the presentation's one `SdfGpuWorkBudget`, and "the services
take from it first come, in the order their residencies are pumped" (its summary) *(code)*. Nothing orders them by
what the viewer sees: in leg 3 the residency rendering a painting took 85 % of the grant.

**Work thrown away.** The worker works through its list a batch of `BatchProbes` (384) at a time *(code)*. A scene
change advances the geometry version (`AdvanceGeometry`), and then:

- `Batch` drops the batch's results under the gate when `Stands` fails, after both its trace and its relight have
  run (the `if (!Stands(snapshot: in snapshot))` before landing);
- `RelightGpu` drops the batch before handing its relight over, when the trace landed but the scene moved;
- `ListGpu` throws on a moved scene, ending the listing;
- `SdfMeshProbeGpuWork.TryTake` cancels a queued query whose scene the volume no longer keeps (`KeepsGpuScene`,
  `KeepsGpuPublication`) *(code)*.

The legs logged what each rebuild found in the worker's list: 768 of 49,152 traces done when leg 2's switch to High
replaced the layout (the hall-indirect lane's "768 to 1,152" is this), 23,040 and 43,008 of 49,152 at the session's
rebuilds, 12,288 and 21,504 at the garden's *(leg 2, 3)*. The 768 and 23,040 had landed in batches before the
change; the batch in flight at each change was dropped.

**Admission waits.** A query also waits:

- behind a scene upload, which runs only with no query in flight and admits none while it runs (`m_sceneUpload` in
  `TryTake`) *(code)*: the first selection behind the boot's ~300 MB upload waited 11.4 s (leg 1), 12.5 s (leg 2)
  and 12.0 s (leg 3) from submission to answer;
- until every mesh the scene names stands in the mesh region (`SdfWorldTables.TryProbeScene`); one that waits past
  `AdmissionWait` (2 s) is cancelled *(code)*: `gpu-cancelled` read 4 in leg 1 and 8 in leg 2;
- for room in the frame allowance while other background work is queued (`SdfGpuWorkAllowance.Admits`) *(code)*:
  the services' jobs were held by the frame's deadline 38,805 times against 2,079 times by the budget in leg 1
  (`deadline-holds`, `budget-holds`).

## Upload sizes

- **The rate.** Background uploads share `SdfGpuWork.UploadBytesPerSecond`, 4 slots × 1 MiB × 60 = 240 MiB a
  second of wall time, across every residency's scene uploads and every mesh region's landing *(code)*.
- **The first scene.** 295,877,828 bytes (leg 1), 300,139,504 (leg 2); the world's whole scene uploads in leg 3
  came to 509,327,280 bytes and the session's to 488,345,512 *(leg)*. The budget uploaded 1.96 GB in leg 1 and
  4.95 GB in leg 3, mesh landing included (`work-budget`'s `uploaded`).
- **Every later scene re-uploads its whole prefix**: the top level's nodes, its braid entries and one record per
  draw (`SdfMeshIrradianceScene.PrefixWords`), whatever geometry it shares *(code)*. At 6,800 to 7,000 draws the
  prefix is 12.6 M to 15.2 M words, 50 to 61 MB *(leg 3)*; run A's 40 large rebuilds summed 254 M prefix words,
  1.02 GB, about 4 s of the upload rate on its own, during each of which no query ran *(log)*.
- **New geometry.** Each new geometry uploads its packed nodes, triangle ordinals and braid frames
  (`CreateGpuExport`) *(code)*.

## What the allowance grants against what lighting could use

- **The grant.** Background GPU work takes at most `SdfGpuWork.GpuShare` (a quarter) of the display period, less
  whatever the frame's own GPU time leaves short of nine tenths of the period, never under
  `SdfGpuWorkBudget.LeastFrameSeconds` (1 ms) a period (`SdfGpuWorkBudget.ShareOf`) *(code)*.
- **What it was at boot.** The frame's own GPU time read 13 to 28 ms a frame against the 16.67 ms period, and the
  share sat at its floor, 0.060 (1.00 ms a frame), from the first poll to the last in every leg. The budget granted
  13.5 s of GPU over leg 1's 740 s of service and 18.8 s over leg 3, and background work charged all of it
  (`work-budget=[gpu-share:0.060,…,gpu-granted:13504ms,gpu-charged:13507ms,…]`) *(leg 1, 3)*.
- **What lighting could use.** Leg 1's whole first fill and 27 sweeps of relights under the moving sun: 49,152
  traces in 0.27 s of GPU (`gpu-trace-nanoseconds` 274,499,200) and 1,342,663 relights in 12.45 s
  (`gpu-relight-nanoseconds` 12,454,226,784), about 5.6 µs a trace and 9.3 µs a relight; the ray-query structures
  took 0.54 s more *(leg 1)*. *Inference:* a still-scene settle of the Medium window (one trace and six to eight
  relights a probe) is 3 to 4 s of GPU, which the floor's 18 ms a second stretches to a few minutes; the moving sun
  alone needs 2,496 relights a second to hold level 0's age target, about 23 ms of GPU a second, more than the
  grant, and level 0 printed `[sdf.mesh-gi] behind` 47 times in leg 1.
- **The authored probe budget never binds.** `render.indirect.budget` grants 122,880 relights and 122,880 traces a
  second (`worlds/standard/quality.puck`) *(code)*; the GPU share is the limit.
- **The floor still costs frames.** The budget's own judge (`FramesMissedBehind`, an upper bound) counted 13,159 of
  22,643 frames in leg 1 and 28,692 of 30,705 in leg 3 as frames background work may have cost, with up to 69.6 ms
  of background work directly ahead of one frame in leg 3 (`most-behind`) *(leg)*. The largest single jobs in leg 3
  were ray-query structure compactions of 16.9 and 19.2 ms (`most-job`), admitted whole because a job with nothing
  queued ahead runs whatever its price (`SdfGpuWorkAllowance`) *(code, leg 3)*.

## Does the lighting depend on render detail tiers, and could it light from the floor

**It does, entirely.** The scene is the static draws' meshes, which are the tier each group shows for the view.
The trace also reads those meshes' words in place from the mesh region (`TryProbeScene`;
`SdfMeshIrradianceScene.CreateGpuExport` references triangles "in an already placed mesh region"), so it can trace
only what the region holds *(code)*. Ray queries build their bottom levels from the same placed meshes
(`SdfMeshProbeGpuWork.Rays`), which is why `world.rays off` made no difference: the churn is upstream of the walk.

**A stable proxy exists for most groups.** Each group's floor is its coarsest tier; a group arriving with nothing
to draw lands it first and keeps it with its own claim while a finer tier draws (`Group.KeptFloor`), named ahead of
the draws so its words stay in the mesh region (`ServeAhead`), and a refused finer tier falls back to it
(`SdfMeshPathBake.Floor.cs`) *(code)*. The floor is chosen by the plan, not by any camera or by the pool's
pressure. In leg 1, 6,106 groups drew their floor, 1,449 drew a finer tier with their floor kept, and 2 had none
(`floor-shown`, `floor-kept`, `floorless`); the static floors held 135 MB of the region and the dynamic ones
415 MB (`floor-bytes.static`, `floor-bytes.dynamic`) *(leg 1)*. Its limits *(code and the plans)*:

- not every group keeps one: a group taken whole from the library, or replanned with nothing shown, keeps none
  until it arrives again (`DescribeFloors`);
- whether a floor is good enough to light from is unjudged. [The plan's stable trace
  mesh](../illumination.md#after-the-gpu-trace) and [streaming decision
  9](../streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand) favour a trace tier
  sized by the probe spacing, with the floor wherever the spacing asks for nothing finer, after a bake-time check
  that the floor keeps thin walls, recesses and separations.

**Inference on the saving.** A scene that ignores view retiers and the bias builds once per arrival. It also skips
the floor-to-detail swap every boot pays at about 45 to 60 s, a whole-window retrace of every volume.

## What an incremental relight would need

1. **Changes that do not collapse.** The selection kernel walks a probe's rays over every remembered box on one GPU
   thread, which is why `MaxChangeBoxes` unions a large change. Kept as boxes, a wave of thousands needs a
   structure the kernel can query in logarithmic time (a box hierarchy or a coarse grid of change cells), so a swap
   near the hall retraces the hall's probes and not the valley's.
2. **A probe's own geometry version decides, not the volume's.** A batch traced under geometry *n* that lands under
   *n + 1* is still a correct measurement of *n*; landing it with its version and letting the next selection's
   change test decide whether it retraces keeps the work instead of dropping it (`Batch`, `RelightGpu`, `ListGpu`,
   the query cancellation in `TryTake`).
3. **Local cubes kept across changes they do not reach.** A point light's shadow segments from a probe's hits are
   tested against the change boxes as the rays are (`Affected` already extends rays by the sheet thickness); a probe
   none of whose segments meets a box keeps its local cube, so only the directional part and the bounce relight.
4. **An incremental top level.** The prefix is uploaded whole per scene; a top level whose unchanged subtrees keep
   their addresses would upload only what moved, and queries would wait for less (they keep the old root during an
   upload, `CompleteSceneUpload`; the cost is the exclusion and the bytes).
5. **Bounce follows on its own.** A geometry change near a probe changes what its neighbours' relights read; the
   existing sweep convergence (`ConvergedShare`, the sweep rule in `Work`) already carries that outward.

With a camera-independent trace scene (fix 2 below), items 1 to 4 matter only for real geometry changes (an
author's edit, a world arriving, a door that moves the static set).

## The ranked fixes

Each fix names its expected saving, the evidence, and the check that shows it worked. Savings are read from the legs
and runs above; none is measured on changed code.

1. **One lighting budget ordered by what the viewer needs, and volumes sized by their display share.** Replace the
   residencies' first-come draw on `SdfGpuWorkBudget` with one lighting order (the plan's step 6: minimums first,
   pixel share second), and give a residency whose views are a painting or a screen only the levels its glass's
   display share asks for (streaming decision 9's lighting floor). *Saving:* in leg 3 the main volume had 13 % of
   the grant and never finished its first fill in 900 s; alone (legs 1, 2) it published level 0 in 75 to 89 s.
   The session's 16.1 s of GPU and the garden's 49,152-probe window for 74 draws are the work to cut. *Check:*
   leg 3 repeated: the world's `probe-cache-probes` reaches 49,152 and its level 0 publishes within leg 1's time,
   and the session's and garden's `trace-jobs` units fall to their coarse levels. *Built:* the services' turns
   (`SdfGpuWorkBudget.Takes`) and the nested-view table's `indirectLevels` (`SdfFrame.IndirectLevels`); the check is
   owed.
2. **Trace a scene neither the view nor the pool moves.** Build the irradiance scene from a per-group trace mesh
   the plan chooses (the floor where a lighting check admits it, a probe-spacing tier where it does not), not from
   `Group.Shown`; keep it resident as floors are now. This is the plan's stable trace mesh and streaming step 9.
   *Saving:* every rebuild after the arrivals: the floor-to-detail retrace every boot pays (8,247 changes at 44 to
   60 s), leg 3's four coarsening and finer-tier rebuilds, and runs A, F, G and H's 36 to 46 large rebuilds.
   *Check:* a warm boot at `debug.go wonderland` logs no `[sdf.mesh-gi] scene` line after its arrivals while the
   bias moves, and a wave of camera tier changes logs none.
3. **Share one residency's meshes between the world and a session that renders the same world.** The session
   residency holds its own copy of the boot world's meshes (871 MB to 1.13 GB named) in the pool the world's
   residency draws on, which is what raised the bias to 6 in leg 3. `SdfMeshBakeShare` already has one bake of a
   geometry serve every residency that plans it, but each residency still lands the mesh in its own region and
   counts it against the pool. A session handed the same scene, or a subset of it, can read the world residency's
   region (the disclosure plan decides what it may read). *Saving:* the
   bias rise and every coarsening wave it causes in every residency, and about 1 GB of device memory while the
   painting is in view; on Vulkan, the bias of 14 and 2,121 coarsened world groups of leg 4. *Check:* `detail-bias`
   stays 0 in legs 3 and 4 and the session's `region-named` is near zero. This
   is a streaming-plan change; it is listed because it is the trigger of the churn the lighting pays for.
4. **Keep work a change does not invalidate.** Land a batch with the geometry version it traced against and leave
   the retrace decision to the next selection, instead of dropping it in `Batch` and `RelightGpu` and cancelling
   its queued jobs. *Saving:* each batch in flight at a rebuild; leg 3's session rebuilds found 23,040 and 43,008
   traces done, and the batch after them was lost. *Check:* `sdf.mesh-gi.traced` keeps rising across a forced
   rebuild (`world.detail 1`, then `world.detail auto`), and `gpu-cancelled` does not.
5. **Keep local cubes across changes that miss them.** Test each probe's point-light shadow segments against the
   change boxes and reuse its local cube when none meets one (`reuseLocal`'s geometry clause). *Saving:* a relight
   after a change costs directional rays (145 a light) instead of every point light in range (up to about 11,300 a
   probe beside the stage) for every probe the change does not reach. *Check:* a relight job's `reused=` count in
   `[sdf.gpu-work] slow` and the relight units after a rebuild; the point-light law's values unchanged within
   `ConvergedShare`.
6. **Name a large change without unioning it.** Replace the one-box union past `MaxChangeBoxes` with a structure the
   selection kernel queries per ray in logarithmic time. *Saving:* a wave retraces only the probes the swapped draws
   reach, instead of the whole window; every large rebuild in the legs and 31 of run A's 40 crossed the rule. The
   same structure bounds the selection's own cost, which reached 4.6 G box tests for the garden's 74-draw volume in
   leg 4. *Check:* the selection's `most-job` boxes and tests stay bounded at a wave of several thousand changes,
   and the traced count per rebuild falls below the window.
7. **Let a loading boot spend the GPU on its lighting, and bound the jobs admitted with nothing queued.** Before the
   first view is shown (or while a view is withheld for its lighting), run the volume's work unbudgeted as a
   converging frame's drain does (`SdfGpuWork.Drive`, `SdfGpuWorkAllowance.Unbounded`); and cut structure builds to
   the allowance rather than admitting a 17 to 19 ms compaction whole. *Saving:* a still-scene settle is 3 to 4 s of
   GPU that the 1 ms floor stretches to minutes; the frames the floor's oversized jobs cost. *Check:* leg 1 repeated:
   level 0 publishes in seconds behind a held first frame, and `most-behind` falls under the frame's headroom.
   *Built differently:* no frame is held; a seen volume's first light takes the owed allowance an arrival's bake work takes,
   the GPU time the frame leaves idle (`SdfMeshIrradiance.OwesFirstLight`, `SdfGpuWorkBudget.OwedFrameSecondsOf`), and
   compactions are cut to the allowance by their own measured rate; the check is owed.
8. **Pace every rebuild by trace progress, not only while the bake owes meshes.** Apply `ScenePendingDue`'s gate
   whatever `bakePending` says. *Saving:* bounds the loss while fixes 2 and 4 are built, at the cost of lighting a
   scene up to one trace behind the drawn one; worth landing only with them. *Check:* no two `[sdf.mesh-gi] scene`
   lines of one volume less than one window trace apart.
9. **Upload only the top level's change.** Item 4 of [what an incremental relight would
   need](#what-an-incremental-relight-would-need). *Saving:* 50 to 61 MB a rebuild and its exclusion of queries;
   1.02 GB over run A; small once fix 2 lands. *Check:* `trace-scene-last-change-bytes` for a one-draw change near
   the draw's own share.

## This lane's legs

Each leg booted `worlds/debug` windowed with `--devices none` on an RTX 4070 from this lane's build of the
integration tip, with the shared mesh store unless it says otherwise, and with throwaway logging added to
`SdfMeshIrradiance` (deleted before this record was committed): each scene build's changed draws split into new
places, gone places and same-place swaps, whether the union rule fired, whether the bake was pending and the
seconds since the previous build; each list the worker abandoned at a geometry change with its progress; each
level's first publication; all stamped with the process's uptime. The logging names no residency, so lines are told
apart by their scene's draw count. A stdin script read `world.residency` and `world.counters sdf.mesh-gi` every
1,200 ticks.

| Leg | Backend | View | Tier | What it showed |
|---|---|---|---|---|
| 1 | Direct3D 12 | the spawn, held still | Medium | Only the world residency staged; two scene builds; levels published at 75, 95 and 115 s; the grant at its floor throughout |
| 2 | Direct3D 12 | the gallery hall (`body.pose 31.25 4.57 28.52 273`, `world.indirect high`), then along the hall | High | Only the world residency staged; the switch to High abandoned 768 of 49,152 traces; level 0 at 89 s, levels 1 and 2 by 126 s; no scene build after the first detail |
| 3 | Direct3D 12 | `debug.go wonderland`, then the hall | Medium | The session and garden residencies staged; bias 0 → 6; four rebuilds past the first detail; the world's first fill unfinished at 900 s |
| 4 | Vulkan | `debug.go wonderland`, then the hall | Medium | As leg 3 with a smaller budget: bias 0 → 14; 30 large rebuilds; the world's level 0 at about 500 s |

Every leg's store was warm (leg 4's bake `done` line: `dispatches=0`, `loaded=1226`). No leg ran cold; the cold
readings here are runs C and H.

## Earlier runs

Each run booted `worlds/debug` windowed from a lane worktree on the integration branch; the World's stderr was kept
by the `puck` server under its per-run directory, and the commit that adds this record names each run's log. The
backend is read from the device line (`[gpu-optional] … raytracing-tier=` on Direct3D 12; the `[backing] sparse-…`
line on Vulkan), the store from the compiled-world path.

| Run | Backend | Store | What it shows |
|---|---|---|---|
| A | Direct3D 12 | shared | The 577 s first `done` after 40 large rebuilds; then High from scratch on a settled scene in 60 s (73,728 traced, 589,824 relit) |
| B | Direct3D 12 | shared | Two large builds; first `done` 56 s (49,152 traced, 296,832 relit) |
| C | Direct3D 12 | empty (cold) | Three large builds while a cold bake ran; first `done` 206 s |
| D | Direct3D 12 | shared | 11 large rebuilds; first `done` 912 s; the session volume's 628 s |
| F | Vulkan | shared | 46 large rebuilds; the main volume never reports `done` |
| G | Vulkan | shared | 45 large rebuilds; never reports `done` |
| H | Vulkan | empty (cold) | 36 large rebuilds; never reports `done` |

In run A the session volume landed 266,606 traces and 1,596,160 relights against the main volume's 93,355 and
915,005 *(log; the session's volume is told apart by its scene's draw count)*.

## What was not run

- **Cold legs on this tip.** The legs ran on the shared store; the cold readings are runs C (Direct3D 12, 206 s with
  a scene that barely moved) and H (Vulkan, 36 large rebuilds, no main `done`), at their own builds. A cold boot
  adds the bake's own draw on the one budget, which run C shows costs about 150 s against run B's warm 56 s on a
  still scene.
- **Profilers.** No `dotnet-trace` or PIX capture was taken: the counts above (jobs, GPU nanoseconds from the
  jobs' own timestamps, grants, holds, bytes) answered every question the brief asked of them, and the host side of
  a rebuild (up to 2.2 s of CPU a scene build in the legs, the `seconds=` field) is not what holds the lighting. A capture of
  one trace job against one relight job on each backend remains the plan's owed reading for the ray-query rate.
