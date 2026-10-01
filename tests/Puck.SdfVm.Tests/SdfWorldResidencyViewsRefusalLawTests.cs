using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for an <see cref="SdfWorldResidency"/> whose views kernel's creation fails while its tables exist, over a
/// <see cref="FakeGpuDevice"/> whose driver fails the full and core views kernels' creations until the law lets them
/// build. A failed views kernel is a named refusal, never a throw out of the frame: the residency holds the frame it last
/// packed, its reason names the refused kernel and its failure, and a program whose own variant was refused renders with
/// a fuller variant that is built. A refused kernel is never built again by a frame, however many arrive; a kernel reload
/// or a device loss builds it again, and the residency is then ready on the program it held.
/// </summary>
public sealed class SdfWorldResidencyViewsRefusalLawTests {
    private const uint Extent = 32;
    private const int Frames = 16;

    [Fact]
    public void AFailedViewsKernelIsANamedRefusalAndTheResidencyHoldsItsFrame() {
        using var rig = new Rig();

        // The sphere selects the core variant, which was refused: the folds variant, built, renders it.
        rig.ProduceUntilReady();
        Assert.Same(
            actual: rig.Residency.Frame,
            expected: rig.Source.First
        );

        // A trapezoid selects the full ISA, also refused: the residency holds the sphere's frame and names the refusal.
        rig.Source.Frame = Rig.Frame(program: Trapezoid(), cameraZ: -8f) with { ProgramChanged = true };

        for (var frame = 0; (frame < Frames); frame++) {
            Assert.True(condition: rig.Residency.Produce(context: in rig.Context));
            _ = rig.Residency.WaitPipelineBuilds(cancellationToken: CancellationToken.None);
            Assert.False(condition: rig.Residency.IsReady);
            Assert.Same(
                actual: rig.Residency.Frame,
                expected: rig.Source.First
            );
            Assert.Equal(
                actual: rig.Residency.CopyLiveProgramWords(),
                expected: rig.Source.First.Program.Words.ToArray()
            );
        }

        Assert.Contains(
            actualString: rig.Residency.NotReadyReason,
            expectedSubstring: "holds its frame: the views kernel its program selects, 'sdf-world-views', was refused and is built again on a kernel reload or a device loss: injected failure creating sdf-world-views"
        );
    }
    [Fact]
    public void ARefusedViewsKernelIsNeverBuiltAgainByAFrame() {
        using var rig = new Rig();

        rig.ProduceUntilReady();
        rig.Source.Frame = Rig.Frame(program: Trapezoid()) with { ProgramChanged = true };

        for (var frame = 0; (frame < Frames); frame++) {
            _ = rig.Residency.Produce(context: in rig.Context);
            _ = rig.Residency.WaitPipelineBuilds(cancellationToken: CancellationToken.None);
        }

        Assert.False(condition: rig.Residency.IsReady);
        Assert.Equal(
            actual: (rig.Attempts(name: "sdf-world-views"), rig.Attempts(name: "sdf-world-views-core")),
            expected: (1, 1)
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AReloadOrADeviceLossBuildsARefusedViewsKernelAgainAndTheResidencyIsReadyOnItsProgram(bool deviceLoss) {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-test-").FullName;

        try {
            using var rig = new Rig();
            var trapezoid = Rig.Frame(program: Trapezoid(), cameraZ: -8f);

            rig.ProduceUntilReady();
            rig.Source.Frame = trapezoid with { ProgramChanged = true };

            for (var frame = 0; (frame < Frames); frame++) {
                _ = rig.Residency.Produce(context: in rig.Context);
                _ = rig.Residency.WaitPipelineBuilds(cancellationToken: CancellationToken.None);
            }

            Assert.False(condition: rig.Residency.IsReady);

            // The driver now builds every kernel, which no frame retries.
            rig.Fails = false;
            rig.Source.Frame = trapezoid;

            for (var frame = 0; (frame < Frames); frame++) {
                _ = rig.Residency.Produce(context: in rig.Context);
                _ = rig.Residency.WaitPipelineBuilds(cancellationToken: CancellationToken.None);
            }

            Assert.False(condition: rig.Residency.IsReady);

            if (deviceLoss) {
                rig.Residency.OnDeviceLost();
            } else {
                // A tree that carries the refused kernel unchanged: the reload builds it again from the same bytecode.
                var passes = Directory.CreateDirectory(path: SdfKernelSet.PassesDirectory(tree: root)).FullName;

                File.WriteAllBytes(
                    bytes: SdfTestPipelines.Kernels()[SdfKernel.Views].ToArray(),
                    path: Path.Combine(
                        path1: passes,
                        path2: $"{SdfKernelSet.StemOf(kernel: SdfKernel.Views)}.comp.spv"
                    )
                );
                Assert.True(condition: rig.Residency.RequestShaderReload(
                    compiler: new ShaderCompiler(cacheDirectory: Path.Combine(path1: Path.GetTempPath(), path2: "puck-test-shader-cache")),
                    tree: root
                ));
            }

            rig.ProduceUntilReady();
            Assert.Same(
                actual: rig.Residency.Frame!.Program,
                expected: trapezoid.Program
            );
            Assert.Equal(
                actual: rig.Residency.CopyLiveProgramWords(),
                expected: trapezoid.Program.Words.ToArray()
            );
            Assert.Equal(
                actual: rig.Attempts(name: "sdf-world-views"),
                expected: 2
            );

            if (!deviceLoss) {
                Assert.Equal(
                    actual: (rig.Residency.ShaderReloadStatus.State, rig.Residency.ShaderReloadStatus.ChangedPipelines),
                    expected: ("applied", 2)
                );
            }
        } finally {
            Directory.Delete(
                path: root,
                recursive: true
            );
        }
    }

    private static SdfProgram Sphere() {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        return builder.Build();
    }
    private static SdfProgram Trapezoid() {
        var builder = new SdfProgramBuilder();

        builder.Trapezoid(
            bottomHalfWidth: 1f,
            halfHeight: 0.5f,
            lift: SdfLift.Revolve,
            liftAmount: 0f,
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            topHalfWidth: 0.5f
        );

        return builder.Build();
    }

    // A residency over a fake driver that fails the full and core views kernels' creations while Fails holds, counting
    // every creation by name.
    private sealed class Rig : IDisposable {
        private readonly Dictionary<string, int> m_attempts = [];
        private readonly Lock m_gate = new();
        private volatile bool m_fails = true;

        public Rig() {
            var gpu = new FakeGpuDevice() {
                BeforeComputePipeline = description => {
                    lock (m_gate) {
                        m_attempts[description.Name] = (Attempts(name: description.Name) + 1);
                    }

                    if (m_fails && (description.Name is "sdf-world-views" or "sdf-world-views-core")) {
                        throw new InvalidOperationException(message: $"injected failure creating {description.Name}");
                    }
                },
            };

            Source = new SwitchingFrameSource(frame: Frame(program: Sphere()));
            Residency = new SdfWorldResidency(
                brickPoolVoxelCapacity: 0,
                frameSource: Source,
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

        public FrameContext Context;

        public bool Fails {
            get => m_fails;
            set => m_fails = value;
        }
        public SdfWorldResidency Residency { get; }
        public SwitchingFrameSource Source { get; }

        public static SdfFrame Frame(SdfProgram program, float cameraZ = -5f) => new(
            Program: program,
            ProgramChanged: false,
            Time: 0f,
            Views: [new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(
                    fieldOfViewRadians: 1f,
                    position: new Vector3(x: 0f, y: 0f, z: cameraZ),
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
        public int Attempts(string name) {
            lock (m_gate) {
                return m_attempts.GetValueOrDefault(key: name);
            }
        }
        public void Dispose() => Residency.Dispose();
        // Produces frames until the residency is ready, waiting out its builds between them.
        public void ProduceUntilReady() {
            var context = Context;

            TestLiveness.Until(
                reason: () => Residency.NotReadyReason,
                step: () => {
                    _ = Residency.Produce(context: in context);

                    return Residency.IsReady;
                },
                wait: Residency.WaitPipelineBuilds
            );
        }
    }
    // Films whatever frame the law sets, remembering the first.
    private sealed class SwitchingFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame First { get; } = frame;
        public SdfFrame Frame { get; set; } = frame;

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => Frame;
    }
}
