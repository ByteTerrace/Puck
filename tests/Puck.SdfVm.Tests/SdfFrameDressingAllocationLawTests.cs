using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for what a captured frame costs once its source and its residency have dressed it: the composition completes the
/// frame its host dressed in place, and the mesh dresser answers it with its own rows by reference, so a steady capture and
/// produce copy no frame however many distinct input frames the host hands over.
/// </summary>
public sealed class SdfFrameDressingAllocationLawTests {
    private const uint Extent = 64;

    [Fact]
    public void ASteadyCompositionCaptureOfADistinctFrameEachTimeAllocatesNothing() {
        using var rig = new Rig();

        rig.Warm();
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => _ = rig.Capture()));
    }
    [Fact]
    public void ASteadyResidencyProduceOfADistinctFrameEachTimeAllocatesNothing() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        void Frame() {
            rig.Residency.ProduceFrame(context: in rig.Context);
            _ = rig.Residency.Work.TryReadCompleted(sample: sample);
        }

        rig.Residency.ProduceFirstFrame(context: in rig.Context);
        for (var warm = 0; (warm < 8); warm++) { Frame(); }
        rig.Warm();
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Frame));
    }
    [Fact]
    public void ADressedFrameReferencesTheDressersRowsAndLeavesTheHostsInputsAsTheyWere() {
        using var rig = new Rig();

        rig.Warm();

        var frame = rig.Capture()!;
        var host = frame.MeshDraws;

        Assert.Null(@object: frame.MeshRows);
        using var dresser = new SdfMeshDresser(bake: new SdfMeshPathBake());

        var dressed = dresser.Dress(frame: frame);

        Assert.Same(expected: frame, actual: dressed);
        Assert.NotNull(@object: dressed.MeshRows);
        Assert.Same(expected: host, actual: dressed.MeshDraws);
        Assert.Equal(expected: 0L, actual: dressed.MeshDrawsRevision);
        Assert.Null(@object: dressed.MeshDrawChanges);

        // Another dresser's rows are its own: the frame is copied before it is answered with this one's.
        using var other = new SdfMeshDresser(bake: new SdfMeshPathBake());

        var answered = other.Dress(frame: dressed);

        Assert.NotSame(expected: dressed, actual: answered);
        Assert.NotSame(expected: dressed.MeshRows, actual: answered.MeshRows);
        Assert.Same(expected: dresser.Dress(frame: dressed).MeshRows, actual: dressed.MeshRows);
    }

    // A host that hands the composition a different frame object each capture, from a set made after its first captures
    // (so each carries the moved set's endpoint the table stands at), the way a real host hands a new frame each time.
    private sealed class PooledDresser : ISdfFrameDresser {
        private readonly SdfFrame[] m_pool = new SdfFrame[4];
        private int m_next;
        private SdfProgram? m_program;
        private DynamicTransform[]? m_transforms;
        private SdfMovedTransforms? m_moved;
        private IReadOnlyList<SdfMeshDraw> m_draws = [];
        private bool m_primed;

        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            m_program = program; m_transforms = transforms; m_moved = moved; m_draws = meshDraws;
            if (!m_primed) { return Make(); }

            var frame = m_pool[m_next];

            m_next = ((m_next + 1) % m_pool.Length);

            return frame;
        }
        public void Prime() {
            for (var i = 0; (i < m_pool.Length); i++) { m_pool[i] = Make(); }
            m_primed = true;
        }

        private SdfFrame Make() => new(Program: m_program!, ProgramChanged: false, Time: 0f, Views: [new SdfViewSnapshot(
            Camera: CameraSnapshot.LookAt(
                fieldOfViewRadians: 1f,
                position: new Vector3(x: 0f, y: 0f, z: -5f),
                target: Vector3.Zero,
                viewportHeight: Extent,
                viewportWidth: Extent
            ),
            Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
        )]) {
            DynamicTransforms = m_transforms!,
            MovedTransforms = m_moved,
            MeshDraws = m_draws,
            Lights = SdfLights.Default(),
            Sky = new SdfSky(),
        };
    }
    private sealed class SphereEmitter : ISdfSceneEmitter {
        public int RevisionComponentCount => 1;
        public int DynamicSlotCount => 1;

        public void WriteRevision(Span<int> destination) => destination[0] = 0;
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 1f);
            _ = builder.Sphere(material: material, radius: 1f);
            _ = builder.EndInstance();
        }
        public void PackDynamicTransforms(DynamicTransform[] slots, in SdfEmitContext context, SdfMovedTransforms moved) {
            if (!moved.Everything) { return; }
            slots[context.SlotBase] = new DynamicTransform(Orientation: Quaternion.Identity, Position: Vector3.Zero);
        }
    }
    private sealed class Rig : IDisposable {
        private readonly PooledDresser m_dresser = new();
        private readonly SdfCompositionFrameSource m_source;

        public Rig() {
            var emitter = new SphereEmitter();
            var gpu = new FakeGpuDevice();

            m_source = new SdfCompositionFrameSource(dresser: m_dresser, emitters: [emitter]);
            Residency = new SdfWorldResidency(
                frameSource: m_source,
                height: Extent,
                kernels: SdfTestPipelines.Kernels(),
                name: "world",
                pipelines: SdfTestPipelines.Cache(),
                width: Extent
            );
            Context = new FrameContext(
                AccumulatorTicks: 0UL,
                DeltaTicks: 0UL,
                ElapsedTicks: 0UL,
                FrameDeltaTicks: 0UL,
                Host: new HostContext(capabilities: new Dictionary<Type, object> {
                    [typeof(IGpuDeviceContext)] = gpu,
                }),
                StepTicks: 0UL,
                TargetHeight: Extent,
                TargetWidth: Extent
            );
        }

        public SdfWorldResidency Residency { get; }
        public readonly FrameContext Context;

        public SdfFrame? Capture() => m_source.CaptureFrame(deltaSeconds: 0f, height: Extent, interpolationAlpha: 0f, width: Extent);
        // Captures until the composition's first frames have run, then hands the dresser the frames it will answer with.
        public void Warm() {
            for (var warm = 0; (warm < 8); warm++) { _ = Capture(); }
            m_dresser.Prime();
            for (var warm = 0; (warm < 8); warm++) { _ = Capture(); }
        }
        public void Dispose() => Residency.Dispose();
    }
}
