using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;

using Xunit;

namespace Puck.World.Tests;

// The GPU work and creation laws of the overlay package's graph: the exact counts of a drawn overlay pass, submission
// identity across a device loss, that the descriptor pools the node states for the graph are the pools it creates, that
// a refused install is retried once per change of the operator's GPU faults or, for a heap refusal, of the heap's
// release revision, and that every creation the graph's install makes, failed in turn through GpuCreationFaults, is
// refused by name and releases exactly what was created before it, and the same graph swapped in again installs and
// draws.
public sealed partial class OverlayPackageLawTests {
    // A steady drawn frame, once every slot has drawn twice. The overlay pass: one command buffer, the planned transition
    // of its target back to render target from the layout publication left it in, one render pass and draw, one pipeline
    // bind, the frame and pass set binds, nothing pushed, nine sampled-image writes (the world image and the eight frame
    // slots, unbound ones given the world image), and nothing uploaded: the region holds the regions at the same bases, so
    // the pass block's values are the same every steady frame, and the frame's packed cursor records repeat what every
    // slot's share already holds, so the region owes nothing and no copy is recorded, under a ring or staged. Outside it:
    // the command buffer, the output's transition to the publish layout, the world image handed back to its host's
    // layout, and the frame group's one 256-byte constant-buffer view.
    private const string DrawnPass =
        "work overlay executed: dispatches=0 dispatches.indirect=0 draws=1 render-passes=1 command-buffers=1 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=2 push-constants=0 descriptor-writes=9 uploads.host-visible=0 clears=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=1 barriers.memory=1 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=256 clears=0";

    // The fake as a device context whose services pass through creation faults, as a backend's do.
    private sealed class FaultingDevice(FakeGpuDevice gpu, GpuCreationFaults faults) : IGpuDeviceContext {
        public long AdapterLuid => gpu.AdapterLuid;
        public GpuDeviceCapabilities? Capabilities => gpu.Capabilities;
        public GpuDeviceIdentity? Identity => gpu.Identity;
        public GpuMemoryProfile MemoryProfile => gpu.MemoryProfile;
        public GpuDeviceServices Services { get; } = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        );

        public void WaitIdle() => gpu.WaitIdle();
    }

    // The newest completed submission's report, without its submission line.
    private static string CompletedWork(ShaderPipelineRenderNode node) {
        var sample = new GpuWorkSample();

        Assert.True(condition: node.TryReadCompleted(sample: sample));

        var lines = GpuWorkReport.AppendSample(
            builder: new StringBuilder(),
            sample: sample
        ).ToString().Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\n'
        );

        return string.Join(
            separator: '\n',
            values: lines[1..]
        );
    }

    [Fact]
    public void ADrawnOverlayPassCountsExactly() {
        using var rig = new Rig();

        _ = ProduceUntilPublished(node: rig.Node);
        rig.ShowCursor();

        for (var frame = 0; (frame < 8); frame++) {
            _ = rig.Node.ProduceFrame(context: default);
        }

        Assert.Equal(
            actual: CompletedWork(node: rig.Node),
            expected: DrawnPass
        );
    }
    /// <summary>On a device that stages the overlay's region, the node leases the region-copy pipeline with the graph's
    /// build, states and creates the region's reserved copy pool beside the graph's, and records the region's copy ahead
    /// of the pass while it owes words: the first frames copy the static prefix into the device-local destination, a
    /// cursor that appears copies its records, and a steady frame owes nothing, so its counts are the ring's.</summary>
    [Fact]
    public void AStagedRegionIsCopiedAheadOfThePassAndASteadyFrameCopiesNothing() {
        using var rig = new Rig(staged: true);

        _ = ProduceUntilPublished(node: rig.Node);
        Assert.Equal(
            actual: rig.Gpu.PoolsCreated,
            expected: ShaderPipelineRenderNode.DescriptorPools(
                inFlight: InFlight,
                plan: rig.Node.Plan!,
                preview: false,
                regionCopies: [1]
            )
        );

        var copies = rig.Gpu.Count(key: "IGpuRecorder.Dispatch");

        Assert.True(
            condition: (copies > 0),
            userMessage: "The staged region's first frame recorded no copy."
        );
        rig.ShowCursor();

        for (var frame = 0; (frame < 8); frame++) {
            _ = rig.Node.ProduceFrame(context: default);
        }

        Assert.True(
            condition: (rig.Gpu.Count(key: "IGpuRecorder.Dispatch") > copies),
            userMessage: "The cursor's records were never copied."
        );
        Assert.Equal(
            actual: CompletedWork(node: rig.Node),
            expected: DrawnPass
        );
    }
    [Fact]
    public void SubmissionIdentityKeepsIncreasingAcrossADeviceLoss() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        _ = ProduceUntilPublished(node: rig.Node);
        _ = rig.Node.ProduceFrame(context: default);
        Assert.True(condition: rig.Node.TryReadCompleted(sample: sample));

        var before = sample.Submission;

        rig.Node.OnDeviceLost();
        Assert.False(condition: rig.Node.TryReadCompleted(sample: sample));

        _ = ProduceUntilPublished(node: rig.Node);
        _ = rig.Node.ProduceFrame(context: default);
        Assert.True(condition: rig.Node.TryReadCompleted(sample: sample));
        Assert.True(
            condition: (sample.Submission > before),
            userMessage: $"The rebuilt overlay graph's submission {sample.Submission} did not follow {before}."
        );
    }
    [Fact]
    public void TheDescriptorPoolsTheOverlayGraphStatesAreThePoolsItCreates() {
        using var rig = new Rig();

        _ = ProduceUntilPublished(node: rig.Node);

        Assert.Equal(
            actual: rig.Gpu.PoolsCreated,
            expected: ShaderPipelineRenderNode.DescriptorPools(
                inFlight: InFlight,
                plan: rig.Node.Plan!,
                preview: false
            )
        );
    }
    /// <summary>The operator's GPU faults are an input of a refused candidate (<see cref="GpuCreationFaults.Revision"/>):
    /// a refusal a fault caused holds over unchanged frames, arming another fault retries the candidate exactly once
    /// (refused again by the fault just armed, whose firing is no further change), and disarming retries it exactly once
    /// more, which installs the graph.</summary>
    [Fact]
    public void ArmingAFaultRetriesARefusedCandidateOnceAndDisarmingInstallsItOnce() {
        var faults = new GpuCreationFaults();
        using var rig = new Rig(
            faults: faults,
            trackObjects: true
        );

        void ProduceUntil(Func<bool> condition, string what) => Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    _ = rig.Node.ProduceFrame(context: default);

                    return condition();
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: what
        );

        faults.Arm(kind: GpuCreationKind.Image);
        ProduceUntil(
            condition: () => (rig.Node.LastSwapError is not null),
            what: "The armed image fault never refused the graph."
        );

        var imagesAsked = faults.SeenOf(kind: GpuCreationKind.Image);

        for (var frame = 0; (frame < 3); frame++) {
            _ = rig.Node.ProduceFrame(context: default);
        }

        Assert.Equal(
            actual: (faults.SeenOf(kind: GpuCreationKind.Image), rig.Node.IsBuildingCandidate),
            expected: (imagesAsked, false)
        );

        faults.Arm(kind: GpuCreationKind.Image);
        ProduceUntil(
            condition: () => ((faults.SeenOf(kind: GpuCreationKind.Image) == (imagesAsked + 1L)) && (rig.Node.LastSwapError is not null)),
            what: "Arming a fault never retried the refused graph."
        );

        for (var frame = 0; (frame < 3); frame++) {
            _ = rig.Node.ProduceFrame(context: default);
        }

        Assert.Equal(
            actual: (faults.SeenOf(kind: GpuCreationKind.Image), rig.Node.IsBuildingCandidate),
            expected: ((imagesAsked + 1L), false)
        );
        Assert.StartsWith(
            actualString: rig.Node.LastSwapError!.Message,
            expectedStartString: $"[{GpuCreationFaults.RefusalCode}] "
        );

        faults.Disarm();
        ProduceUntil(
            condition: () => rig.Node.IsReady,
            what: "Disarming never installed the refused graph."
        );
        Assert.Null(@object: rig.Node.LastSwapError);

        var created = rig.Gpu.Created.Count;

        for (var frame = 0; (frame < 3); frame++) {
            _ = rig.Node.ProduceFrame(context: default);
        }

        Assert.Equal(
            actual: rig.Gpu.Created.Count,
            expected: created
        );
    }
    /// <summary>A graph whose pools the device's heap cannot admit is refused by name before it creates anything, tries
    /// nothing again over frames that return no heap space, and installs once another owner returns heap space: the
    /// heap's release revision (<see cref="IGpuBindings.HeapReleaseRevision"/>) is the change a heap refusal waits
    /// for.</summary>
    [Fact]
    public void AGraphTheHeapRefusesInstallsOnceAnotherOwnerReturnsHeapSpace() {
        using var rig = new Rig();
        IGpuBindings bindings = rig.Gpu;
        var pools = ShaderPipelineRenderNode.DescriptorPools(
            inFlight: InFlight,
            plan: new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: Graph()).Pipeline,
            preview: false
        );
        var demand = ((uint)pools.Sum(selector: static pool => ((long)pool.HeapDescriptors)));

        rig.Gpu.DescriptorHeap = new GpuDescriptorHeapBudget(capabilities: (GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: 3,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 64,
            shaderModel: "6.6",
            staticSamplerHeapSize: 0,
            viewHeapSize: 0
        ) with {
            ViewHeapSize = demand,
        }));

        // Another owner holds one view descriptor of a heap exactly the graph's size.
        var other = bindings.CreatePool(
            name: default,
            sizes: new GpuDescriptorPoolSizes(
                CombinedImageSamplerCount: 0,
                MaxSets: 1,
                StorageBufferCount: 1,
                StorageImageCount: 0
            )
        );

        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    _ = rig.Node.ProduceFrame(context: default);

                    return (rig.Node.LastSwapError is not null);
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The heap never refused the graph."
        );
        Assert.IsType<GpuDescriptorHeapRefusalException>(@object: rig.Node.LastSwapError);
        Assert.StartsWith(
            actualString: rig.Node.LastSwapError!.Message,
            expectedStartString: $"[{GpuDescriptorHeapBudget.RefusalCode}] 'shader pipeline root' needs {demand} view "
        );
        Assert.Empty(collection: rig.Gpu.PoolsCreated.Skip(count: 1));

        var admissions = rig.Gpu.Admissions;

        // Frames that return no heap space try nothing again.
        for (var frame = 0; (frame < 3); frame++) {
            _ = rig.Node.ProduceFrame(context: default);
        }

        Assert.Equal(
            actual: (rig.Gpu.Admissions, rig.Node.IsBuildingCandidate),
            expected: (admissions, false)
        );

        // The other owner's release is the change a heap refusal waits for: the graph builds again and installs, with
        // no device loss.
        bindings.DestroyPool(poolHandle: other);
        _ = ProduceUntilPublished(node: rig.Node);
        Assert.Null(@object: rig.Node.LastSwapError);
        Assert.Equal(
            actual: rig.Gpu.Admissions,
            expected: (admissions + 1)
        );
        Assert.Equal(
            actual: rig.Gpu.PoolsCreated.Skip(count: 1),
            expected: pools
        );
    }
    [Fact]
    public void EveryCreationOfTheOverlayGraphFaultedInTurnIsRefusedByNameAndReleasesWhatWasCreated() {
        var expected = new Dictionary<GpuCreationKind, long>();
        var measuredFaults = new GpuCreationFaults();

        using (var measured = new Rig(
            faults: measuredFaults,
            trackObjects: true
        )) {
            _ = ProduceUntilPublished(node: measured.Node);

            foreach (var kind in GpuCreationFaults.Kinds) {
                expected[kind] = measuredFaults.SeenOf(kind: kind);
            }
        }

        // The recorder creates its framebuffers at install, so a framebuffer fault refuses the install by name like any
        // other creation rather than escaping a produced frame.
        Assert.True(condition: (expected[GpuCreationKind.Framebuffer] > 0L));

        var faulted = 0;

        foreach (var kind in GpuCreationFaults.Kinds) {
            for (var nth = 1; (nth <= expected[kind]); nth++) {
                var faults = new GpuCreationFaults();
                using var rig = new Rig(
                    faults: faults,
                    trackObjects: true
                );

                faults.Arm(
                    kind: kind,
                    nth: nth
                );
                Assert.True(
                    condition: SpinWait.SpinUntil(
                        condition: () => {
                            _ = rig.Node.ProduceFrame(context: default);

                            return (rig.Node.LastSwapError is not null);
                        },
                        timeout: TimeSpan.FromSeconds(value: 30)
                    ),
                    userMessage: $"The {GpuCreationFaults.NameOf(kind: kind)} creation {nth} fault was never reported."
                );

                // Refused by the fault, which names the creation it failed, and everything created before it released
                // exactly once.
                var fault = Assert.IsType<GpuCreationFaultException>(@object: rig.Node.LastSwapError);

                Assert.Equal(
                    actual: (fault.Kind, fault.Creation),
                    expected: (kind, nth)
                );
                Assert.Equal(
                    actual: $"{kind} {nth}: unreleased [{string.Join(separator: ", ", values: rig.Gpu.Created.Where(predicate: static created => (created.DisposeCount != 1)))}], held {rig.Gpu.Memory.Held}",
                    expected: $"{kind} {nth}: unreleased [], held 0"
                );

                // The same graph swapped in again installs and publishes.
                rig.Swap();
                _ = ProduceUntilPublished(node: rig.Node);
                Assert.Null(@object: rig.Node.LastSwapError);
                faulted++;
            }
        }

        Assert.Equal(
            actual: faulted,
            expected: expected.Values.Sum()
        );
        Assert.True(condition: (faulted > 0));
    }
}
