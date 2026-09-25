using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for an <see cref="SdfEngineNode"/> whose engine build fails, over a <see cref="FakeGpuDevice"/> that tracks
/// every object it creates, behind <see cref="GpuCreationFaults"/>. The node builds an engine only when it has none (its
/// first frame, and the rebuild after a device loss disposed the previous one), so a failed build has no previous engine
/// to present: the produced frame returns nothing new rather than throwing, <see cref="SdfEngineNode.NotReadyReason"/>
/// names the refusal, everything the failed construction created is released while the pipeline set's lease is kept, and
/// the next produced frame builds again.
/// </summary>
public sealed class SdfEngineNodeBuildRefusalLawTests {
    private const uint Extent = 32;

    [Fact]
    public void AFaultedFirstBuildIsRefusedByNameAndTheNextFrameBuildsTheEngine() {
        using var rig = new Rig(reportVersion: SdfIsa.Version);

        // The pipeline set's build creates no command pool, so the second one created is the engine's last: the refused
        // construction had created nearly everything else first.
        rig.Faults.Arm(
            kind: GpuCreationKind.CommandPool,
            nth: 2
        );

        var refusal = rig.ProduceUntilRefused();

        Assert.True(condition: refusal.IsEmpty);
        Assert.Contains(
            expectedSubstring: GpuCreationFaults.RefusalCode,
            actualString: rig.Node.NotReadyReason
        );
        rig.AssertOnlyThePipelineSetIsHeld();

        _ = rig.Node.ProduceFirstFrame(context: in rig.Context);
        Assert.Null(@object: rig.Node.NotReadyReason);
    }
    [Fact]
    public void AFaultedRebuildAfterADeviceLossIsRefusedByNameAndTheNextFrameRebuilds() {
        using var rig = new Rig(reportVersion: SdfIsa.Version);

        _ = rig.Node.ProduceFirstFrame(context: in rig.Context);
        rig.Node.OnDeviceLost();
        rig.Faults.Arm(kind: GpuCreationKind.Buffer);

        var refusal = rig.ProduceUntilRefused();

        Assert.True(condition: refusal.IsEmpty);
        Assert.Contains(
            expectedSubstring: GpuCreationFaults.RefusalCode,
            actualString: rig.Node.NotReadyReason
        );
        rig.AssertOnlyThePipelineSetIsHeld();

        _ = rig.Node.ProduceFirstFrame(context: in rig.Context);
        Assert.Null(@object: rig.Node.NotReadyReason);
    }
    [Fact]
    public void ARefusalThatRecursIsRetriedEveryFrameAndLeaksNothing() {
        using var rig = new Rig(reportVersion: unchecked((byte)(SdfIsa.Version + 1)));

        _ = rig.ProduceUntilRefused();

        for (var frame = 0; (frame < 3); frame++) {
            Assert.True(condition: rig.Node.ProduceFrame(context: in rig.Context).IsEmpty);
        }

        Assert.False(condition: rig.Node.IsReady);
        Assert.Contains(
            expectedSubstring: "SDF ISA version mismatch",
            actualString: rig.Node.NotReadyReason
        );
        rig.AssertOnlyThePipelineSetIsHeld();
    }

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
    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
    private sealed class Rig : IDisposable {
        private readonly FrameContext m_context;

        public Rig(byte reportVersion) {
            var builder = new SdfProgramBuilder();

            builder.Sphere(
                material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                radius: 1f
            );

            var frame = new SdfFrame(
                Program: builder.Build(),
                ProgramChanged: false,
                Time: 0f,
                Views: [new SdfViewSnapshot(
                    Camera: CameraSnapshot.LookAt(
                        fieldOfViewRadians: 1f,
                        position: new Vector3(x: 0f, y: 0f, z: -5f),
                        target: Vector3.Zero,
                        viewportHeight: Extent,
                        viewportWidth: Extent
                    ),
                    Region: new NormalizedRect(
                        Height: 1f,
                        Width: 1f,
                        X: 0f,
                        Y: 0f
                    )
                )]
            );

            Gpu = new FakeGpuDevice(
                reportVersion: reportVersion,
                trackObjects: true
            );
            Faults = new GpuCreationFaults();
            Node = new SdfEngineNode(
                brickPoolVoxelCapacity: 0,
                frameSource: new FixedFrameSource(frame: frame),
                height: Extent,
                kernels: SdfTestPipelines.Kernels(),
                pipelines: new SdfWorldPipelineCache(),
                width: Extent
            );
            m_context = new FrameContext(
                AccumulatorTicks: 0UL,
                DeltaTicks: 0UL,
                ElapsedTicks: 0UL,
                FrameDeltaTicks: 0UL,
                Host: new HostContext(capabilities: new Dictionary<Type, object> {
                    [typeof(IGpuDeviceContext)] = new FaultingDevice(
                        faults: Faults,
                        gpu: Gpu
                    ),
                }),
                StepTicks: 0UL,
                TargetHeight: Extent,
                TargetWidth: Extent
            );
        }

        public ref readonly FrameContext Context => ref m_context;
        public GpuCreationFaults Faults { get; }
        public FakeGpuDevice Gpu { get; }
        public SdfEngineNode Node { get; }

        // Every object the engines created was released exactly once, the pipeline set the node still leases (pipelines
        // and their shader modules nothing released) is held, a set a device loss released was released once, and no
        // device-local memory is held.
        public void AssertOnlyThePipelineSetIsHeld() {
            Assert.All(
                action: static created => Assert.Equal(
                    actual: created.DisposeCount,
                    expected: 1
                ),
                collection: Gpu.Created.Where(predicate: static created => !IsPipelineSetObject(created: created))
            );
            Assert.All(
                action: static created => Assert.InRange(
                    actual: created.DisposeCount,
                    high: 1,
                    low: 0
                ),
                collection: Gpu.Created.Where(predicate: IsPipelineSetObject)
            );
            Assert.Contains(
                collection: Gpu.Created,
                filter: static created => (IsPipelineSetObject(created: created) && (created.DisposeCount == 0))
            );
            Assert.Equal(
                actual: Gpu.Memory.Held,
                expected: 0L
            );
        }
        public void Dispose() => Node.Dispose();
        // Produces frames until the node refuses its engine build, and returns that frame's surface. A refusal never
        // throws out of the frame. The bound is liveness for a pipeline build on the thread pool; it decides nothing.
        public Surface ProduceUntilRefused() {
            var surface = default(Surface);
            var context = m_context;
            var node = Node;

            Assert.True(condition: SpinWait.SpinUntil(
                condition: () => {
                    surface = node.ProduceFrame(context: in context);

                    return (node.NotReadyReason?.Contains(
                        comparisonType: StringComparison.Ordinal,
                        value: "refused"
                    ) ?? false);
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ));
            Assert.False(condition: node.IsReady);

            return surface;
        }

        private static bool IsPipelineSetObject(FakeGpuDevice.Creation created) =>
            (created.Kind is "compute pipeline" or "shader module");
    }
}
