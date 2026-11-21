# The first bet: what a view reads, and what records its change

This memo is the first step of [the first bet](../rendering-research.md#the-first-bet-stands-on-the-clients-records)
in [the rendering plan](../rendering-research.md). The bet is that a view
whose inputs have not changed can keep its picture without being captured
again, and that the client already holds the records needed to decide
that. The memo reads the code that prepares a view, lists every input it
reads on the client, and says for each one what records its change today,
what would have to, and what the shortcut needs from it.

It was written from the source alone. Nothing was built or run for it, so
every statement about behaviour is a reading of the code, and the few that
are deductions say so.

## Summary

**Preparing a view is two pieces of work, and the bet should skip only the
first.** The first is the capture: asking the world for this frame's
scene and writing it into the GPU tables. The second is upkeep the
residency does on every frame whatever was captured: fitting the baked
meshes to the frame, binding screens, advancing indirect light and
exposure, and then hashing the tables to decide whether the picture
stands. The shortcut the memo recommends skips the capture and leaves the
second piece exactly as it is. That is narrower than "the view keeps its
picture", and it reaches further: a world with a running screen can never
keep its picture, but its capture can still be skipped while its own
scene is still.

**The two records the plan names are real, and narrower than the bet
assumes.**

- *The state mirror's revision* moves when a delivered value changes. It
  does not move when a value that is easing between two ticks is presented
  at a new point between them, when the presented tick moves, or when a
  clock is scrubbed from the console. The environment's resolver already
  works around the first of these with a record of its own.
- *The scene's revision vector* covers which geometry exists and nothing
  else: not poses, cameras, lights or settings. Reading it is also not
  free. Three of the five emitters poll or scan to fill their components.

**Most other inputs have a record that is written too late.** The moved
set of the transform table, each owner's rest state, the tables' revisions
and the draw list's revision are all written while a frame is captured or
packed. They tell a consumer afterwards what moved. They cannot decide
whether to capture.

**Ten inputs of the capture have no record that can be read before it.**
[They are listed below](#inputs-with-no-early-record). Seven are small
additions to a type that already knows the answer. One has an answer
that nothing consults. One, a view's camera, needs no record because
comparing the few values is the record. The last is elapsed time, which no
record can cover and a rule closes.

**The boot world's capture is not a candidate.** It resolves the cameras
every other view is fitted to, and it publishes the frame's sound, marker
chips and cursor viewports. It stays, as decision 2 already says. The
candidates are the three other kinds of residency: a world a seat is
presented in or seen through a window, a session screen, and an infinity
view. All three read through one emitter, so one set of records serves
them.

**The mechanism already exists.** A source that films nothing already
leaves the held frame in place. A converging capture already presents a
held frame and has the mesh dresser fit it again, which the dresser is
built to do. The shortcut is one more cause for that second path, decided
by the frame source from its own records.

**The first step is a count.** Nothing today counts how many captures
produce a frame identical to the one before. That count, on the
benchmark's route, is the cost the bet could save and decides whether the
prototype is worth building.

**One defect in today's code surfaced, by reading.** In a narrow case a
stamped creation that reads an easing state value holds its pose between
two ticks. It is described under [the first gap](#inputs-with-no-early-record),
is not reproduced, and is the same hole the shortcut has to close.

## Words this page uses

- A **residency** is one world's scene made ready to draw on the GPU
  (`SdfWorldResidency`). Several views can draw from one.
- To **capture** is to ask the residency's frame source for this frame's
  scene: its program, poses, views, lights and sky
  (`ISdfFrameSource.CaptureFrame`, which answers an `SdfFrame`).
- To **pack** is to write a captured frame into the GPU tables
  (`SdfWorldTables.Pack`).
- A view **stands** when its latest picture is shown again and none of its
  passes run.
- An **early record** can be read before a capture and says whether an
  input has moved since the last one. A **late record** is written during
  the capture or the pack, so it exists only once the work it could have
  saved is done.

## What preparing a view does today

Each frame the package prepares the host residency and each residency that
owes its minimum (`SdfWorldPasses.BeginFrame`), and every other residency
only for an instance the frame reads (`SdfWorldPasses.PrepareRead`). The
question of whether a view stands is asked afterwards, of read instances
only, and begins with the same preparation,
which runs once a frame (`SdfWorldResidency.IsUnchanged`,
`SdfWorldResidency.Prepare`). The preparation has three parts.

**The capture** (`SdfWorldResidency.HostFrame`).

1. A film gate may decline, and the held frame then stands in.
2. A converging capture presents the frame it froze and calls
   `ISdfFrameSource.PresentHeldFrame`.
3. Otherwise the frame source captures. A source may answer no frame, and
   the held frame then stands in.

**The dresser's pass**, which today runs on a frame the source answered or
a capture froze, and not when the source films nothing. The mesh demand
reads the views' eyes (`SdfMeshDemand.Observe`).
The mesh dresser then opens the bake's one door from presentation
(`SdfMeshDresser.Dress`): the bake notices a lost mesh, plans again the
groups a moved input reaches, finishes replacements, gives detail back
where the tables refused a mesh, decides tiers, and hands over the baked
meshes the frame draws. The dresser is built to dress the same frame
again, and answers that same frame when it does.

**The upkeep**, which runs whichever frame is held.

1. The device and tables are found or built. A requested memory trim is
   served, a pending shader reload is installed, and the glyph atlas is
   compared.
2. Every screen's source, mapping, treatment and light is bound
   (`ISdfScreenSources`), and the screens' moving surfaces and text decals
   are polled.
3. A frame that is a new object is packed. One that is the same object as
   last time is not, and only the meshes that landed since are staged
   (`SdfWorldTables.StageLandedMeshes`).
4. The indirect light and the views' exposures advance
   (`SdfMeshIrradiance.Stage`, `SdfViewExposures.Stage`), and the tables'
   signature is computed (`SdfWorldTables.UpdateTablesSignature`).

**The answer.** A view stands when no refusal holds and its signature
equals the one it last rendered at (`SdfWorldTables.ViewSignature`). The
refusals are meshes pending, lighting settling, a source waiting, and a
frame that forces a render (`SdfWorldTables.ForcesRender`). The package
adds its own: a pending pick, a changed binding, render scale or
sharpness, and the image history (`SdfWorldPasses.IsUnchanged`).

## Skip the capture, and leave the answer alone

The bet is worded as a view keeping its picture. Read against the code,
that is two decisions.

- **Whether to capture.** Today the answer is yes, unless a film gate
  declines or a capture holds the frame.
- **Whether the picture stands.** Today this is the hash compare and its
  refusals.

The shortcut should change only the first. When the frame source can say
that a capture would produce the frame it produced last, the residency
holds the frame it has. The dresser's pass and the upkeep then run on the
held frame, and the second decision is made as it is today, on tables that
hold the same frame.

Four things follow from drawing the line there.

- **The dresser's pass is upkeep and keeps running.** Much of it is paced
  by frames and by what arrives from outside, not by the scene: a tier
  decision counts frames, and a lost mesh, a finished replacement or a
  refused mesh comes from the bake's workers and the memory account. A
  source that films nothing stalls all of it today. Running it on the held
  frame
  keeps the bake moving, at the cost of one change in the residency: the
  mesh region is staged again when the dressed rows' revision moved, where
  today that happens only for a new frame object.
- **Everything the upkeep reads stays covered by the existing hash.**
  Screens, text decals, the glyph atlas, a shader reload, the debug view,
  exposure and indirect light are read after the capture and folded into
  the signature or guarded by a refusal. The shortcut needs no record for
  any of them.
- **The plan's first rule holds without effort.** The refusals belong to
  the second decision, which is untouched.
- **More frames qualify.** A world that shows a running cartridge on a
  screen never lets a view stand, because the screen's picture is
  rewritten in place. Its capture can still be skipped on every frame its
  own scene is still, and the capture is the processor's share of the
  work.

The rest of this memo is about the first decision: what a capture reads,
and what can say beforehand that it would read the same.

## The four kinds of residency

The same input has a different record depending on which kind of
residency reads it, so the table is needed before the list.

| Kind | Frame source | State arrives through | Poses are presented at | Cameras come from |
|---|---|---|---|---|
| The boot world | `WorldFramePresenter`, over a composition of four emitters: `WorldSceneEmitter`, `WorldSdfDocumentEmitter`, `WorldAdjacencySceneEmitter` and `WorldFieldEmitter` | `WorldClient.StateMirror`, refreshed at each delivery | The host's interpolation fraction (`FrameContext.InterpolationAlpha`) | Resolved inside its own capture, from each seat's body, look, rig and eases |
| A routed scene: a world a seat is presented in, or seen through a window | `WorldRoutedScene.FrameSource`, a composition of one `WorldSessionSceneEmitter` | `WorldSessionMirror.FollowState`, which brings a mirror of its own up to the latest delivery when asked | A fraction measured from the wall clock since the snapshot arrived (`WorldSessionMirror.InterpolationAlpha`) | Handed in: the seats' views the boot presenter latched (`WorldRoutedScene.AddView`), each window's view (`WorldRoutedWindow.View`), then filmed cameras |
| A session screen | `WorldSessionFrameSource` over the same kind of composition | The same | The same | A named camera of the destination (`WorldSessionSceneEmitter.ResolveCamera`), or a window's fit to the viewer's eye (`WorldSessionSceneEmitter.SetWindowFit`) |
| An infinity view: a sky layer that shows a world or far geometry | `WorldSessionFrameSource` over a composition dressed by `WorldInfinityViewScene` | The same | The same | Fitted to each camera that looks at the layer |

Four things follow.

- **The boot world is the root.** Its capture resolves the cameras the
  other three are fitted to, and a routed scene's film gate takes the boot
  frame before its own. The boot capture also publishes what is not a
  picture at all: the sound snapshot, the marker chips, the seats'
  viewports for the cursor, the frame-rate sample, and the placement of
  views in the render graph. None of these may be skipped, and its cameras
  have no record of rest. The boot residency is outside the prototype.
- **The other three share one emitter.** `WorldSessionSceneEmitter` reads
  for all of them.
- **Their bound values move on deliveries, not on frames,** unless a seat
  is routed there. The boot presenter presents a routed world's mirror at
  a fraction only while it resolves a seat's camera in that world. For any
  other routed world nothing applies a fraction, so an easing value and
  the presented tick move only when a delivery arrives.
- **A session's time is the wall clock.** It measures its own interval
  between captures (`WorldSessionFrameSource.CaptureFrame`) and its poses'
  fraction. No delivered record covers either, so both are handled by a
  rule: the capture is skipped only while nothing would read elapsed time.

## The records that exist

Each record below says what it proves and for how long.

**The revision vector** (`ISdfSceneEmitter.WriteRevision`, compared in
`SdfCompositionFrameSource.CaptureFrame`). It proves that the next emission
would build the same program, until any component moves. Components are
kept apart and compared one by one, so no two can cancel. It is an early
record, with two limits. It says nothing about poses, views or lighting.
And filling it does work: the boot scene scans its screens for carried ones
(`WorldSceneEmitter.WriteRevision`), the session scene follows its state
mirror and, where its host paints the bodies, compares every body's colour
(`WorldSessionSceneEmitter.WriteRevision`), and the adjacency emitter polls
every neighbour (`WorldAdjacencySceneEmitter.WriteRevision`). The
components are counters, so reading the vector twice in a frame gives the
same answer.

**The state mirror's counters** (`WorldStateMirror`). `Revision` moves
when a refresh changes any slot's current sample, and `ColorRevision` when
a colour does. `Changed` answers the same for one slot. `Generation` moves
when the set of registered bindings changes, `Installs` at each installed
document, and `Lifetime` when the world is replaced. Together they prove
that every delivered value is what it was. They do not prove that what is
presented is what it was, for the reasons the summary gives. A holder's
own view of them is `WorldStateLease.Changed`.

**The environment's record of what it read** (`WorldEnvironmentResolve`).
The resolver notes each slot and each clock it reads while resolving lights
and sky. It resolves again only when the definition, one of those slots,
one of those clocks or the presented tick it integrated a rate over has
moved, and `WorldEnvironmentResolve.Resolutions` counts the times it did.
This is the one consumer that already records its reads, and it is the
shape the shortcut wants everywhere. It also compares each bound slot's
presented value, because the mirror's revision does not cover an eased
value (`WorldEnvironmentResolve.BoundSlotMoved`).

**The session levers' revision** (`WorldRenderSettings.Revision`). Every
lever's setter moves it, with one exception listed among the gaps. It is
the sum of two counters that only rise.

**The definition's revisions.** `WorldClient.DefinitionRevision` moves at
a definition delivery, and the client's response revision when a state
delivery changes which creations placements show. A routed world has
`WorldSessionMirror.DefinitionRevision` and
`WorldSessionMirror.SnapshotRevision`. The second is assigned from the
wire and can fall, which is why the vector never sums.

**Late records.** These are precise and cannot decide a capture, because
they are written by the work it would skip.

- The moved set of the transform table (`SdfMovedTransforms`), written as
  emitters pack.
- Each owner's rest state (`WorldTransformOwners`), settled as it is
  packed.
- The draw list's revision (`ISdfSceneEmitter.MeshDrawsRevision`). The
  stamp pool moves it during its pack.
- The sampled fields' set, which is the same object while every field
  stands (`SdfSampledSet.Capture`).
- The tables' revisions and region hashes, and the GPU row journal
  (`GpuChangedRows`).

**Records of cuts, not of motion.** A body's pose epoch
(`WorldPoseTable.PoseEpoch`) moves when it appears or teleports. A view's
cut revision (`SdfViewSnapshot.CutRevision`) moves when its framing is
reseeded. Neither moves when the body walks or the camera pans.

## The inputs of the capture, one by one

The tables give each input with where it is read, what records its change
today, and what the shortcut needs. Where a row says "boot only", the
input is read only by the boot world's capture, which the shortcut does
not skip.

### The call and its gates

| Input | What records its change today | What the shortcut needs |
|---|---|---|
| The frame's wall interval (`FrameContext.FrameDeltaSeconds`), handed to the capture. A session measures its own instead | Nothing. It is positive on every presented frame | No record can cover it. The rule is that the capture is skipped only while every reader of elapsed time is at rest. Those readers are rows below: replay cursors, followers and drivers |
| The interpolation fraction, which poses (`WorldClient.UpdateRenderPoses`) and bound values (`WorldStateMirror.Apply`) are presented at | `WorldStateMirror.PresentationFraction` holds the last one. Where it is applied it moves on almost every frame | The fraction matters only while something is between two samples. Two answers are missing: whether any pose is, and whether any slot is |
| The capture extent (`SdfWorldResidency.RequestExtent`) | The two fields | Compare them |
| The film gate, a callback given to the residency. A routed scene's takes the boot frame and fits its windows | None; it is code | Keep calling it. What it fits is a camera input, below |
| A capture that holds the frame (`SdfWorldResidency.HoldsFrame`) | The request | Already its own path. The shortcut does not run under it |
| A tick-locked frame (`SdfFrame.TickLocked`) | The flag | The shortcut does not run under it either. Lockstep exists so that every tick's frame is formed in full |

### What geometry exists

| Input | What records its change today | What the shortcut needs |
|---|---|---|
| Each emitter's content (`ISdfSceneEmitter.Emit`) | The revision vector. Early | The vector, read once a frame through the composition, with the shortcut holding its own copy of the values it last skipped on |
| Each emitter's reconciliation (`ISdfSceneEmitter.PrepareFrame`): the stamp census, rigid assets and the avatar catalog | Each skips itself on the revisions it reads (`WorldSceneEmitter.PrepareFrame`, `WorldSessionSceneEmitter.PrepareFrame`). Early | It runs before the vector is read and is cheap when nothing moved. The shortcut calls it and reads the vector after |
| The slot layout (`ISdfSceneEmitter.DynamicSlotCount`) | None of its own. It can change only in a reconciliation | Covered by the row above |
| Engine code edited while running (`CodeRevision.Current`) | The vector's last component, which is the composition's own. Early | In the key |
| A program build finishing in the background (`SdfCompositionFrameSource.BuildsInBackground`) | `SdfCompositionFrameSource.Building`. No component moves on the frame the finished program is adopted, and `SdfCompositionFrameSource.Waiting` names the build only for a tick-locked or converging frame | Capture while a build is in flight. Without this the adoption frame is missed |
| Mesh draws (`ISdfSceneEmitter.MeshDraws`) and rigid copies (`ISdfSceneEmitter.MeshInstances`) | The list's identity and revision; a copy's transform is compared as it is written. Late for anything a pack poses | Covered by the vector and by motion at rest, below |
| Sampled fields: a height field's heights (`WorldFieldEmitter.PrepareFrame`) | The field's row slot in the mirror (`WorldStateMirror.Changed`). Early. Boot only today | The mirror's counters |
| The pick map and the slot clips at a portal | By reference. Late | Nothing of their own. They follow geometry and poses |

### Poses and motion

| Input | What records its change today | What the shortcut needs |
|---|---|---|
| Delivered poses (`WorldPoseTable`, filled by `WorldClient.DeliverSnapshot` or by the session mirror) | The delivered tick, and the pose epoch for a cut. Nothing says whether any body's two poses differ | A count on the pose table of the bodies whose previous and current poses differ. At zero the fraction can move no pose. The table writes both poses at each admission, so it can keep the count there |
| The correction easers that bleed a snap back out (`WorldClient.UpdateRenderPoses`) | None | Boot only |
| Each owner's rest: an avatar's leaves, a stamp's root and shapes (`WorldTransformOwners.Wake`) | The owner's stored pose, version and restless flag. Late: they are compared as the owner is visited in the pack | A summary the emitter publishes when a pack ends: every owner is at rest. It proves the next pack moves no slot until a pose, a fact, an epoch or a bound value moves. Each of those has its own row |
| A stamped creation's replay cursor, drivers, effectors and followers (`WorldCreationInstances.PackTransforms`, advanced by `WorldCreationInstances.Tick`) | Per registration, and private: the cursor and the lease's `WorldStateLease.Changed` folded into one integer, and whether a driver still runs. Late | The pool's own answer that it is at rest: no cursor running, no driver continuing, no rated pose owed, no owner restless |
| The rate each body's pose updates at (`WorldCreationInstances.RateBy`), from the last frame's views and shadow culls | None | Nothing of its own. It is a function of cameras and lights, and a rated pose still owed is part of the pool's rest |
| The moved set (`SdfMovedTransforms`) | Itself. Late | It is the tables' input. The audit can read it to say why a frame moved |

### State values and time

| Input | What records its change today | What the shortcut needs |
|---|---|---|
| Bound values as delivered (`WorldStateMirror.Refresh`, `WorldStateMirror.Advance`, `WorldStateMirror.Install`, `WorldStateMirror.RefreshRows`) | The mirror's counters. Early | All of them, as separate components |
| A routed world's deliveries, which wait in its session mirror until a presentation follows them (`WorldSessionMirror.FollowState`) | The moved rows are held behind the mirror until followed. `WorldSessionMirror.DefinitionRevision`, `WorldSessionMirror.SnapshotRevision` and `WorldSessionMirror.EngineTick` are readable at once | The check follows the state itself, then reads the counters. Following reads only what moved |
| A value easing between two ticks, presented at a fraction (`WorldStateMirror.Apply`) | None. The presented value moves and no counter does | Whether any slot is interpolating, which the mirror already knows as a count. While one is, the fraction is an input |
| The presented tick (`WorldStateMirror.Presented`, carried as `SdfFrame.Clock`) | The value. It moves at least once for each delivery while the world ticks | Knowing whether anything reduces it. Four things do: a rate the environment integrates, a bounded medium's drift and pulse (`SdfVolumeMotion.At`), a flow layer's clock, and a sky layer's clock. The environment records the first. The other three are visible only as packed bytes |
| Keyed values and state clocks (`WorldStateMirror.TryPhase`) | Each consumer's `WorldClockReads`. Early | Reuse it |
| A clock scrubbed, held or re-rated from the console (`WorldStateMirror.ControlClock`) | None. A consumer notices only by reading the phase again | A counter the control moves |
| Lights, sky, atmosphere, exposure settings, render lanes and indirect controls (`WorldEnvironmentResolve.Resolve`) | The resolver's own test of what it read. Early, and cheap | The test as an answer on its own: would it resolve again. Three things are applied after it on every frame and are not covered: a light anchored to a body, which is a pose; the shadow slots; and the sun disc's light, which follows the key light |
| A shadow slot's hand-off from one light to another (`WorldShadowSelection.CopyPresented`) | None. The selection advances at each delivery on its own, and the fade's weight is computed from the presented tick | Whether a hand-off is in progress. While one is, the presented tick is an input |

### Views and cameras

| Input | What records its change today | What the shortcut needs |
|---|---|---|
| A seat's camera in the boot world: its body's pose, its look, an authored rig, and four eases stepped by elapsed time | A cut revision only | Boot only. No record of rest exists for the look or the eases |
| A routed scene's seat views (`WorldRoutedScene.AddView`) | The value handed in | Compare each view with the one in the held frame. `SdfViewSnapshot` is a value, so this is a field compare |
| A window's view (`WorldRoutedWindow.View`, `WorldSessionSceneEmitter.SetWindowFit`) | None; the holder sets it, or a callback fits it to the viewer's eye | Evaluate the fit and compare. This is the commonest reason a routed view moves: a window's frustum follows the viewer's eye, so a walking viewer moves every window in sight |
| A session's own camera (`WorldSessionSceneEmitter.ResolveCamera`): the destination's camera row, its anchor's pose, its rig, and the destination's tick | None | Resolve it and compare. It is one rig evaluation, and it is the only way to see a rig that reads the tick |
| Cameras filmed for the world's own screens, and the sky layers fitted to each view (`SdfViewSnapshot.SkyViews`) | None; callbacks add them | Part of the same compare. The views are resolved on every frame, and only the rest of the capture is skipped |
| A nested view's quality, which follows the share of the display the view reached on the last measured frame (`WorldRenderSettings.NestedViewQuality`) | None for the share. The levers behind it move the settings' revision | Part of the view compare: the quality is a field of the view |
| Layout, transitions and the render scale | None for the layout. The package compares scale and sharpness itself | Boot only, apart from the scale, which the package already checks |

### Levers and document values

| Input | What records its change today | What the shortcut needs |
|---|---|---|
| Session levers: cull, detail, update rates, the cadence gate, toon, shading, exposure pin, bloom, sky quality (`WorldRenderSettings`) | `WorldRenderSettings.Revision`. Early. `WorldRenderSettings.RateTints` is set without moving it. A view's resolution pin and controller (`WorldRenderViewResolution`) are apart from it, and reach the frame as the view's scale | The revision as two components, and the one setter moving it. The view compare covers the scale |
| The levers a routed scene copies from the boot frame (`WorldRoutedScene.Dress`) | The boot frame is a new object every frame | Compare the copied values. The boot frame's elapsed time is copied too, and it is covered by the rule on elapsed time |
| Render values read from the document on every frame: far distance, shadow coverage, toon, shade programs, indirect tier | `WorldClient.DefinitionRevision` or `WorldSessionMirror.DefinitionRevision`. Early | The revision. A state delivery replaces the definition object without moving it and keeps the render section's object, so the section's identity is the finer record |

### What the dresser's pass reads

The pass keeps running on a held frame, so none of these needs anything
from the frame source.

| Input | What records its change today | What the shortcut needs |
|---|---|---|
| Mesh demand (`SdfMeshDemand.Observe`) | None | Nothing. It is a function of the views, which are compared |
| The bake's products, a mesh the shared library lost (`SdfMeshLibrary.LostRevision`), a mesh the tables refused, and the tier each group is drawn at | The dressed rows' own revision (`SdfFrame.DrawRowsRevision`), moved by the dresser | The residency stages the mesh region again when that revision moves |
| A group whose geometry reads a moving operand or a sampled field, planned again from the frame's transforms and fields | The frame's own transforms and sampled set | Nothing. A held frame carries the same ones |

### What the upkeep reads

These are not inputs of the capture. They are listed so that the line is
plain: each runs on a held frame as on a new one, and each is already
covered by the signature or by a refusal.

| Input | How it is covered today |
|---|---|
| What each screen shows, its face, mapping, treatment, luminance and ripple (`ISdfScreenSources`); a screen carried by a body; text decals (`WorldScreenDecals`) | Bound on every frame. The mapping and surface tables are hashed, the decal buffer carries a revision, a pass that samples a screen's image folds the image's publication into its signature (`SdfWorldPasses.ReadSignatureOf`), and a screen that shows a source forces the whole view to render (`SdfWorldTables.ForcesRender`) |
| The glyph atlas (`ISdfFrameSource.GlyphAtlas`) | Compared by identity on every frame |
| A shader reload (`SdfWorldResidency.RequestShaderReload`), the debug view (`SdfWorldResidency.DebugMode`) and the hit-door diagnostic (`SdfWorldTables.ForceHitBound`) | The program revision moves at a reload, and the pass block carries the other two |
| Indirect light (`SdfMeshIrradiance.Stage`) | `SdfMeshIrradiance.Settling` refuses a stand until the probes have reached the tables |
| Each view's exposure (`SdfViewExposures.Stage`), eased over the host's interval | The multiplier is in the pass block. A view whose exposure is still adapting renders again, whether or not it was captured |
| Meshes that land after the pack, and a memory trim | Staged on every frame (`SdfWorldTables.StageLandedMeshes`). The mesh revision is in the signature |
| The device and the shading kernels | A lost device clears every rendered signature, and a frame whose kernel is still building is not ready (`SdfWorldTables.FrameWaiting`) |

## Inputs with no early record

These are the gaps. Each names the type that would hold the record and how
large the addition is.

1. **Values eased between two deliveries.** The mirror keeps the list of
   slots it interpolates and exposes neither the list nor its length. A
   public count on `WorldStateMirror` closes it.

   This gap has a consequence in today's code. A stamped creation repacks
   when its lease's `WorldStateLease.Changed` moves, which happens once at
   the tick. If the first frame after that tick presents at a fraction of
   exactly zero, the value presented is the old one, the repack changes no
   slot, and the owner comes to rest (`WorldTransformOwners.Settle`). The
   frames that follow present new values and move no counter, so the
   creation holds its pose until the next tick. At any other fraction the
   repack moves a slot and the owner stays restless, which is why the case
   is narrow. This is a deduction from the code and was not reproduced.
   The environment's resolver has the correct guard for the same case.
2. **Whether a frame reads the presented tick.** The environment knows for
   its own reads. Bounded media, a flow layer and a sky layer's clock do
   not say. One flag for each, set where the program is built or the
   volumes are appended, closes it.
3. **A scrubbed clock.** A counter on `WorldStateMirror`, moved by
   `WorldStateMirror.ControlClock`.
4. **Whether any pose is between two samples.** A count on
   `WorldPoseTable`.
5. **Motion at rest.** A summary on `WorldCreationInstances` and on the
   session emitter's avatar owners, set when a pack ends.
6. **A shadow hand-off in progress.** An answer on `WorldShadowSelection`.
7. **A view's camera,** for the three kinds that are handed one. There is
   no record, and none is needed: the views are few, and comparing them by
   value after resolving them is the record.
8. **One session lever that skips the revision:** the tier tint,
   `WorldRenderSettings.RateTints`.
9. **A background program build finishing.** The answer exists
   (`SdfCompositionFrameSource.Building`) and nothing consults it for
   freshness.
10. **Elapsed time.** No record can exist. It is closed by the rule that
    every reader of time reports rest, which is what gap 5 supplies.

The boot world's camera inputs (a seat's look, its four eases, the layout's
transition) and its correction easers also have no record. They are not
numbered here because the shortcut does not skip the boot capture.

## How the shortcut should be built

### Use the path that already holds a frame

The residency already holds a frame in place of a capture in two cases.
When a source films nothing, the held frame stands in and neither the
dresser nor the pack runs. When a capture is converging, the residency
presents the frame it froze, calls `ISdfFrameSource.PresentHeldFrame` so
that whatever the source does each frame besides composing still happens,
and dresses the frozen frame again.

The shortcut is the second case with another cause. The frame source
gains one answer: a capture now would produce the frame it produced last.
The residency asks unless a capture or lockstep holds the frame. On a yes
it keeps the frame object it holds, presents
it as a converging capture does, and dresses it again. No second freshness
mechanism is added. The tables see the same frame object and pack nothing,
and they stage the mesh region only when the dressed rows moved.

One detail of that branch is easy to get wrong. A captured frame arms the
program's upload from its own flag (`SdfFrame.ProgramChanged`), and the
converging path clears the flag on the copy it presents. A branch that
keeps the same object must not arm the upload again from a flag that was
already consumed.

The decision belongs in the frame source because that is where the reads
are. A record kept beside the code that reads the input is the one most
likely to be updated when that code changes.

### What the frame source's answer rests on

For the session emitter, which serves all three candidate kinds, the
answer is yes only when all of the following hold.

- The revision vector equals the shortcut's copy, and no build is in
  flight.
- After following the state, the mirror's five counters equal the copy,
  no slot is interpolating, and no clock has been scrubbed.
- The pose table reports no body between two samples, and the pool and
  the avatar owners report rest.
- The environment would not resolve again, no shadow hand-off is in
  progress, and no bounded medium, flow layer or sky layer reads a tick
  that has moved.
- The views, resolved for this frame, equal the held frame's by value.
- The session levers' revision equals the copy, and for a routed scene the
  levers copied from the boot frame equal the held frame's.

The components sit in a second vector beside the revision vector, written
by the emitter and the dresser under the same contract: never summed, and
every component written on every read. The composition compares it and
keeps the copy, as it does for the program
(`SdfCompositionFrameSource.CaptureFrame`).

### The invariant, what enforces it, and where that stops

**The invariant.** A capture is skipped only if the frame it would produce
equals the held frame in everything the dresser, the pack and the pass
blocks read.

**What enforces it.** One door. `SdfWorldResidency` is the only caller of
a frame source's capture, so the branch that skips it sits in one place.
The second decision, whether the picture stands, is unchanged and still
hashes whatever the tables hold.

**Where it stops.** Nothing makes a new reader add a component. A lane
that teaches the session emitter to read a new input compiles and runs
without touching the key, and the capture is then skipped on a frame that
moved. The audit is the only check for that, which is why the plan keeps
the shortcut under adversarial review. One part of that hole can be closed
by construction, and the next section says how.

### Make a missed read impossible where one door exists

Most of a routed scene's inputs arrive through one object, its state
mirror. Every read goes through a handful of members
(`WorldStateMirror.TryValue`, `WorldStateMirror.TryColor`,
`WorldStateMirror.TryPhase`, `WorldStateMirror.Presented`). If the mirror
stamps a slot with a serial when it is read, then "did the last capture
read anything that has since moved" is a question the mirror answers
itself, for readers that exist today and for readers added later. The
environment's resolver does this by hand for its own reads. Moving it into
the mirror makes it hold for every reader.

The cost is one integer write for each read. A routed world's mirror is
read by its own scene alone, so the stamp is precise there. The boot
world's mirror is also read by the HUD, which would make the answer err
toward capturing. That is the safe direction, and the boot residency is
not a candidate.

This is a proposal, weighed against the plain list of components in the
prototype. What decides it is the audit's count of disagreements traced to
a read through the mirror: if the plain list produces none over the route
and the test world, the stamp is not needed.

### What a residency owes when it captures again

A residency that skipped captures for a long time must not then take one
long step.

- **The session's own interval.** `WorldSessionFrameSource` measures the
  time between its captures. Under the shortcut it samples its clock on a
  skipped frame too, so the first capture afterwards sees one frame's
  interval.
- **Replay cursors, followers and drivers** are at rest whenever a capture
  is skipped, so there is nothing to catch up.
- **The bake, exposure and indirect light** never stop. They advance in
  the dresser's pass and the upkeep.

## How the plan's rules and limits are met

The plan sets three rules for the shortcut, and the step's entry in
[runtime and delivery](../runtime-and-delivery.md#the-presentation-view)
adds two limits. Each is answered here.

- **The existing refusals stay outside.** They belong to the decision the
  shortcut does not touch. The skip adds one condition of its own, for
  work only a capture can do: adopting a program whose build is in flight.
- **The audit compares with the full decision and counts.** For the
  capture, the full decision has a precise form: capture and pack as
  today, and see whether the pack changed any table besides the mesh
  region, which the dresser owns, and whether any view's pass block
  differs from the held frame's. The audit has two forms, because each
  sees what the other cannot.

  In the *shadow* form nothing is skipped. The shortcut gives its answer,
  the capture and pack then run, and a frame where the shortcut said
  "equal" and the pack wrote something is a disagreement, reported in that
  frame with the table that changed. It counts captures that were
  eligible, that the shortcut would have skipped, and that were audited,
  and it fails a run with no would-be skip in a still scene. It cannot see
  an error that appears only after a real skip.

  In the *sampled* form captures really are skipped, and on a fixed share
  of skipped frames the residency captures anyway and reports whether the
  pack wrote anything. This is the form that catches drift nothing
  recorded, and it is the forced rebuild from delivered inputs that the
  plan asks for, since the capture reads the delivered state afresh.
- **The fault.** The shortcut's copy of one component is frozen after the
  full decision has received the change. The shadow form must report the
  capture in that frame. The copy to freeze is chosen from each family in
  turn: a vector component, a mirror counter, the pose count, the pool's
  rest, the environment's answer and one view.
- **A value easing on a quiet delivery, and the sky's clock.** Gaps 1 and
  2 are these two cases. The audit's cases include both, with the fraction
  forced to zero and then to one half for the first.
- **A quiet network is not a still picture.** The check never reads
  whether anything was delivered. It reads the presented tick whenever
  anything reduces it, so a sky moving along a clock is captured with
  nothing on the wire.
- **The GPU row journal cannot decide a pack.** It is listed among the
  late records, with the moved set and the owners' rest for the same
  reason, and the key uses none of them.

## The steps, each with its check

No step here was run. Each is for an implementing lane, with what it shows
and what it decides.

1. **Count the captures that changed nothing.** Have the pack report
   whether it changed any table besides the mesh region, and compare each
   view's pass block with the held frame's. Count, by residency, the
   frames captured and the frames whose capture changed nothing. The frame
   thread's breakdown already times the capture and the pack apart for all
   residencies together (`HostFrameWork`). Split those times by residency,
   and time the dresser's pass apart from the source's capture. No
   behaviour changes.

   *Check:* on the benchmark's multi-view chapter the report lists each
   shown residency with both counts and the three times.

   *What it decides:* the second count times the time of a source's
   capture and a pack is the most the shortcut can save. If that is small
   beside the 2 ms that ends the bet, the bet ends here.
2. **Confirm or dismiss the eased-value defect** (gap 1). It is a defect
   in today's code whichever way the bet goes.

   *Check:* a creation driven by an easing cell, with the fraction forced
   to zero on the first frame after a tick and to one half on the next.
   The second frame's pack moves the creation's rows, counted as
   `SdfMovedTransforms.OwedRows`. If it moves none, the defect is real.
3. **Add the early records** for gaps 1 to 6 and 8, and consult the answer
   of gap 9. Each is a member on the type named in the gap.

   *Check, one for each:* a scenario moves only that input and reads the
   new member before and after. Where the input is state, a law over
   `WorldStateMirror` holds it.
4. **Build the second vector and the shadow audit,** with the shortcut
   answering and nothing skipped.

   *Check:* zero disagreements over the route and over the test world the
   plan describes. At least one would-be skip in a still scene. The
   injected fault is reported in its frame, once for each family.
5. **Skip captures, with the sampled audit,** behind a session lever that
   is off by default.

   *Check:* no sampled capture's pack writes anything, over the same route
   and world, including a residency that captures again after a long run
   of skips.
6. **Measure,** as [what decides each bet](../rendering-research.md#what-decides-each-bet)
   states: the slowest frame, and the rays traced and bytes uploaded in
   the ten seconds after a lighting change, with the shortcut on and off
   from the same queue of background work.

Steps 1 and 2 need no decision about the bet and can start at once. Steps
3 to 6 wait for the count.

## How this compares with incremental build systems

The plan lists this reading as [research still owed](step-designs.md#research-still-owed)
beside the memo. Two sources were read for it, and a third is named for
the research round.

Salsa, an incremental computation framework for Rust, keeps one current
revision and records for each input the revision in which it last changed.
Each memoized result records what it read. A result is reused when nothing
it read changed in the current revision, and when a dependency is
recomputed to the same value its dependents are kept as well, which Salsa
calls backdating
([the Salsa book, on its algorithm](https://salsa-rs.github.io/salsa/reference/algorithm.html)).
It also sorts inputs by durability, so that a result built only from
rarely changed inputs is validated without walking its dependencies when
only a volatile input moved
([the Salsa book, on durability](https://salsa-rs.github.io/salsa/reference/durability.html)).

Three things carry over.

- **Today's code is backdating without validation.** It always recomputes
  the frame and then finds, by hash, that the result is the same. The
  shortcut is the missing first half: validating the inputs before
  recomputing.
- **Salsa cannot miss a dependency, and Puck can.** Every read in Salsa
  goes through the framework, which records it. Puck's readers read
  freely. The mirror stamp above is the same idea applied to the one door
  Puck has, and the audit covers the reads that go through no door.
- **Durability is the order of the check.** A capture's inputs fall into
  three classes by how often they move: the document and the levers, which
  move on an edit; delivered state and poses, which move on a tick; and
  the fraction and the presented tick, which can move on every frame. The
  check reads the last class first, because one cheap answer (nothing is
  interpolating and nothing reads the tick) removes it from the question.

*Build systems à la carte* (Mokhov, Mitchell and Peyton Jones, ICFP 2018)
classifies build systems by how they decide what is out of date. Its
citation was confirmed. Its text could not be opened for this memo, so its
terms are not used here, and it is the paper for the research round to
read first.

What Puck has that these systems do not is a second answer that is always
available. A build system that wrongly reuses a result has nothing to
compare against. A residency can always capture and pack, which is why the
audit can run in the shipped engine and not only in tests.
