using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;

using Xunit;

namespace Puck.World.Tests;

// The GPU work and creation laws of the overlay package's graph: the exact counts of a drawn overlay pass, submission
// identity across a device loss, that the descriptor pools the node states for the graph are the pools it creates, and
// that every creation the graph's install makes, failed in turn through GpuCreationFaults, is refused by name and
// releases exactly what was created before it, and the same graph swapped in again installs and draws.
public sealed partial class OverlayPackageLawTests {
    // A steady drawn frame, once every slot has drawn twice. The overlay pass: one command buffer, the planned transition
    // of its target back to render target from the layout publication left it in, one render pass and draw, one pipeline
    // bind, the frame and pass set binds, nothing pushed, nine sampled-image writes (the world image and the eight frame
    // slots, unbound ones given the world image), and 128 uploaded bytes: the 112 bytes of the frame's packed cursor
    // records, and the pass block's words 6, 7, 8 and 10, 16 bytes, since the region bases WritePassValues writes there
    // are shifted per frame slot and so differ from the previous frame's. Outside it: the command buffer, the
    // output's transition to the publish layout, the world image handed back to its host's layout, and the frame group's
    // one 256-byte constant-buffer view.
    private const string DrawnPass =
        "work overlay executed: dispatches=0 dispatches.indirect=0 draws=1 render-passes=1 command-buffers=1 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=2 push-constants=0 descriptor-writes=9 uploads.host-visible=128 clears=0\n" +
        "work outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=1 barriers.memory=1 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=256 clears=0";

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
