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
/// or a device loss builds it again, and the residency is then ready on the program it held. The error stream reports
/// each refusal once, and a device loss in an unselected views build escapes an ordinary refusal after tables exist.
/// </summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class SdfWorldResidencyViewsRefusalLawTests {
    private const uint Extent = 32;
    private const int Frames = 16;

    [Fact]
    public void AViewsRefusalIsPrintedOnceEvenWhileFramesKeepRenderingTheHeldProgram() {
        var original = Console.Error;
        using var captured = new StringWriter();

        Console.SetError(newError: captured);

        try {
            using var rig = new Rig();

            rig.ProduceUntilReady();
            rig.Source.Frame = Rig.Frame(program: Trapezoid()) with { ProgramChanged = true };

            for (var frame = 0; (frame < Frames); frame++) {
                Assert.True(condition: rig.Residency.Produce(context: in rig.Context));
                _ = rig.Residency.WaitPipelineBuilds(cancellationToken: CancellationToken.None);
            }

            Assert.False(condition: rig.Residency.IsReady);
            var lines = captured.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');

            foreach (var name in new[] { "sdf-world-views", "sdf-world-views-core" }) {
                var line = Assert.Single(collection: lines, predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: $"[{name}]"));

                Assert.Contains(actualString: line, expectedSubstring: "retried on a kernel reload or a device loss");
                Assert.Contains(actualString: line, expectedSubstring: $"injected failure creating {name}");
            }
        } finally {
            Console.SetError(newError: original);
        }
    }
    [Fact]
    public void ADeviceLossInAnUnselectedViewsBuildEscapesAnExistingRefusal() {
        Assert.SkipWhen(
            condition: (GpuPassPipelineCache.BuildConcurrency < 2),
            reason: "The tables can finish beside a held creation only with at least two build turns."
        );
        using var entered = new ManualResetEventSlim(initialState: false);
        using var finish = new ManualResetEventSlim(initialState: false);
        var lost = new DeviceLostException(message: "injected device loss creating sdf-world-views-core");
        var rig = new Rig(beforeComputePipeline: description => {
            if (description.Name == "sdf-world-views-core") {
                entered.Set();
                finish.Wait();
                throw lost;
            }
        });

        try {
            var context = rig.Context;

            // The full program has no rendered frame: its kernel is refused while core stays in the driver.
            rig.Source.Frame = Rig.Frame(program: Trapezoid());
            TestLiveness.Until(
                reason: () => rig.Residency.NotReadyReason,
                step: () => {
                    _ = rig.Residency.Produce(context: in context);

                    return ((rig.Residency.Tables is not null) &&
                        (rig.Residency.NotReadyReason?.Contains(comparisonType: StringComparison.Ordinal, value: "was refused") == true));
                }
            );
            Assert.True(condition: entered.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(condition: rig.Residency.IsReady);

            // Core cannot render this program, but its completed device loss must escape the ordinary full refusal.
            finish.Set();
            _ = rig.Residency.WaitPipelineBuilds(cancellationToken: CancellationToken.None);
            Assert.Same(expected: lost, actual: Assert.Throws<DeviceLostException>(testCode: () => rig.Residency.Produce(context: in context)));
        } finally {
            finish.Set();
            rig.Dispose();
        }
    }
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
    public void ARefusedViewsKernelIsTheResidencysRefusalAndAReadyOneIsNot() {
        using var rig = new Rig();

        rig.ProduceUntilReady();
        Assert.Null(@object: rig.Residency.Refusal);

        // A trapezoid selects the full ISA, also refused: the instance's refusal names the kernel and its failure, the one
        // channel SdfWorldPasses.RefusalOf reports, so a host steps on rather than waiting on a kernel nothing retries.
        rig.Source.Frame = Rig.Frame(program: Trapezoid(), cameraZ: -8f) with { ProgramChanged = true };

        for (var frame = 0; (frame < Frames); frame++) {
            _ = rig.Residency.Produce(context: in rig.Context);
            _ = rig.Residency.WaitPipelineBuilds(cancellationToken: CancellationToken.None);
        }

        Assert.False(condition: rig.Residency.IsReady);
        Assert.Contains(
            actualString: rig.Residency.Refusal,
            expectedSubstring: "the views kernel its program selects, 'sdf-world-views', was refused and is built again on a kernel reload or a device loss: injected failure creating sdf-world-views"
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
        using var scratch = new TemporaryDirectory(prefix: "puck-test-");
        var root = scratch.RootPath;

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
        Assert.Null(@object: rig.Residency.Refusal);
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

        public Rig(Action<GpuComputePipelineDescription>? beforeComputePipeline = null) {
            var gpu = new FakeGpuDevice() {
                BeforeComputePipeline = description => {
                    lock (m_gate) {
                        m_attempts[description.Name] = (Attempts(name: description.Name) + 1);
                    }

                    beforeComputePipeline?.Invoke(obj: description);

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
