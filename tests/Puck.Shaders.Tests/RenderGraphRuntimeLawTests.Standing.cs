using Puck.Abstractions.Counting;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// An output standing for another instance's image: a root whose pass draws nothing publishes its producer's image in its
// output's place. Whatever releases or replaces that producer's images, no reader binds one, the display is never handed
// one and the fake device records no command naming one; the root keeps its refresh while its producer moves on, and
// renders over the stand-in in the frame its producer is released.
public sealed partial class RenderGraphRuntimeLawTests {
    // A runtime over the fake device driven one frame at a time, each frame given its roots, footprints and names.
    private sealed class StandingScene : IDisposable {
        private long m_index;

        // The instances whose package pass draws nothing while StandsIn holds, once their recorders exist.
        private readonly string[] m_drawsNothing;

        public StandingScene(string[] drawsNothing, RenderGraphInstanceSet set, params RenderGraphRuntimeGraph[] graphs) {
            m_drawsNothing = drawsNothing;
            Set = set;
            Runtime = RenderGraphRuntimeLawTests.Runtime(Gpu, Recorders, set, "main", graphs);
        }

        public FakePipelineGpu Gpu { get; } = new();
        public Recorders Recorders { get; } = new(Camera, Over);

        public RenderGraphRuntime Runtime { get; }

        // Whether those passes draw nothing; a pass told it may not stand in draws, as a shipped package does.
        public bool StandsIn { get; set; } = true;

        public RenderGraphInstanceSet Set { get; }

        public void Dispose() => Runtime.Dispose();
        public ShaderPipelineRenderNode Node(string instance) => Runtime.NodeOf(instance: instance)!;
        // Produces one frame, returning what the display is handed, which must name no released image.
        public Surface Produce(IReadOnlyList<RenderGraphRoot> roots, IReadOnlyList<RenderGraphFootprint> footprints, IReadOnlyList<string>? named = null) {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: footprints,
                Index: m_index++,
                Named: named,
                Roots: roots
            );

            foreach (var name in m_drawsNothing) {
                if (Recorders.ByInstance.TryGetValue(key: name, value: out var counter)) {
                    counter.DrawsWhenRefused = true;
                    counter.Outcome = (StandsIn
                        ? RenderGraphPackageOutcome.DrewNothing
                        : RenderGraphPackageOutcome.Drew);
                }
            }

            var shown = Runtime.ProduceFrame(
                context: default,
                frame: in frame
            );

            // Checked without allocating, since a steady frame's allocations are counted around this.
            if (Gpu.IsReleased(handle: shown.ImageHandle)) {
                Assert.Fail(message: $"Frame {frame.Index} handed the display a released image.");
            }
            if (Gpu.UsesAfterRelease.Count != 0) {
                Assert.Fail(message: string.Join(separator: "; ", values: Gpu.UsesAfterRelease));
            }

            return shown;
        }
        // Produces frames until the root renders and publishes the camera's latest image in its output's place.
        public Surface ProduceUntilMainStandsForCamera(IReadOnlyList<RenderGraphRoot> roots, IReadOnlyList<RenderGraphFootprint> footprints, IReadOnlyList<string>? named = null) {
            var shown = default(Surface);

            TestLiveness.Until(
                reason: () => "The root never published the camera's image in its output's place.",
                step: () => {
                    var rendered = Node(instance: "main").FrameCounter;

                    shown = Produce(
                        footprints: footprints,
                        named: named,
                        roots: roots
                    );

                    return (
                        (Node(instance: "main").FrameCounter != rendered) &&
                        (shown.ImageHandle != 0) &&
                        (shown.ImageHandle == Recorders.Of(instance: "camera").OutputImage)
                    );
                }
            );

            return shown;
        }
    }

    private static readonly RenderGraphRoot[] MainRoot = [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)];

    private static RenderGraphInstance Refreshed(string name, int divisor, params RenderGraphRead[] reads) => new(
        Name: name,
        Output: ShaderPipelineResourceKind.Image,
        Passes: 1,
        Reads: reads,
        Refresh: RenderGraphRefresh.Every(divisor: divisor)
    );

    /// <summary>A reader of a slow standing view resolves the camera at the frame it reads, even after the camera's
    /// two recorded outputs are newer than the view's last render. A previous-frame reader takes the camera's previous
    /// output; a same-frame reader takes its current one. The view keeps its cadence.</summary>
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AReaderOfASlowStandingViewFollowsItsCamera(bool previousFrame) {
        using var scene = new StandingScene(
            ["mid"],
            Set(
                Instance(name: "camera"),
                Refreshed("mid", 64, new RenderGraphRead(Producer: "camera")),
                Instance("main", reads: [
                    new RenderGraphRead(PreviousFrame: previousFrame, Producer: "mid"),
                    new RenderGraphRead(Producer: "camera"),
                ])
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(OverGraph(reader: false), ("world", "mid"))
        );
        RenderGraphFootprint[] filmed = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "mid", Width: 1.0),
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0),
            new RenderGraphFootprint(Consumer: "mid", Height: 1.0, Producer: "camera", Width: 1.0),
        ];

        TestLiveness.Until(
            reason: () => "The view never stood for the camera's output.",
            step: () => {
                _ = scene.Produce(footprints: filmed, roots: MainRoot);

                return (
                    scene.Runtime.IsSettled &&
                    (scene.Node(instance: "mid").PublishedBinding == "world") &&
                    scene.Runtime.TryLatestImage(image: out var image, instance: "mid") &&
                    (image.ImageHandle == scene.Recorders.Of(instance: "camera").OutputImage)
                );
            }
        );
        var rendered = scene.Node(instance: "mid").FrameCounter;

        for (var frame = 0; (frame < 8); frame++) {
            var priorCamera = scene.Recorders.Of(instance: "camera").OutputImage;

            _ = scene.Produce(footprints: filmed, roots: MainRoot);
            Assert.Equal(
                actual: scene.Recorders.Of(instance: "main").InputImage,
                expected: (previousFrame ? priorCamera : scene.Recorders.Of(instance: "camera").OutputImage)
            );
        }

        Assert.Equal(actual: scene.Node(instance: "mid").FrameCounter, expected: rendered);
    }
    /// <summary>A capture waiting on a slow root that stands for a view that stands for a camera keeps both producers
    /// when their footprints disappear. Its image stays live while the root waits for its next refresh.</summary>
    [Fact]
    public void APendingCaptureKeepsEveryProducerInAStandingChain() {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Refreshed("mid", 64, new RenderGraphRead(Producer: "camera")),
                Refreshed("main", 64, new RenderGraphRead(Producer: "mid"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(OverGraph(reader: false), ("world", "mid"))
        );
        RenderGraphFootprint[] filmed = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "mid", Width: 1.0),
            new RenderGraphFootprint(Consumer: "mid", Height: 1.0, Producer: "camera", Width: 1.0),
        ];

        TestLiveness.Until(
            reason: () => "The root never stood for the camera through the view.",
            step: () => {
                if (scene.Recorders.ByInstance.TryGetValue(key: "mid", value: out var counter)) {
                    counter.Outcome = RenderGraphPackageOutcome.DrewNothing;
                }

                _ = scene.Produce(footprints: filmed, named: [], roots: MainRoot);

                return (
                    scene.Runtime.IsSettled &&
                    (scene.Node(instance: "main").PublishedBinding == "world") &&
                    (scene.Node(instance: "mid").PublishedBinding == "world") &&
                    scene.Runtime.TryLatestImage(image: out var image, instance: "main") &&
                    (image.ImageHandle == scene.Recorders.Of(instance: "camera").OutputImage)
                );
            }
        );
        var camera = scene.Recorders.Of(instance: "camera").OutputImage;
        var rendered = scene.Node(instance: "main").FrameCounter;
        var request = CaptureRequest();

        scene.Runtime.RequestCapture(request: request);
        var shown = scene.Produce(footprints: [], named: [], roots: MainRoot);

        Assert.False(condition: scene.Gpu.IsReleased(handle: camera));
        Assert.Equal(actual: shown.ImageHandle, expected: camera);
        Assert.Equal(actual: scene.Node(instance: "main").FrameCounter, expected: rendered);
        Assert.Equal(actual: scene.Runtime.PendingCapturePath, expected: request.Path);
    }
    /// <summary>A root refreshed every other frame draws nothing over the camera, publishing the camera's image. On a frame
    /// it waits, the camera loses its footprint and nothing names it, so its graph is released: that same frame the root
    /// renders again over the camera's stand-in, and the display is never handed the released image.</summary>
    [Fact]
    public void ARootStandingForAReleasedCameraRendersOverItsStandInThatFrame() {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Refreshed("main", 2, new RenderGraphRead(Producer: "camera"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera"))
        );
        RenderGraphFootprint[] filmed = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)];


        var aliased = scene.ProduceUntilMainStandsForCamera(
            footprints: filmed,
            named: [],
            roots: MainRoot
        );
        var rendered = scene.Node(instance: "main").FrameCounter;
        var released = scene.Produce(
            footprints: [],
            named: [],
            roots: MainRoot
        );

        Assert.Equal(
            actual: (Status: scene.Runtime.Latest!.Instances[scene.Set.IndexOf(name: "camera")].Status, Owned: scene.Node(instance: "camera").OwnedBytes),
            expected: (Status: RenderGraphInstanceStatus.Unnamed, Owned: 0UL)
        );
        Assert.True(condition: scene.Gpu.IsReleased(handle: aliased.ImageHandle));
        Assert.Equal(
            actual: scene.Node(instance: "main").FrameCounter,
            expected: (rendered + 1UL)
        );
        Assert.NotEqual(
            actual: released.ImageHandle,
            expected: 0
        );

        for (var frame = 0; (frame < 8); frame++) {
            _ = scene.Produce(
                footprints: [],
                named: [],
                roots: MainRoot
            );
        }
    }
    /// <summary>A root refreshed once in 64 frames draws nothing over the camera while a probe root keeps the camera
    /// rendering every frame. The camera then replaces its graph, which retires every image but its two latest. The root
    /// keeps its refresh, rendering nothing more, while the display is handed the camera's latest image every frame and
    /// never a retired one; a steady frame with the root standing for the camera allocates nothing.</summary>
    [Fact]
    public void ARootStandingForACameraFollowsItAcrossAGraphReplacementWithoutRenderingAgain() {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Instance(name: "probe", reads: new RenderGraphRead(Producer: "camera")),
                Refreshed("main", 64, new RenderGraphRead(Producer: "camera"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(false, "near"), ("near", "camera")),
            Graph(OverGraph(reader: false), ("world", "camera"))
        );
        RenderGraphRoot[] roots = [
            .. MainRoot,
            new RenderGraphRoot(Height: 1.0, Instance: "probe", Width: 1.0),
        ];
        RenderGraphFootprint[] filmed = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0),
            new RenderGraphFootprint(Consumer: "probe", Height: 1.0, Producer: "camera", Width: 1.0),
        ];

        _ = scene.ProduceUntilMainStandsForCamera(
            footprints: filmed,
            roots: roots
        );

        var rendered = scene.Node(instance: "main").FrameCounter;

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                for (var frame = 0; (frame < 8); frame++) {
                    _ = scene.Produce(footprints: filmed, roots: roots);
                }
            }),
            expected: 0L
        );

        var installs = scene.Recorders.Of(instance: "camera").Created;

        scene.Node(instance: "camera").Swap(pipeline: CameraGraph());
        TestLiveness.Until(
            reason: () => "The camera's replacement graph never installed.",
            step: () => {
                _ = scene.Produce(footprints: filmed, roots: roots);

                return (scene.Recorders.Of(instance: "camera").Created > installs);
            }
        );

        // Long enough for the replaced graph's last two published images to retire too.
        for (var frame = 0; (frame < 8); frame++) {
            _ = scene.Produce(footprints: filmed, roots: roots);
        }

        Assert.Equal(
            actual: scene.Produce(footprints: filmed, roots: roots).ImageHandle,
            expected: scene.Recorders.Of(instance: "camera").OutputImage
        );

        Assert.Equal(
            actual: scene.Node(instance: "main").FrameCounter,
            expected: rendered
        );
    }
    /// <summary>A view draws nothing over the camera every frame, and the root reads the view's previous frame. The camera
    /// loses its footprint and is released: the view's previous output stood for the camera's image, so the root binds a
    /// stand-in in its place and never a descriptor naming the released image.</summary>
    [Fact]
    public void APreviousOutputStandingForAReleasedCameraBindsAStandIn() {
        using var scene = new StandingScene(
            ["mid"],
            Set(
                Instance(name: "camera"),
                Instance(name: "mid", reads: new RenderGraphRead(Producer: "camera")),
                Instance(name: "main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "mid"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(ScreensGraph(false, "near"), ("near", "mid"))
        );
        var shown = new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "mid", Width: 1.0);
        RenderGraphFootprint[] filmed = [
            shown,
            new RenderGraphFootprint(Consumer: "mid", Height: 1.0, Producer: "camera", Width: 1.0),
        ];

        TestLiveness.Until(
            reason: () => "The view never published the camera's image in its output's place.",
            step: () => {
                _ = scene.Produce(footprints: filmed, named: [], roots: MainRoot);

                return (
                    scene.Runtime.IsSettled &&
                    (scene.Recorders.Of(instance: "camera").OutputImage != 0) &&
                    scene.Runtime.TryLatestImage(image: out var image, instance: "mid") &&
                    (image.ImageHandle == scene.Recorders.Of(instance: "camera").OutputImage)
                );
            }
        );
        _ = scene.Produce(footprints: filmed, named: [], roots: MainRoot);

        var camera = scene.Recorders.Of(instance: "camera").OutputImage;

        scene.Gpu.Recording = true;

        for (var frame = 0; (frame < 4); frame++) {
            _ = scene.Produce(footprints: [shown], named: [], roots: MainRoot);
        }

        Assert.True(condition: scene.Gpu.IsReleased(handle: camera));
        Assert.DoesNotContain(
            collection: scene.Gpu.DescriptorWrites,
            filter: write => (write.Handle == (camera + 1))
        );
    }
}
