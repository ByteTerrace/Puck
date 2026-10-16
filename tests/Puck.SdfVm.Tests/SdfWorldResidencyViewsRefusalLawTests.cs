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
/// Laws for an <see cref="SdfWorldResidency"/> whose views kernel's creation fails, over a <see cref="FakeGpuDevice"/>
/// whose driver fails the views kernel's creation until the law lets it build. A failed views kernel is a named refusal,
/// never a throw out of the frame: the residency is not ready, and its reason and refusal name the kernel and its
/// failure. A refused kernel is never built again by a frame, however many arrive; a kernel reload or a device loss
/// builds it again, and the residency is then ready on the program it held. The error stream reports the refusal once,
/// and a device loss in the views build escapes to the host's recovery.
/// </summary>
public sealed class SdfWorldResidencyViewsRefusalLawTests {
    private const uint Extent = 32;
    private const int Frames = 16;

    [Fact]
    public void AViewsRefusalIsPrintedOnceWhileFramesKeepArriving() {
        var original = Console.Error;
        using var captured = new StringWriter();

        Console.SetError(newError: captured);

        try {
            using var rig = new Rig();

            rig.ProduceFrames();
            Assert.False(condition: rig.Residency.IsReady);
            var lines = captured.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');
            var line = Assert.Single(collection: lines, predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[sdf-world-views]"));

            Assert.Contains(actualString: line, expectedSubstring: "retried on a kernel reload or a device loss");
            Assert.Contains(actualString: line, expectedSubstring: "injected failure creating sdf-world-views");
        } finally {
            Console.SetError(newError: original);
        }
    }
    [Fact]
    public void AFailedViewsKernelIsANamedRefusal() {
        using var rig = new Rig();

        rig.ProduceFrames();
        Assert.False(condition: rig.Residency.IsReady);
        Assert.Contains(
            actualString: rig.Residency.NotReadyReason,
            expectedSubstring: "the views kernel, 'sdf-world-views', was refused and is built again on a kernel reload or a device loss: injected failure creating sdf-world-views"
        );
        Assert.Contains(
            actualString: rig.Residency.Refusal,
            expectedSubstring: "the views kernel, 'sdf-world-views', was refused and is built again on a kernel reload or a device loss: injected failure creating sdf-world-views"
        );
    }
    [Fact]
    public void ARefusedViewsKernelIsNeverBuiltAgainByAFrame() {
        using var rig = new Rig();

        rig.ProduceFrames();
        rig.Fails = false;
        rig.ProduceFrames();
        Assert.False(condition: rig.Residency.IsReady);
        Assert.Equal(actual: rig.Attempts(name: "sdf-world-views"), expected: 1);
    }
    [Fact]
    public void ADeviceLossInTheViewsBuildEscapesToRecovery() {
        var lost = new DeviceLostException(message: "injected device loss creating sdf-world-views");
        var rig = new Rig(beforeComputePipeline: description => {
            if (description.Name == "sdf-world-views") {
                throw lost;
            }
        });

        try {
            var context = rig.Context;
            DeviceLostException? escaped = null;

            TestLiveness.Until(
                reason: () => rig.Residency.NotReadyReason,
                step: () => {
                    try {
                        _ = rig.Residency.Produce(context: in context);
                    } catch (DeviceLostException exception) {
                        escaped = exception;
                    }

                    return (escaped is not null);
                },
                wait: rig.Residency.WaitPipelineBuilds
            );
            Assert.Same(expected: lost, actual: escaped);
        } finally {
            rig.Dispose();
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AReloadOrADeviceLossBuildsARefusedViewsKernelAgainAndTheResidencyIsReadyOnItsProgram(bool deviceLoss) {
        using var scratch = new TemporaryDirectory(prefix: "puck-test-");
        var root = scratch.RootPath;

        using var rig = new Rig();

        rig.ProduceFrames();
        Assert.False(condition: rig.Residency.IsReady);

        // The driver now builds every kernel, which no frame retries.
        rig.Fails = false;
        rig.ProduceFrames();
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
            expected: rig.Source.First.Program
        );
        Assert.Equal(
            actual: rig.Residency.CopyLiveProgramWords(),
            expected: rig.Source.First.Program.Words.ToArray()
        );
        Assert.Equal(
            actual: rig.Attempts(name: "sdf-world-views"),
            expected: 2
        );

        if (!deviceLoss) {
            Assert.Equal(
                actual: (rig.Residency.ShaderReloadStatus.State, rig.Residency.ShaderReloadStatus.ChangedPipelines),
                expected: ("applied", 1)
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
    // A residency over a fake driver that fails the views kernel's creation while Fails holds, counting every creation by
    // name.
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

                    if (m_fails && (description.Name == "sdf-world-views")) {
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
        // Produces a run of frames, waiting out the residency's builds between them.
        public void ProduceFrames() {
            for (var frame = 0; (frame < Frames); frame++) {
                _ = Residency.Produce(context: in Context);
                _ = Residency.WaitPipelineBuilds(cancellationToken: CancellationToken.None);
            }
        }
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
