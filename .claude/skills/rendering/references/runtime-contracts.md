# Render-graph runtime contracts

These five invariants govern the frame-graph runtime (`RenderGraphRuntime` and
its partials in `src/Puck.Shaders/Graph`, `ShaderPipelineRenderNode*.cs`, the
scheduling types in `src/Puck.Hosting/Graph`) and every package, producer and
host that feeds it. A change to the runtime keeps each one, and a review of such
a change hunts each one's violations. Each section states the rule, how the code
holds it, the laws that pin it, and where the code is weaker than the rule. The
mechanism detail stays in [../SKILL.md](../SKILL.md#shader-manifests-and-pipelines)
and [Frame graphs](../../../../docs/reference/shaders.md#frame-graphs); this file
does not repeat it.

## 1. Image lifetime is leased per image and per reader

**Rule.** An image is disposed only after its owner has dropped it and every
submission that reads it has completed. Each reader holds its own lease from
the moment it binds the image until the fence of the submission that read it,
and each lease retires exactly once.

**How the code holds it.**

- An external or sampled image reaches a pass as a `GpuImageLease`
  (`src/Puck.Hosting/GpuImageLease.cs`). A recording holds it in its frame
  slot's `LeaseRetireList` (`Hold`, `MoveTo`, `RetireAll`), which retires it
  after that slot's fence.
- A node's own published image (`HeldImage` in
  `ShaderPipelineRenderNode.Retirement.cs`) is disposed at the fence of the
  submission made `RetirementLag` (2) submissions after it stopped being
  published. A replaced graph is retired once the queue finishes the node's
  latest submission, never through a device drain.
- At reconfiguration, a producer removed while a kept consumer still reads it
  is held whole (`RetiredProducer`, counted in `RetiredProducers`) until the
  consumer's replacement installs, and `ShaderPipelineRenderNode.HoldBinding`
  holds a binding's lease.

**Laws.** `RenderGraphRuntimeLawTests.Leases`
(`ALeaseARecordingHoldsRetiresAfterItsFrameSlotsFence`),
`LeaseRetireListLawTests` (`tests/Puck.Hosting.Tests`),
`ShaderPipelineRenderNodeLawTests` (retirement and disposal counts),
`RenderGraphRuntimeLawTests.Reconfigure`.

**Where the code is weaker.** A node's own image is protected by a count of its
producer's submissions, not by a lease per reader: another instance that reads
it holds no lease of its own, so correctness rests on the lag outliving every
reader's submission. `GpuImageLease.Retire` invokes its callback with no guard
against a second call; only the list's discipline makes it once.

**Violations to hunt.** An image disposed or overwritten while a submission
that reads it is pending: a paused or slow reader, a capture, the display after
present when no submission follows, a swapchain resize, device loss and
teardown order. A lease never retired: a frame slot whose submission is never
made, a cancelled capture, a reconfiguration in the middle of a capture. A
lease retired twice. A steady frame that allocates lease bookkeeping.

## 2. Nothing resolves silently to nothing

**Rule.** A read, alias, package, source or capture that cannot be served
either refuses by name, with a code and a message naming the instance, pass,
input or producer, or binds a stand-in that is recorded and reported. It never
yields an empty, default or stale result that the frame presents as success.

**How the code holds it.**

- `RenderGraphInstanceSet.TryCreate` refuses an invalid set with a
  `RenderGraphInstanceRefusal` whose code is a `RenderGraphInstanceRefusalCode`.
- `RenderGraphRuntime.TryReconfigure` refuses with a
  `RenderGraphRuntimeRefusal` whose code is a `RenderGraphRuntimeRefusalCode`
  (`GraphCount`, `Root`, `OutputKind`, `PackageUnserved`, `InputVersion`,
  `InputProducer`, `InputKind`, `InputSize`, `ExternalProducer`).
- At frame time, an image input whose producer has no output binds a
  transparent-black stand-in (`RenderGraphRuntime.Bind`,
  `RenderGraphRuntime.StandIns.cs`), recorded per instance. A capture waits on
  that instance, and `RenderGraphRuntime.UnservedCaptureReasonOf` names the
  producer. A buffer input with no output does not render.
- A package pass that draws nothing publishes its input in its output's place
  only where that is sound; otherwise `ShaderPipelineRenderNode.AliasRefusalOf`
  refuses it by name.
- A pipeline source with no stored package and no compiler is refused as
  `SHADERPKG_ABSENT` (`ShaderSourceClosure`), beside the other `SHADERPKG_*`
  codes.
- `SdfWorldResidency.NotReadyReason` names a pending build or a refused table
  build.

**Laws.** `RenderGraphRuntimeLawTests.Lifetime`
(`ACaptureNoRootOutputServesIsRefusedByNameAndThenDropped`),
`RenderGraphRuntimeLawTests.Alias`
(`APassWhoseOutputAnotherPassReadsIsRefusedByNameWhenItDrawsNothing`,
`AnOutputCannotStandForAPreviousFrameInputAndIsRefusedByName`),
`RenderGraphRuntimeLawTests.UnboundReads`, and the `pipeline-package` and
`no-device-compile` canaries.

**Where the code is weaker.** A refused residency build reads as not ready:
nothing in the frame result separates it from a build still running (§4).

**Violations to hunt.** A path that returns null, empty or a default handle and
lets the frame present; a stand-in bound without being recorded; a refusal
whose message omits the name a reader needs; a released or retired producer
whose readers still bind its last image.

## 3. A capture's pixels are pinned or copied

**Rule.** A capture reads pixels that cannot change before its readback
completes: the image of the frame it was served from, held by the node that
rendered it until the readback finishes, or a copy into an image the capturing
node owns. A capture never reads a handle that a later frame of another
instance can overwrite.

**How the code holds it.** `ShaderPipelineRenderNode.CaptureIfPending` reads the
node's own last surface, which the node's retirement rule (§1) holds. The
runtime forwards a capture to a node only when the node is ready and the
instance read no stand-in or tainted input. A capture waits while a preview
builds, while the root has not yet presented at the requested extent
(`ShownAtItsExtent`), and while its readback encoder (`SurfaceEncoder`) builds. The offscreen host
holds its clock at an armed, unserved capture, and refuses it as `unserved`
after 60 seconds of holding in all. A root frame that drew nothing captures its
input version in that version's layout.

**Laws.** `WorldCaptureHoldLawTests`
(`NoTickIsSteppedWhileACaptureIsArmedAndUnservedOffscreen`),
`WorldPipelineResizedWaitLawTests`
(`AResizedWaitHoldsUntilTheGraphAtTheNewExtentInstallsPausedOrRunning`),
`RenderGraphRuntimeLawTests.RootCapture`,
`RenderGraphRuntimeLawTests.UnboundReads`
(`ACaptureWaitsForAScheduledUnboundScreenReadToHaveAnOutput`),
`RenderGraphRuntimeLawTests.Alias`
(`ARootCaptureOfAFrameThatDrewNothingReadsTheInputVersionInItsLayout`).

**Where the code is weaker.** There is no copy path. A node that published
another instance's image in its output's place serves a capture from that
image under the producer's retirement rule, without a lease of its own.

**Violations to hunt.** A capture served from an image a later frame
overwrites; a capture served at the wrong extent or from a frame of another
tick; a capture that waits forever on a condition no frame can clear.

## 4. Render completion is Rendered, NotYetRenderable or Refused

**Rule.** A frame's outcome is one of three. *Rendered*: the image shows this
frame's scheduled work. *Not yet renderable*: a condition that waiting ends,
such as a cold build or a pending upload. *Refused*: a condition no wait ends,
such as a refused package or build, a source that has ended, or a view with no
camera. A permanent condition is always refused, so a host that waits on "not
yet" never waits forever, and a refused frame names its reason.

**How the code holds it.** `IRenderRoot.ProduceFrame` returns a `Surface` and no
outcome. Readiness is reported beside the frame instead: capture service and
`UnservedCaptureReasonOf` (§2, §3), and `SdfWorldResidency.NotReadyReason`. The
offscreen host paces by `HostPacing.OneTickPerFrame` and `FixedStepPump.TryStep`,
and holds a tick only at an armed capture.

**Laws.** `WorldCaptureHoldLawTests` and `SdfPipelineBuildLivenessLawTests`
(the pump never blocks on pipeline creation).

**Where the code is weaker.** A host cannot tell a frame that has not rendered
yet from one that never will. Outside an armed capture, the offscreen host
steps regardless, and inside one only the 60-second backstop ends a permanent
wait. A change that adds an outcome to the frame classifies every condition by
whether a wait can end it.

**Violations to hunt.** A permanent condition reported as waiting: a source
with no frames, a disabled view, a portal past its nesting limit, a world with
no camera, a capture feed that ended. A "rendered" result carrying an older
tick's image. Commands landing on a different tick when a host holds or
retries a frame.

## 5. History epochs reset only for unseen views, never for cadence gaps

**Rule.** Temporal history (accumulated samples, reprojected frames) resets when
its previous samples are wrong for this view: a cut, a binding change, an
output extent or reconstruction ceiling change, an enable or debug toggle, a pose the view did
not render, or a view shown again after it went unseen. A gap the view's
cadence scheduled (a refresh divisor, a standing consumer, a budget skip) is
continuity and never resets history.

Every render-graph history resource follows one successful-write rule. A
previous-frame read creates no writer demand, including a P11 self-reference.
A slot advances only when its writer records and submits successfully; a
failed or skipped write leaves the preceding history intact. Device loss
discards the ring and starts with no history. Publication and export remain
allowed, and a failure in an export handoff after submission keeps the
committed write. No consumer uses a private advancement mode.

**How the code holds it.** `SdfTemporalHistory` (`src/Puck.SdfVm`) continues
history while `Continues(epoch, previousPoses)` holds: the frame's
`SdfTemporalEpoch` (`Binding`, `Cut`, `Width`, `Height`, `Ceiling`, `Enabled`,
`Debug`, `Temporal`, `Unread`) equals the last one, and the previous poses are the ones the instance
last rendered. Any difference resets the epoch. A frame gap with an unchanged
epoch and unchanged poses continues it. `SdfWorldPasses.EpochOf` sets `Enabled`
while the view resolves temporally or a capture converges. The runtime counts
only frames outside displayed outputs, including held consumer outputs, in
`Unread`. A dynamic render-grid change restarts settling without discarding
the history ring. `ShaderPipelineRenderNode` reserves a ring instance for a
recording writer and commits its cursor at submission; its recovery restores
the existing access tracker on failure. `IRenderGraphPackageRecorder.Submitted`
commits the temporal sample metadata, and `SdfResolveRecorder` caches history
descriptors by their bound handles as well as their slot and tables.

**Laws.** `SdfTemporalHistoryLawTests`
(`EveryEpochInputAndAPoseGapResetAtThePixelCenter`,
`ACountedSourceDrivesTheIndexAndUncountedRendersRepeatTheirSample`,
`AJitteredRenderDoesNotStandOnceSamplingEnds`),
`SdfWorldPassesLawTests.Temporal`, the runtime's convergence laws,
`RenderGraphHistoryLawTests`, and `RenderGraphSchedulerLawTests.History`.

**Verification boundary.** CPU laws hold scheduling, cursor recovery and
submission callbacks on a fake device. Temporal and device-loss canaries and
parity hold the backend recordings and pixels on Vulkan and Direct3D 12.

**Violations to hunt.** History reset every refresh period by a divisor (the
view never converges); history kept across a cut, a crossing into another
residency, a resize, or a return to view; a counter that overflows or never
resets; a reconfiguration that loses or keeps the count wrongly.
Also hunt a history read that demands its writer, a cursor moved by a skipped
or refused submission, temporal metadata committed during recording, and a
cached descriptor that still names a submission slot's former history.
