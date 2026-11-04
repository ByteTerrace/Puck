# ground-frame: premises for Next items 0 and 1, checked against the code

Lane `ground-frame`, read-only. At the main landing. Every claim below cites the
source read in this pass. Usage and symbol questions were answered by text search over `src/` (Grep), not by
`puck references`/`puck declarations`: no lane CLI was built, so no lease was taken. A lane that renames or deletes a
symbol named here runs `puck references` on it first.

Numbering follows the brief: P1 effector probes, P2 camera clearance, P3 lockstep relight, P4 dressing and motion
staging.

---

## P1. The effector surface probes

### What is actually there

- **The ring.** `WorldEffectorSolver.TryProbeSurface` (`src/Puck.World.Client/WorldEffectorSolver.cs:239-260`) builds a
  `SurfaceQuery`, scans a `[ThreadStatic]` 64-entry ring (`:263-265`), and on a miss writes the answer to
  `answers[NextAnswer]` **unconditionally** (`:256-257`). `SurfaceQuery.Matches` begins with `(Field is not null)`
  (`:270-272`), so a fieldless answer can never match. **Confirmed.**
- **Where the fieldless probes come from.** `WorldSessionStampSource.StaticField => null`
  (`src/Puck.World.Client/WorldSessionStampSource.cs:31-33`), so every body in a routed or session scene probes with
  no field. `Probe` returns false at once for a null field (`WorldEffectorSolver.cs:285-291`), so these probes are
  cheap in themselves. Their cost is the eviction. The arithmetic matches the trace: 8 field-backed probes plus 56
  fieldless ones is exactly 64 writes a frame. That clears the ring every frame, so a bit-identical probe from the
  frame before finds its answer gone. **Confirmed.**
- **Probes whose answer is thrown away.** This is not in the plan. `ApplyEffectors` resolves the target before it
  tests the gate (`WorldCreationInstances.Effectors.cs:82-98`), so a probe runs even when `when` does not hold. A
  planted effector inside its window returns the latched target and never reads the fresh probe
  (`ApplyPlant`, `:419-424`). A resting plant reads the fresh probe only to compare its drift with
  `replant.distance` (`:504-522`). The Moth's two feet are exactly this case: `align: 1`, `standoff: 0.1875`, and a
  `replant` of 9 cm (`worlds/debug/avatars/moth.puck:6600-6660`).
- **The gradient.** `Probe` calls `field.TryFieldGradient` at the rebuilt hit point (`WorldEffectorSolver.cs:316-325`).
  `SdfFieldEvaluator.TryFieldGradient` (`src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs:1686-1739`) takes three
  central differences through `TryAxisDifference` (`:1586-1607`). That is six `TryDistance` walks at hit ± 0.01 on
  each axis (`GradientEpsilon`, `:67`). **Confirmed, with one correction:** none of the six taps is at the point the
  march last evaluated (the march accepts within `HitEpsilon` 0.001, `SdfFieldMarch.cs:46,218-231`). A central
  difference never reads its centre, so the march's last sample cannot be reused as it stands. What the march and
  the taps do share is locality: the cull reaches and the primer candidate for nearby points.
- **"Distant bodies are probed every frame." Corrected.** Distance rating already exists and is on by default:
  - `SdfUpdateRates.All` (`WorldRenderSettings.cs:57`; `SdfUpdateRates.cs:15-33`), applied through `HoldsPose`
    (`WorldCreationInstances.Rates.cs:218-230`).
  - At about 3.5 px/m (220 m away, 900 px) a Moth takes the 15 Hz tier (`SdfUpdateRateTiers.BodyHertz`,
    `SdfUpdateRates.cs:88-92`).
  - A tier is a floor in presentation time: a pose holds only while `elapsed + frame <= 1/hertz`
    (`src/Puck.Hosting/PresentationRate.cs:60-72`). At the meadow's 67-74 ms frames that never holds, because 15 Hz is
    66.7 ms. **The rating gives way to the slow frame, so every body poses and probes every frame.** At 60 FPS the
    same bodies would pose every fourth frame.
  - `HeldHertz` (no pose at all) applies only when no view, shadow cascade (`Rates.cs:115-137`) or pose reader can
    see the body. On an unpaced host nothing holds (`PresentationRate.cs:61`). Under lockstep the tiers hold by whole
    ticks (`world.rates` help, `WorldRenderLeverCommandModule.cs:487`).
- **A truly still body does not probe at all.** `Wake` repacks only when the root pose, version or facts changed, or
  the last repack moved a slot (`WorldTransformOwners.cs:55-74`; `Pack.cs:225-233`). So the Moths that probe every frame
  are not bit-still: something moves them, whether idle drivers, interpolation or contact jitter. If their foot tips
  move every frame, fixing only the fieldless eviction will not make their probes hit. Not confirmed from the code;
  the lane measures it first.

### Every caller

- `WorldEffectorSolver.TryProbeSurface`: `WorldCreationInstances.Effectors.cs:713` (`TryResolveTarget`, the
  `surface` kind), plus tests `tests/Puck.World.Client.Tests/CreationEffectorLawTests.cs:418,452,473`.
- `ApplyEffectors` ← `PackRegistration` (`WorldCreationInstances.Pack.cs:660`) ← `PackTransforms` (`:136`). These run
  for the presenter's pool and for each session pool. The solve is single-threaded on the window-pump thread
  (`Effectors.cs:14-18`).
- `TryFieldGradient` (the shared code) has these callers:
  - `WorldEffectorSolver.cs:317` and `WorldSessionSceneEmitter.cs:870` (pick normal), presentation.
  - `WorldEditorPointer.cs:216`, presentation.
  - `SdfMeshAudit.cs:449`, a tool.
  - **simulation:** `WorldSolidField.cs:281` (grip and anchor casts) and `FixedFieldContactSolver.cs:481` (`TryUp`,
    through `IFieldEvaluator`).
  - It is delegated through `SdfBandedFieldEvaluator.cs:236-243` and `UnionField.cs:98-128`.

### Does physics share it?

- `WorldEffectorSolver` is presentation-only float (`:20`), and its answers never reach simulation state.
- What it calls is shared with simulation: `SdfFieldEvaluator.TryDistance`, `TryFieldGradient` and the one march
  `SdfFieldMarch.Run` (`SdfFieldMarch.cs:159`), which is also behind `WorldSolidField`'s `m_query.Raycast`
  (`WorldSolidField.cs:270`).
- The two sides use separate evaluator instances: the presenter builds its own presentation field
  (`WorldFramePresenter.cs:466-531`), and the server builds `WorldSolidField`'s (`WorldSolidField.cs:515-520`).

### Invariant and its enforcement

- **I1a. A reused probe answer is the answer the probe would give now**: bit for bit, or within a stated tolerance
  ε if the lane adopts one, under the same field instance.
  - Enforce it by keeping the answer on its owner (the registration's per-effector arrays, beside
    `EffectorPlantTarget`) and passing it into `TryProbeSurface` by `ref`. That one door replaces the thread-static
    ring.
  - Ownership removes eviction, and it removes the hidden dependence on which thread packs. The second matters
    because a ring keyed by thread silently loses its hits once the job system moves packing to workers (Next item 0
    asks for "off the frame thread").
  - A fieldless probe returns before it reaches the cache.
  - Field identity stays a reference: a rebuild publishes a new evaluator (`WorldFramePresenter.cs:504,528`).
- **I1b. Every caller of `TryFieldGradient` keeps its per-point bits.** A batched or shared-reach gradient must give
  the same `FixedVector3` for each point as the scalar one, because physics reads it.
  - Enforce it with one implementation: the scalar method is the batch of one, never a second formula.
  - The tool is the cull's exact-by-construction property (a cull never changes an answer;
    `tests/Puck.SignedDistance.Tests/SdfFieldEvaluatorCullLawTests.cs` header). A walk that skips less, for example
    six taps sharing reaches computed once at the centre less G·ε, returns identical bits.
  - Changing the gradient's formula (four tetrahedral taps, a forward difference) is a simulation change. Rule 4
    allows it if the hashes are re-recorded, but it is not a presentation fix.
- **I1c. A normal is computed only when something reads it**: at a latch, in an align, at a step's landing. If the
  lane makes the normal lazy, `body.rig`'s `Normal` read-back (`Effectors.cs:783`) is documented as the normal last
  used.

### Mechanisms to reuse, not rebuild

- The pose-rate machinery: `SdfUpdateRateTiers` and its perceptibility rule (`SdfUpdateRates.cs:41-92`), `HoldsPose`,
  `NotePoseRead`. Do not add a second distance cull.
- The perceptibility rule supports a principled cut. An effector's largest correction is its reach (0.42 m on the
  Moth). At 3.5 px/m that is about 1.5 px, under `PerceptibleStepPixels` (2). The cut can be decided by the same rule
  the tiers use, by the owner's existing ruling on barely perceptible trade-offs.
- The counter path: `WorkCounterSet`, `IWorkCounterSource` and `world.counters`
  (`WorldCountersCommandModule.cs:31,49`).
  - `SdfFieldEvaluator.PointWork` (`sdf.point.evaluations`, `sdf.point.instructions`, `SdfFieldEvaluator.Work.cs:9-14`)
    exists but is **not registered**: `WorldBootComposition.cs:164-173` registers `SdfBoundsCache.ProcessWork` and
    leaves it out.
  - `world.counters sdf.rates` already reports bodies posed and held.

### What proves it

The counts must be load-independent, so read them under `world.lockstep`, where the tiers hold by ticks, at the
meadow with a fixed camera, over 600 ticks, before and after:

- probes asked, skipped as fieldless, answered from cache, marched; gradient walks (`sdf.point.evaluations`, once it
  is registered);
- `sdf.rates.bodies.posed` and `.held`.

Headless boots present no frames (`WorldBootComposition.cs:309,359`), so this is a **GPU leg** (windowed or
offscreen).

### Draft brief

> **frame-probes (P1+P2 lane).** Fix the frame thread's two field-query answer caches at the cause.
>
> Effectors:
> - Replace `WorldEffectorSolver`'s thread-static 64-entry ring (`WorldEffectorSolver.cs:239-280`) with an answer
>   owned per effector on the registration (`WorldCreationInstances.Effectors.cs`), passed by `ref` through the one
>   door `TryProbeSurface`.
> - A null field returns before any cache.
> - Probe only when the answer can be used: gate holding or weight above zero; not inside a held plant window
>   (`:82-98`, `:419-424`).
> - Compute the hit's normal only where it is read.
> - Read why the rate-range Moths repack every frame (`WorldTransformOwners.Wake`, `:55-74`) before choosing whether
>   exact reuse suffices or a tolerance is needed. Name the tolerance ε and what bounds it (a fraction of
>   `replant.distance`, or `PerceptibleStepPixels`).
> - Consider not solving an effector whose reach projects under `SdfUpdateRateTiers.PerceptibleStepPixels` in every
>   view that may see it, deciding by the tiers' own rule, not a new distance.
> - Do not change `SdfFieldEvaluator.TryFieldGradient`'s output: physics reads it (`WorldSolidField.cs:281`,
>   `FixedFieldContactSolver.cs:481`). A shared-reach six-tap walk is allowed only if it is the one implementation
>   and bit-identical per point.
>
> Register `SdfFieldEvaluator.PointWork` in `WorldBootComposition.cs` beside `SdfBoundsCache.ProcessWork`, and add one
> frame field-query counter source shared with the clearance (asked, fieldless, reused, marched, sphere casts).
>
> Invariant for the commit body: a reused answer equals the probe's answer now (or is within ε) under the same field
> instance; the owner's slot is the only store.
>
> Proof: the counters at the meadow under `world.lockstep`, before and after; GPU leg granted, one windowed World,
> stopped once read. Build `Puck.World.Client` and `Puck.SignedDistance`. Update or delete
> `CreationEffectorLawTests` if the signature changes. Vulkan boot required only if GPU code changes (none expected).
>
> Stop when the counters show fieldless probes at zero ring writes, a still body's probes reused, and the meadow's
> marched probes a frame at or below the field-backed bodies that move.

---

## P2. The camera clearance cache

### What is actually there

- **Bit-for-bit matching. Confirmed.**
  - `WorldCameraClearance.Clear` (`src/Puck.World.Client/WorldCameraClearance.cs:118-138`) scans a `[ThreadStatic]`
    32-entry ring (`:141-143`). `ClearanceQuery.Matches` compares target, direction, length, scale and both skins by
    float bits, and the radius by raw value (`:146-166`).
  - The match covers `March`'s inputs, and `March` is one `SphereCast` (plus an `Overlap` for an obstructed target)
    (`:169-209`).
- **Where the drift comes from.** The target that reaches `Resolve` has passed through `view.Follow`, the boom's
  second-order easing (`WorldFramePresenter.cs:776-785`), and comes from the body's interpolated pose
  (`:798-805`).
  - A second-order follower's tail, or a resting body's interpolated jitter, moves it by fractions of a millimetre.
    The trace saw `y 1.3671…1.3674`.
  - Which of the two it is was not confirmed. If the body itself does not settle at rest, that is a defect of its
    own, and fixing it lets exact reuse work.
- **Casts a frame.**
  - Fixed casts: the rest probe's `Room` when a close target is authored (`:402`), `Goal`'s `Fits` (`:459`, `:265-272`,
    usually a ring hit on the rest probe's query when no shift applies), the held room at `SurfaceSkin` radius
    (`:491-494`), and under cutaway the face at radius 0 (`:509-512`).
  - Variable casts: up to 6 lift steps plus 4 refinements when crowded (`:290-327`), and `AvailableRoom` when a
    first-person destination is authored (`WorldSeatViewState.cs:332`).
  - The trace's figures, 4 casts at 3.2 ms, 12.8 ms at the meadow (`engine-job-system.md:156`), fit this list. The
    milliseconds are the trace's; this pass did not measure them.

### Every caller

- `WorldCameraClearance.Resolve`, `Pass` and `ApplyView`: `WorldSeatViewState.Clear` (`WorldSeatViewState.cs:298-345`,
  under the seat's own `m_gate`).
- `WorldSeatViewState.Clear`: `WorldFramePresenter.cs:789`.
- `AvailableRoom` (static): `WorldSeatViewState.cs:332`.
- Tests: `tests/Puck.World.Client.Tests/WorldCameraClearanceLawTests.cs`.
- `MotionClearance` (`src/Puck.Cli.Runs/Motion/MotionClearance.cs:31`) only cites it in a comment.

### Does physics share it?

The class is presentation-only (`:43`). `SphereCast`, `Overlap` and `SdfFieldMarch` are shared code (see P1), each
side running its own instance. A camera fix must not touch the march.

### Invariant and its enforcement

- **I2. A reused room never exceeds the room geometry leaves at this frame's inputs.** If the eye could not stand
  within the skin a fresh cast enforces, it cannot under a reused one either.
- A sound tolerance follows from sphere containment:
  - A sphere of radius r centred at t′ + s·d′ lies inside the sphere of radius r + |t′ − t| + L·∠(d, d′) centred at
    t + s·d, for s ≤ L.
  - So a cast made once at radius r + m stays valid while |Δtarget| + L·|Δθ| ≤ m.
  - The cost is that the answer is up to about m shorter than exact.
- Enforce it in one type: the cached answer records its inflated radius, and its `Admits(query)` computes the
  containment bound. The answer lives on the `WorldCameraClearance` instance (per seat), not on the thread.
  `AvailableRoom` goes through the same instance rather than a static.
- Under cutaway, the face query (radius 0, `:509`) feeds `Near`. A conservative face cuts away more, but never into
  the body, because `Near` is clamped by `NearestDepth` (`:516-529`). The lane states this explicitly.

### Mechanisms to reuse

- The same owner-keyed answer cache as P1. Today's two rings are one pattern copied twice
  (`WorldEffectorSolver.cs:261-280`, `WorldCameraClearance.cs:139-166`): a thread-static array, a cursor, a bit
  compare. That is a duplication finding, and one mechanism replaces both.
- The precedent of holding until movement passes a threshold: the far shadow cascade's `ShadowFarMoveMetres` and
  `ShadowFarTurnRadians` (`SdfUpdateRates.cs:72-77`).
- An alternative query to weigh, not required: `SdfFieldEvaluator.TryCertifiedSweep` (`SdfFieldEvaluator.Certified.cs:50-79`)
  is certified and caps its cost by a bounds-query budget. It suits a camera that must never pass inside geometry
  and never spend unbounded marches.

### What proves it

Under `world.lockstep` at the meadow with the player idle, 600 ticks, read:

- sphere casts a frame and reused answers a frame, in the shared counter source;
- the eye placement from `DescribeClearance`, which must stay within m of before.

Before: 4 casts a frame, 0 reused. After: about 0 casts a frame while the target's drift stays inside m.
A **GPU leg**, the same one as P1.

### Draft brief

Folded into **frame-probes** above. Add to it:

> Camera:
> - Replace the clearance ring (`WorldCameraClearance.cs:118-166`) with a per-seat answer that records the radius it
>   was cast at, cast at r + m and reused while |Δtarget| + L·|Δdirection| ≤ m. Choose m from the authored skins, for
>   example a fraction of `SurfaceSkin`.
> - Route `AvailableRoom` through the same instance.
> - First find whether the 0.1 mm drift is the boom follower's tail or the body not settling at rest
>   (`WorldFramePresenter.cs:776-805`). Fix a settling defect at its cause.
> - The march (`SdfFieldMarch`) is shared with physics and stays as it is.
>
> Invariant: a reused room never exceeds the room at the current inputs (sphere containment). Enforced by the
> answer's `Admits`.
>
> Proof: casts and reuses a frame plus the clearance read-back at the meadow under lockstep, before and after.

---

## P3. The lockstep relight

### What is actually there

- **The drain decision. Confirmed.**
  - `Stage` sets `drain = frame.TickLocked && scene && lighting && (m_owed || m_drained != DrainKey())`
    (`src/Puck.SdfVm/SdfMeshIrradiance.cs:538`), then waits for the worker and runs `Drain()` (`:551-563`).
  - `DrainKey()` is `(m_geometry, m_lightingVersion, m_windowVersion, m_layout)` (`:578`).
- **Why a moving sun forces a drain.**
  - `RefreshLighting` hashes each directional light's whole record into its key (`:1073-1077`).
  - Outside lockstep it re-captures at most every `LightingInterval`, 0.1 s of wall time (`:202`, `:1084`).
  - **Under lockstep there is no such limit**, so any change in the sun's floats bumps `m_lightingVersion`
    (`:1098`), which makes the next lockstep frame drain.
- **What a drain costs.**
  - `Drain()` lists every probe of the windows (`List(all: true)`, `:1433`) and relights them all from no bounce,
    pass after pass until a pass moves nothing, up to `MaxSweeps` = 32 (`:205`, `:1434-1445`).
  - It runs on `ProcessorCount - 2` workers (`:181`).
  - Starting from no bounce is the documented invariant: "never of the bounce earlier frames left" (`:1421-1424`).
- **Probe count. Corrected.** The window is `Layout.TotalCount`: level 0 at the tier's dims plus two levels at
  `MediumDims` (`:2441-2456`; `SdfMeshIrradianceProbe.Levels` = `MeshIrradianceLevelCount` = 3,
  `SdfWorldPackage.Mesh.cs:70`).
  - High: 36,864 + 2 × 12,288 = **61,440** probes. Medium: 3 × 12,288 = 36,864.
  - The plan's 589,824 equals 36,864 × 16, so it is relights summed over a drain's passes, not the window. This is an
    inference; the counter will settle it.
  - Either way the cost is window × passes, every tick.

### Every caller

- `SdfMeshIrradiance.Stage`: `SdfWorldResidency.cs:570`, once a frame, per residency. Each routed world has its own
  residency.
- `TickLocked` comes from `WorldFramePresenter.cs:1618,2209` (`HistoryFree`), `WorldRoutedScene.cs:88`, and the
  launcher's `PresentPacingControl.TickLocked` (`PresentPacingControl.cs:50`).

### Does physics share it?

No. It is presentation-only, in `Puck.SdfVm`. The determinism that matters here is lockstep reproducibility: "the
same image for the same tick on every run" (`SdfFrame.cs:295-300`).

### Invariant and its enforcement

- **I3. Under lockstep, the light a frame renders is a pure function of the frame**: scene, windows, and a lighting
  that is itself a function of this frame's inputs, never of which earlier frame captured it.
- A tolerance measured against the last capture breaks this. Two runs that start at different ticks inside one cell
  would capture different exact suns.
- The fix that keeps I3: quantize the directional lights' direction and radiance onto a fixed grid **before both
  the key and the capture**. Snapping is history-free, so a drain happens only when the snapped sun crosses a cell.
  Enforce it with one function, used by `RefreshLighting` for the key (`:1076`) and by `Lighting.Capture` (`:1092`).
  The views' direct sun is untouched.
- The cell size is a perceptibility choice. The code's own figure is 1/40 of a degree per 0.1 s (`:199-201`). The
  windowed path could take the same snapped key in place of its wall-clock interval, which would leave one policy
  instead of two.
- **Related, low:** the sky-only tolerance already compares against the previous capture (`SkyMatches`,
  `:1086,1097`), so lockstep's sky bounce depends on history within `SkyTolerance`. This is the same class of
  problem, and the same quantization would remove it.

### Mechanisms to reuse

- The existing counters `sdf.mesh-gi.relit`, `.traced` and `.behind` (`SdfMeshIrradiance.cs:113-138`), read through
  `world.counters` (`WorldRenderProbe.cs:51`, registered at `WorldBootComposition.cs:1357,1727`).
- The `[sdf.mesh-gi] done … relit=` log line (`:1463`).
- `frame.Clock` (`PresentedTick`, `SdfFrame.cs:322`), the deterministic clock, if the lane grids on time rather
  than on angle.

### What proves it

Under `world.lockstep` with the day cycle running, over 120 ticks: `sdf.mesh-gi.relit` per tick and drains per 100
ticks, before and after. Then reproducibility: two lockstep runs from different start ticks capture an identical
still at the same tick. A **GPU leg**.

### Draft brief

> **lockstep-relight.** Under `world.lockstep` a moving sun bumps the irradiance lighting version every tick
> (`SdfMeshIrradiance.cs:1060-1106`; no `LightingInterval` limit under lockstep, `:1084`), and each bump drains the
> whole window from no bounce, up to 32 passes (`:538-563`, `:1425-1455`).
>
> - Quantize the directional lights (direction, colour × intensity) onto a fixed grid in one function that both the
>   lighting key and `Lighting.Capture` read, so the drained light stays a pure function of the frame (I3) and
>   drains only when the snapped sun changes cell.
> - Decide whether the windowed path takes the same snapped key in place of its wall-clock `LightingInterval`. One
>   policy is preferred.
> - Fold the sky-only `SkyMatches` history dependence into the same quantization if it falls out.
> - Keep the drain's from-no-bounce invariant (`:1421-1424`).
>
> Proof: `world.counters sdf.mesh-gi` relit per tick and drains per 100 ticks with the day cycle running under
> lockstep, before and after; and two lockstep stills at one tick from runs started at different ticks, compared.
> GPU leg granted. Windowed Vulkan boot (rendering change).
>
> Stop when the sweep's windowed leg can run with the day cycle, at which point the sweep baseline in Next item 1
> becomes runnable.

---

## P4. `SdfMeshDresser.Dress` and the motion staging

The source inspection below records the earlier implementation and its line
positions. Retained topology, shared copy indexing, cached identities and one
history observation door are implemented; dressing now has its own owner. The
remaining step is actual dirty ranges through dressing and staging, tracked in
the [landscape plan](../dense-landscapes.md).

### What is actually there

- **Per-group scans. Confirmed** (`src/Puck.SdfVm/SdfMeshPathBake.cs:402-423`).
  - For every dynamic group, `frame.MeshAssetSlots.Contains(group.Slot)` (`:404`). `MeshAssetSlots` is an
    `IReadOnlyList<int>` (`SdfFrame.cs:204`), so this is a linear `Enumerable.Contains`.
  - Each asset group then scans every `frame.MeshInstances` with `copy.UsesSlot` (`:405-409`), which runs another
    `OtherAssetSlots.Contains` (`SdfMeshInstance.cs:23`).
  - That is O(groups × copies), and it is the trace's `Enumerable.Contains` at 0.5 %.
- **When the list rebuilds.** Any moved dynamic group (`:379-393`), changed copy, `MeshDrawsRevision` or slot clip
  (`:370-377`) rebuilds the **whole** list: `m_draws.Clear()` and re-add everything, then `SdfMeshClaims.Update`
  over all draws (`:398-438`). Any animated body moves a group every frame, so the meadow (4,215 retained draws)
  rebuilds every frame.
- **Boxed tuple keys. Confirmed, but the boxing is in Dress, not in Motion.cs.**
  - `MotionIdentity = ((copy.MotionIdentity ?? copy.Identity), group.Selector)` (`:408`) and `(…, "mesh")` (`:425`)
    box a new `ValueTuple<object,string>` per copy draw per rebuild into `object? MotionIdentity`
    (`SdfMesh.cs:255`).
  - Each boxed key is then compared with a virtual `Equals` in three places: `SdfWorldTables.Motion.cs:214`,
    `SdfWorldTables.MeshRegion.cs:288` and `:320`.
  - The precedent for a stable key exists already: `WorldRigidAssets.cs:255-256` reuses the previous boxed key while
    it is equal.
- **`ToArray()` per rebuild. Confirmed, at `SdfMeshPathBake.cs:434`** (`m_drawnCopies = frame.MeshInstances.ToArray()`),
  not in Motion.cs. `SdfWorldTables.Motion.cs:200-233` (`StageMeshMotion`) compares identities and matrices per draw
  and refreshes `m_meshIdentities`. It only grows its array.
- **Downstream, also O(all draws) every frame.**
  - `StageMeshRegion` sees the same `List` instance with a new revision (`SdfMeshPathBake.cs:458`) and runs
    `m_meshArena.Plan(draws)` (`SdfWorldTables.MeshRegion.cs:155`). Plan has an O(N) fast path,
    `NamesLatestMeshes` (`SdfMeshRegionArena.cs:90`).
  - It also runs a second `SdfMeshClaims.Update` over the same list (`MeshRegion.cs:144`), a `draws.Any(lambda)`
    (`:201`), and `StageChangedDraws`, which runs `SameDraw` on every draw (`:269-310`).
- **Duplication.** Two of these paths decide the same thing:
  - `StageMeshMotion` (`Motion.cs:210-225`) and `StageChangedDraws` (`MeshRegion.cs:288-293`) both decide
    seed-or-copy per draw (identity changed means seed; matrix changed means copy), in two places.
  - Two `SdfMeshClaims` trackers update over the same list each revision (`SdfMeshPathBake.cs:433`,
    `MeshRegion.cs:144`). Whether both owners are needed was not confirmed.

### Every caller

- `SdfMeshDresser.Dress`: called by `SdfWorldResidency.Capture`, once a frame, per residency.
- `StageMeshRegion`: `SdfWorldTables.FramePreparation.cs:311`.
- `StageChangedDraws` and `StageMeshMotion`: `MeshRegion.cs:219,239`.
- Producers of `MeshAssetSlots` and `MeshInstances`: `WorldSceneEmitter.cs:453-454` and
  `WorldSessionSceneEmitter.cs:921-922` (from `WorldRigidAssets`), through `SdfCompositionFrameSource.cs:501,644`.

### Does physics share it?

No. It is presentation-only (`Puck.SdfVm`, draw lists and GPU tables).

### Invariant and its enforcement

- **I4a. A draw's index and identity stand while the dressed list's membership stands.** A frame that only moves
  poses changes values at stable indices and reports which ones. Anything that adds, drops, reorders or swaps a mesh
  is a structural change and takes the full path.
  - Enforce it with one type produced only by `Dress`, carrying a structural revision plus the changed-index runs
    (the vocabulary of `GpuUploadRuns` and `SdfMovedTransforms`). `StageMeshRegion` consumes it, so the tables stage
    only those runs and skip `Plan` and claims on a value-only revision.
- **I4b. Two draws' motion keys are equal exactly when they are the same continuing copy under the same selector.**
  Enforce it with one key type (for example a sealed record with value equality), created in one place and interned
  per copy, so the common comparison is reference-equal and nothing allocates per frame. The seed-or-copy decision
  becomes one helper that both staging paths call.

### Mechanisms to reuse

- `SdfMovedTransforms`, the frame's moved set (`WorldCreationInstances.Pack.cs:136,304-313`).
- `GpuUploadRuns` (`SdfWorldTables.Motion.cs:21-26`) and the tables' existing "same-sized list owes only its changed
  draws" path (`MeshRegion.cs:209-224`).
- `WorldRigidAssets`' stable-key reuse (`:255`).
- `AllocationWindow` (`src/Puck.Abstractions/Counting/AllocationWindow.cs`) to count bytes.

### What proves it

At the meadow under lockstep, 600 ticks, before and after:

- draws rebuilt a frame (today all of them; after, 0 on a value-only frame);
- draws compared in staging (today all; after, the changed runs);
- `Plan` and claims calls a frame;
- bytes allocated a frame on the frame thread in `Dress` and staging.

A **GPU leg**. A motion-vector check (the `motion` debug view, one still) shows reprojection unchanged.

### Draft brief

> **dress-proportional.** Make the dressing and the mesh table's staging proportional to what changed.
>
> - Patch moved groups' draws in place at stable indices, and hand `StageMeshRegion` a structural revision plus
>   changed-index runs, so a value-only frame skips `Plan`, the claims updates and the full `SameDraw` scan
>   (`SdfWorldTables.MeshRegion.cs:138-310`).
> - Replace the per-rebuild boxed `(object, string)` motion keys (`:408,425`) with one interned key type, and merge
>   the two seed-or-copy decisions (`SdfWorldTables.Motion.cs:210-225`, `MeshRegion.cs:288-293`) into one helper.
> - Drop the `ToArray` at `:434`.
> - Decide whether both `SdfMeshClaims` trackers (`SdfMeshPathBake.cs:433`, `MeshRegion.cs:144`) are needed; keep
>   one if not.
>
> Invariants I4a and I4b in the commit body, each with its one enforcing type.
>
> Proof: draws rebuilt, draws compared, `Plan` and claims calls, and frame-thread bytes a frame at the meadow under
> lockstep, before and after; a `motion` debug-view still unchanged. GPU leg granted. Windowed Vulkan boot
> (rendering and table code). Build `Puck.SdfVm`.

---

## One lane or three for P1, P2 and P4

**Two lanes: P1 and P2 together, and P4 alone.** P3 is a third, independent lane.

- **P1 and P2 share a mechanism, so one lane.**
  - Both caches are the same copied pattern (thread-static ring, cursor, bit compare).
  - Both are fed by `SdfFieldEvaluator` on the presenter's static field.
  - Both want one frame field-query counter source and the `PointWork` registration (`WorldBootComposition.cs:173`).
  - Two lanes would build the owner-keyed answer cache twice, or conflict over its home.
  - Files: `WorldEffectorSolver.cs`, `WorldCreationInstances.Effectors.cs` (and possibly `.Pack.cs`/`.Rates.cs`),
    `WorldCameraClearance.cs`, `WorldSeatViewState.cs`, `WorldBootComposition.cs`, and possibly
    `SdfFieldEvaluator.cs` (only for a bit-identical shared-reach gradient).
- **P4 has no file in common with P1/P2.**
  - Files: `SdfMeshPathBake.cs`, `SdfWorldTables.Motion.cs`, `SdfWorldTables.MeshRegion.cs`, `SdfMeshInstance.cs`,
    `WorldRigidAssets.cs`.
  - It runs in parallel. Its GPU legs serialize on the lease, but no file conflicts.
- **P3 is one file** (`SdfMeshIrradiance.cs`) and shares nothing with the others.

All three measure at the meadow under lockstep. One windowed World per lane under `puck lease gpu`, stopped once its
counters are read.

## Findings not in the plan

1. **[high] The rate tiers give way to the slow frame.** `PresentationRate.Holds` (`PresentationRate.cs:60-72`)
   holds a 15 Hz body only while `elapsed + frame <= 66.7 ms`, so at the meadow's 67-74 ms frames no body holds. The
   rating saves nothing exactly where the frame is slowest. This is by design (a floor in presentation time), but
   the plan should know it: the frame-time fixes and the rating compound. Recorded, not a defect to fix in P1.
2. **[medium] Probes whose answer is discarded:** the gate is not yet tested, or the plant is already latched
   (`WorldCreationInstances.Effectors.cs:82-98`, `:419-424`). In P1's brief.
3. **[medium] Duplication: two identical answer rings** (`WorldEffectorSolver.cs:261-280`,
   `WorldCameraClearance.cs:139-166`). One mechanism; in the P1+P2 brief.
4. **[medium] Duplication: two seed-or-copy decisions** (`SdfWorldTables.Motion.cs:210-225`,
   `SdfWorldTables.MeshRegion.cs:288-293`), and possibly two claims trackers over one list. In P4's brief.
5. **[low] `SdfFieldEvaluator.PointWork` is counted on every walk but never registered** for `world.counters`
   (`SdfFieldEvaluator.Work.cs:14`; `WorldBootComposition.cs:164-173`). Only `Puck.Cli.Bench` reads it
   (`WorldBenchmarks.cs:527-528`).
6. **[low] Lockstep sky bounce depends on history within `SkyTolerance`** (`SdfMeshIrradiance.cs:1086,1097`). In P3's
   brief.
7. **[open, product] Session bodies do not plant.** A session's effectors find no field
   (`WorldSessionStampSource.cs:31-33`, "holds its authored pose"), though the session emitter builds a pickable
   field for its scene (`WorldSessionSceneEmitter.cs:840-874`). Wiring it would add probes, so this is a look call
   for the owner, not a frame fix.
8. **[note] The plan's 589,824 is not the window size** (61,440 at High, 36,864 at Medium,
   `SdfMeshIrradiance.cs:2441-2456`). It matches 36,864 × 16 passes. Correct the plan's figure when P3 measures it.

## Corrections to the plan's text

- `open-items.md:48-51` and `engine-job-system.md` (Next item 1): "relights all 589,824 probes every tick" should
  read "relights the whole window (61,440 probes at High) from no bounce, up to 32 passes, every tick", until the
  counter gives the real figure.
- `engine-job-system.md:170-174` and `open-items.md:768-770`: "posed and probed every frame" is right, but the cause
  is that the 15 Hz floor cannot hold at 67-74 ms frames (`PresentationRate.cs:71`), not a missing distance rating.
  "A point the march has just evaluated" should read "six taps around a point the march just accepted; none is the
  sample the march took".
- `engine-job-system.md:158-159` and P4's premise: the `ToArray()` is at `SdfMeshPathBake.cs:434`, and the boxed keys
  are made at `SdfMeshPathBake.cs:408,425`. `SdfWorldTables.Motion.cs:211-230` is where they are compared.
