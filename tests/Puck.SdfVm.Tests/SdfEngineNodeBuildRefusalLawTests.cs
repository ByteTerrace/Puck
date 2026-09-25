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
/// names the refusal, and everything the failed construction created is released while the pipeline set's lease is
/// kept. A refused build is tried again only when one of its inputs changes (the program, the extent, a kernel reload
/// request, the device); frames that change none of them attempt nothing. Each engine attempt that passes admission
/// creates exactly one descriptor pool, which is how the laws count attempts; an attempt the device's descriptor heap
/// refuses creates nothing, and the fake counts its admissions instead.
/// </summary>
public sealed class SdfEngineNodeBuildRefusalLawTests {
    private const uint Extent = 32;
    private const int Frames = 5;

    [Fact]
    public void AFaultedFirstBuildIsRefusedByNameAndBuildsOnceTheProgramChanges() {
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

        // The fault fired once, but nothing a frame brings could have fixed the build, so none tries it again.
        rig.ProduceUnchanged(frames: Frames);
        Assert.Equal(
            actual: rig.EngineAttempts,
            expected: 1
        );

        rig.ChangeProgram();
        _ = rig.Node.ProduceFrame(context: in rig.Context);
        Assert.True(condition: rig.Node.IsReady);
        Assert.Null(@object: rig.Node.NotReadyReason);
        Assert.Equal(
            actual: rig.EngineAttempts,
            expected: 2
        );
    }
    [Fact]
    public void AFaultedRebuildAfterADeviceLossIsRefusedByNameAndTheNextDeviceLossRebuilds() {
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

        var attempts = rig.EngineAttempts;

        rig.ProduceUnchanged(frames: Frames);
        Assert.Equal(
            actual: rig.EngineAttempts,
            expected: attempts
        );

        rig.Node.OnDeviceLost();
        _ = rig.Node.ProduceFirstFrame(context: in rig.Context);
        Assert.Null(@object: rig.Node.NotReadyReason);
        Assert.Equal(
            actual: rig.EngineAttempts,
            expected: (attempts + 1)
        );
    }
    [Fact]
    public void AnEngineTheHeapCannotAdmitIsRefusedByNameAllocatesNothingAndRetriesOnlyOnAChangedInput() {
        using var rig = new Rig(reportVersion: SdfIsa.Version);
        var demand = SdfWorldEngine.DescriptorPoolSizes(
            brickPool: false,
            brickUpload: false
        ).HeapDescriptors;
        var heap = Heap(views: (demand - 1U));

        rig.Gpu.DescriptorHeap = heap;

        var refusal = rig.ProduceUntilRefused();

        // The refusal is a named build refusal, never a throw out of the frame, and the construction was refused before
        // it allocated: nothing but the pipeline set exists, no pool was created and the heap lent nothing.
        Assert.True(condition: refusal.IsEmpty);
        Assert.Contains(
            expectedSubstring: $"[{GpuDescriptorHeapBudget.RefusalCode}] 'SDF world engine' needs {demand} view descriptors",
            actualString: rig.Node.NotReadyReason
        );
        rig.AssertNothingButThePipelineSetWasCreated();
        Assert.Empty(collection: rig.Gpu.PoolsCreated);
        Assert.Equal(
            actual: (heap.FreeViewDescriptors, heap.LivePools),
            expected: ((demand - 1U), 0)
        );
        Assert.Equal(
            actual: rig.Gpu.Admissions,
            expected: 1
        );

        // Frames that change no input ask the heap nothing again.
        rig.ProduceUnchanged(frames: Frames);
        Assert.Equal(
            actual: rig.Gpu.Admissions,
            expected: 1
        );

        // A new extent is a changed input: one attempt, refused the same way, and then none again.
        Assert.False(condition: rig.Node.Produce(
            context: in rig.Context,
            height: (Extent * 2U),
            width: (Extent * 2U)
        ));
        rig.ProduceUnchanged(frames: Frames);
        Assert.Equal(
            actual: rig.Gpu.Admissions,
            expected: 2
        );
        Assert.Empty(collection: rig.Gpu.PoolsCreated);

        // A heap that holds the pool admits the next changed build, which creates the engine and its one pool.
        rig.Gpu.DescriptorHeap = Heap(views: demand);
        rig.ChangeProgram();
        _ = rig.Node.ProduceFrame(context: in rig.Context);
        Assert.True(condition: rig.Node.IsReady);
        Assert.Null(@object: rig.Node.NotReadyReason);
        Assert.Equal(
            actual: (rig.Gpu.Admissions, rig.EngineAttempts),
            expected: (3, 1)
        );
    }
    [Fact]
    public void APersistentRefusalBuildsOnceAndEachChangedInputRetriesOnce() {
        using var rig = new Rig(reportVersion: unchecked((byte)(SdfIsa.Version + 1)));

        _ = rig.ProduceUntilRefused();
        rig.ProduceUnchanged(frames: Frames);
        Assert.Equal(
            actual: rig.EngineAttempts,
            expected: 1
        );
        Assert.Contains(
            expectedSubstring: "SDF ISA version mismatch",
            actualString: rig.Node.NotReadyReason
        );

        // A new program is a changed input: one attempt, refused the same way, and then none again.
        rig.ChangeProgram();
        rig.ProduceUnchanged(frames: Frames);
        Assert.Equal(
            actual: rig.EngineAttempts,
            expected: 2
        );

        // So is a kernel reload request.
        Assert.True(condition: rig.Node.RequestShaderReload());
        rig.ProduceUnchanged(frames: Frames);
        Assert.Equal(
            actual: rig.EngineAttempts,
            expected: 3
        );
        Assert.False(condition: rig.Node.IsReady);
        rig.AssertOnlyThePipelineSetIsHeld();
    }

    // A Direct3D 12 heap of the given view descriptors, the only kind an engine's pool takes.
    private static GpuDescriptorHeapBudget Heap(uint views) => new(capabilities: (GpuDeviceCapabilities.FromDirectX(
        resourceBindingTier: 3,
        rootSignatureVersion: "1.1",
        samplerHeapSize: 0,
        shaderModel: "6.6",
        viewHeapSize: 0
    ) with {
        ViewHeapSize = views,
    }));

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
        public SdfFrame Frame { get; set; } = frame;

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            Frame;
    }
    private sealed class Rig : IDisposable {
        private readonly FrameContext m_context;
        private readonly FixedFrameSource m_source;

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
            m_source = new FixedFrameSource(frame: frame);
            Node = new SdfEngineNode(
                brickPoolVoxelCapacity: 0,
                frameSource: m_source,
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
        // Each engine construction creates one descriptor pool and the pipeline set none, so the pools are the attempts.
        public int EngineAttempts => Gpu.Created.Count(predicate: static created => (created.Kind == "descriptor pool"));
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
        // Nothing but the pipeline set's pipelines and shader modules was ever created, and no device-local memory is held.
        public void AssertNothingButThePipelineSetWasCreated() {
            Assert.All(
                action: static created => Assert.True(condition: IsPipelineSetObject(created: created)),
                collection: Gpu.Created
            );
            Assert.Equal(
                actual: Gpu.Memory.Held,
                expected: 0L
            );
        }
        // Replaces the frame's program with an equal one built again: a new program is a changed build input.
        public void ChangeProgram() {
            var builder = new SdfProgramBuilder();

            builder.Sphere(
                material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                radius: 1f
            );
            m_source.Frame = (m_source.Frame with { Program = builder.Build() });
        }
        public void Dispose() => Node.Dispose();
        // Produces frames that change no build input, each presenting nothing new unless the node is ready.
        public void ProduceUnchanged(int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                var surface = Node.ProduceFrame(context: in m_context);

                Assert.Equal(
                    actual: surface.IsEmpty,
                    expected: !Node.IsReady
                );
            }
        }
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
