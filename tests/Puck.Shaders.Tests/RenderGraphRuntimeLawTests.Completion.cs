using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// The runtime reports whether the root's image shows the frame it was asked to compose
/// (<see cref="RenderGraphRuntime.Completion"/>), which the offscreen host steps on. A cold build is not yet renderable
/// until the root renders, and a producer that keeps its older output while it rebuilds leaves the root not yet renderable
/// though the root renders over that output: a host that took the surface alone would show an older frame's image for a
/// newer one.
/// </summary>
public sealed partial class RenderGraphRuntimeLawTests {
    // A camera and the root reading it within the frame, as a world's root reads its view.
    private static (RenderGraphRuntime Runtime, Frames Frames) CompletionScene(FakePipelineGpu gpu) {
        var runtime = Runtime(
            gpu,
            new Recorders(Camera),
            Set(
                Instance(name: "camera"),
                Instance(
                    name: "main",
                    reads: new RenderGraphRead(Producer: "camera")
                )
            ),
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(false, "screen"), ("screen", "camera"))
        );

        return (runtime, new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        ));
    }

    [Fact]
    public void AColdBuildIsNotYetRenderableUntilTheRootRendersTheFrame() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames) = CompletionScene(gpu: gpu);

        using (runtime) {
            // Declared inside the runtime's scope, the opener releases a build held in the driver before the runtime's
            // disposal waits for it.
            using var opener = new PipelineGateOpener();

            gpu.PipelineGate = opener.Gate;

            for (var frame = 0; (frame < 4); frame++) {
                var surface = frames.Next();

                Assert.True(condition: surface.IsEmpty);
                Assert.Equal(
                    actual: runtime.Completion,
                    expected: FrameCompletion.NotYetRenderable
                );
                Assert.Contains(
                    expectedSubstring: "has no installed graph yet: its pipelines are building",
                    actualString: runtime.CompletionReason
                );
            }

            gpu.PipelineGate = null;
            opener.Gate.Set();

            var rendered = default(Surface);

            TestLiveness.Until(
                reason: () => (runtime.CompletionReason ?? "rendered"),
                step: () => {
                    rendered = frames.Next();

                    return (runtime.Completion == FrameCompletion.Rendered);
                }
            );
            Assert.False(condition: rendered.IsEmpty);
            Assert.Null(@object: runtime.CompletionReason);
        }
    }
    [Fact]
    public void ARebuildNeverYieldsAnOlderImageForANewerFrame() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, producers) = WorldScene(gpu: gpu);

        using (runtime) {
            var world = producers.Only;

            frames.Settle();
            _ = frames.Next();
            Assert.Equal(
                actual: runtime.Completion,
                expected: FrameCompletion.Rendered
            );

            // The world rebuilds and produces nothing: the root renders over the world's older output.
            world.Holding = true;

            var produced = world.Produced;

            for (var frame = 0; (frame < 3); frame++) {
                var surface = frames.Next();

                // The red leg: the root rendered and hands out an image, one showing the world of an earlier frame.
                Assert.False(condition: surface.IsEmpty);
                Assert.Equal(
                    actual: world.Produced,
                    expected: produced
                );
                Assert.Equal(
                    actual: runtime.Completion,
                    expected: FrameCompletion.NotYetRenderable
                );
                Assert.Equal(
                    actual: runtime.CompletionReason,
                    expected: "the instance 'world' produced no output this frame"
                );
            }

            world.Holding = false;
            _ = frames.Next();
            Assert.Equal(
                actual: (runtime.Completion, world.Produced),
                expected: (FrameCompletion.Rendered, (produced + 1))
            );
        }
    }

    // A package whose build never finishes, as an SDF view's does while its residency's tables are refused, and which
    // states a refusal for its instances when told to.
    private sealed class RefusingPackage : IRenderGraphPackageFactory {
        public RefusingPackage() => Registry.Register(
            factory: this,
            package: Camera
        );

        public string? Refusal { get; set; }
        public RenderGraphPackageRecorders Registry { get; } = new(regionCopy: new GpuRegionCopyPass(pipelines: new GpuPassPipelineCache(), kernel: new byte[] { UploadModelGpu.RegionCopyBytecode }));

        public async ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
            await Task.Delay(
                cancellationToken: cancellationToken,
                delay: Timeout.InfiniteTimeSpan
            );

            return null;
        }
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) =>
            throw new InvalidOperationException(message: "The refusing package never builds.");
        public string? RefusalOf(string instance) => Refusal;
    }

    /// <summary>A package that refuses its instance (an SDF residency whose tables' build was refused) makes the frame
    /// <see cref="FrameCompletion.Refused"/>, naming the refusal, so the offscreen host steps on. The red leg: the same
    /// package stating no refusal leaves the frame not yet renderable, a wait the host would hold forever.</summary>
    [Fact]
    public void APackageRefusalIsARefusedFrameNeverAWait() {
        var gpu = new FakePipelineGpu();
        var package = new RefusingPackage();

        Assert.True(condition: RenderGraphRuntime.TryCreate(
            deviceContext: gpu,
            graphs: [Graph(pipeline: CameraGraph()), Graph(ScreensGraph(false, "screen"), ("screen", "camera"))],
            hostsOnDirectX: false,
            packages: package.Registry,
            pipelines: new GpuPassPipelineCache(),
            refusal: out var created,
            root: "main",
            runtime: out var refusing,
            set: Set(
                Instance(name: "camera"),
                Instance(
                    name: "main",
                    reads: new RenderGraphRead(Producer: "camera")
                )
            )
        ), userMessage: created?.Message);

        using (refusing) {
            var frames = new Frames(
                footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)],
                roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
                runtime: refusing
            );

            // The root installs, and renders over a stand-in for the camera, whose build never finishes: a wait.
            const string Waiting = "the instance 'camera' has no installed graph yet: its pipelines are building";

            TestLiveness.Until(
                reason: () => (refusing.CompletionReason ?? "rendered"),
                step: () => {
                    _ = frames.Next();

                    return (refusing.CompletionReason == Waiting);
                }
            );
            frames.Next(count: 3);
            Assert.Equal(
                actual: (refusing.Completion, refusing.CompletionReason),
                expected: (FrameCompletion.NotYetRenderable, Waiting)
            );

            package.Refusal = "the engine's build was refused and is retried when its inputs change: [GPU_CREATION_FAULT] the law refused it";
            _ = frames.Next();
            Assert.Equal(
                actual: (refusing.Completion, refusing.CompletionReason),
                expected: (FrameCompletion.Refused, $"the instance 'camera' cannot render: {package.Refusal}")
            );
        }
    }
}
