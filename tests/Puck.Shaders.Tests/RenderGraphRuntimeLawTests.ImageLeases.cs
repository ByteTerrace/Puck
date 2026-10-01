using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// Image lifetime under leases: every image the runtime hands a reader is leased from whichever node owns it, and an image
// its owner drops lives until the last lease on it retires. Feedback never stands for its own image, a capture of a root
// standing for a moving camera is served, a kept consumer's installed graph samples an image whose whole standing chain
// retired, and a reader's pending submission outlives the retirement that dropped the image it reads. Every frame of
// every law checks that the display is handed no released image and the fake device records no command naming one.
public sealed partial class RenderGraphRuntimeLawTests {
    /// <summary>A root reads its own previous frame through a pass that draws over it. Once the pass would draw nothing,
    /// standing for that previous image would publish an image the root renders into again a few frames later, so the pass
    /// is told it may not stand in and draws: the root keeps a completed image of its own every frame, never
    /// nothing.</summary>
    [Fact]
    public void ARootReadingItsOwnPreviousFrameDrawsRatherThanStandingForItself() {
        using var scene = new StandingScene(
            ["main"],
            Set(Instance("main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "main"))),
            Graph(OverGraph(reader: false), ("world", "main"))
        );
        RenderGraphFootprint[] shown = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "main", Width: 1.0)];

        AssertFeedbackKeepsAnImage(
            loop: ["main"],
            scene: scene,
            shown: shown
        );
    }
    /// <summary>A view reads the root's previous frame and the root reads the view in the same frame, each through a pass
    /// that draws over its input. Once both would draw nothing, the loop would close on the root's own image, so the root
    /// draws and the view stands for the root's previous image: the loop keeps a completed image every frame, never
    /// nothing.</summary>
    [Fact]
    public void TwoInstancesReadingEachOtherNeverCloseALoopOfStandingOutputs() {
        using var scene = new StandingScene(
            ["main", "view"],
            Set(
                Instance("view", reads: new RenderGraphRead(PreviousFrame: true, Producer: "main")),
                Instance("main", reads: new RenderGraphRead(Producer: "view"))
            ),
            Graph(OverGraph(reader: false), ("world", "main")),
            Graph(OverGraph(reader: false), ("world", "view"))
        );
        RenderGraphFootprint[] shown = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "main", Width: 1.0),
        ];

        AssertFeedbackKeepsAnImage(
            loop: ["main", "view"],
            scene: scene,
            shown: shown
        );
        Assert.Equal(
            actual: scene.Node(instance: "view").PublishedBinding,
            expected: "world"
        );
    }
    /// <summary>A camera and a root standing for it both render every frame, so the camera has always rendered past the
    /// image the root last published by the time the root is asked for a capture: the capture still moves to the root and
    /// is served from the frame it renders.</summary>
    [Fact]
    public void ACaptureOfARootStandingForACameraRenderingEveryFrameIsServed() {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Instance("main", reads: new RenderGraphRead(Producer: "camera"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera"))
        );
        RenderGraphFootprint[] filmed = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)];

        scene.Gpu.ReadbackSupported = true;
        _ = scene.ProduceUntilMainStandsForCamera(
            footprints: filmed,
            roots: MainRoot
        );

        var request = CaptureRequest();

        scene.Runtime.RequestCapture(request: request);

        for (var frame = 0; ((frame < 8) && !request.Completion.IsCompleted); frame++) {
            _ = scene.Produce(footprints: filmed, roots: MainRoot);
        }

        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(
            actual: Assert.Single(collection: scene.Gpu.Readbacks).Image,
            expected: scene.Recorders.Of(instance: "camera").OutputImage
        );
    }
    /// <summary>A view stands for the camera, and a kept root's installed graph reads the view. A reconfiguration retires
    /// both while the root's replacement waits in the driver: the root keeps sampling the image it bound, the camera's,
    /// under a lease, though neither the view nor the camera holds it any more; once the replacement installs, the image
    /// is disposed exactly once.</summary>
    [Fact]
    public void AKeptConsumerSamplesAnImageWhoseWholeStandingChainRetired() {
        using var scene = new StandingScene(
            ["view"],
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(Producer: "camera")),
                Instance("main", reads: new RenderGraphRead(Producer: "view"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(ScreensGraph(false, "near"), ("near", "view"))
        );
        RenderGraphFootprint[] filmed = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "camera", Width: 1.0),
        ];
        var camera = SettleStandingView(
            filmed: filmed,
            scene: scene
        );
        var main = scene.Node(instance: "main");

        Assert.True(
            condition: scene.Runtime.TryReconfigure(
                // A replacement with a shader pass of its own, whose pipeline the held driver creates.
                graphs: [Graph(pipeline: ScreensGraph(pool: false))],
                refusal: out var refusal,
                root: "main",
                set: Set(Instance(name: "main"))
            ),
            userMessage: refusal?.Message
        );

        using (var opener = new PipelineGateOpener()) {
            scene.Gpu.PipelineGate = opener.Gate;
            scene.Gpu.Recording = true;

            try {
                for (var frame = 0; (frame < 4); frame++) {
                    scene.Gpu.DescriptorWrites.Clear();
                    _ = scene.Produce(footprints: [], roots: MainRoot);

                    Assert.True(condition: main.HasPendingCandidate);
                    Assert.Contains(
                        collection: scene.Gpu.DescriptorWrites,
                        filter: write => (write.Handle == (camera + 1))
                    );
                    Assert.False(condition: scene.Gpu.IsReleased(handle: camera));
                }
            } finally {
                scene.Gpu.PipelineGate = null;
                scene.Gpu.Recording = false;
            }
        }

        main.WaitForBuild();

        for (var frame = 0; (frame < 4); frame++) {
            _ = scene.Produce(footprints: [], roots: MainRoot);
        }

        Assert.False(condition: main.HasPendingCandidate);
        Assert.Equal(
            actual: scene.Gpu.CreatedObjects.Single(predicate: created => (created.Handle == camera)).DisposeCount,
            expected: 1
        );
    }
    /// <summary>A view stands for the camera and the root reads the view. A reconfiguration retires the camera while the
    /// view's replacement builds, so the view's installed graph keeps the camera's image under a hold and the root binds
    /// it through the view. The fake queue finishes the view's submission of the next frame but not the root's, which
    /// reads the camera's image; the view then installs its replacement and releases its hold. The image survives the
    /// root's pending submission, under the root's own lease, and is disposed only once that submission has
    /// finished.</summary>
    [Fact]
    public void AReadersPendingSubmissionOutlivesTheRetirementThatDroppedTheImageItReads() {
        using var scene = new StandingScene(
            ["view"],
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(Producer: "camera")),
                Instance("main", reads: new RenderGraphRead(Producer: "view"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(ScreensGraph(false, "near"), ("near", "view"))
        );
        RenderGraphFootprint[] filmed = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "camera", Width: 1.0),
        ];
        var camera = SettleStandingView(
            filmed: filmed,
            scene: scene
        );
        var view = scene.Node(instance: "view");
        RenderGraphFootprint[] shown = [filmed[0]];

        Assert.True(
            condition: scene.Runtime.TryReconfigure(
                graphs: [Graph(pipeline: ScreensGraph(pool: false)), null],
                refusal: out var refusal,
                root: "main",
                set: Set(
                    Instance(name: "view"),
                    Instance("main", reads: new RenderGraphRead(Producer: "view"))
                )
            ),
            userMessage: refusal?.Message
        );

        scene.Gpu.TracksPendingReads = true;
        scene.Gpu.Recording = true;
        // The next frame's first submission, the view's, finishes; the root's after it stays pending.
        scene.Gpu.CompletedThrough = (scene.Gpu.SubmissionsMade + 1L);
        _ = scene.Produce(footprints: shown, roots: MainRoot);
        scene.Gpu.Recording = false;

        Assert.True(condition: view.HasPendingCandidate);
        Assert.Contains(
            collection: scene.Gpu.DescriptorWrites,
            filter: write => (write.Handle == (camera + 1))
        );

        view.WaitForBuild();

        for (var frame = 0; (frame < 6); frame++) {
            _ = scene.Produce(footprints: shown, roots: MainRoot);
        }

        Assert.False(condition: view.HasPendingCandidate);
        Assert.True(condition: scene.Gpu.IsReleased(handle: camera));
    }

    // Produces until the view stands for the camera's latest image, and returns that image's handle.
    private static nint SettleStandingView(StandingScene scene, RenderGraphFootprint[] filmed) {
        TestLiveness.Until(
            reason: () => "The view never stood for the camera's image.",
            step: () => {
                _ = scene.Produce(footprints: filmed, roots: MainRoot);

                return (
                    scene.Runtime.IsSettled &&
                    (scene.Node(instance: "view").PublishedBinding == "world") &&
                    scene.Runtime.TryLatestImage(image: out var image, instance: "view") &&
                    (image.ImageHandle == scene.Recorders.Of(instance: "camera").OutputImage)
                );
            }
        );
        _ = scene.Produce(footprints: filmed, roots: MainRoot);

        return scene.Recorders.Of(instance: "camera").OutputImage;
    }
    // Draws the loop for a while, then has every pass in it draw nothing: each frame the root still has a completed image,
    // which the display is handed and which is no stand-in, and the root's own pass draws rather than standing for itself.
    private static void AssertFeedbackKeepsAnImage(StandingScene scene, RenderGraphFootprint[] shown, string[] loop) {
        scene.StandsIn = false;
        TestLiveness.Until(
            reason: () => "The loop never settled.",
            step: () => {
                _ = scene.Produce(footprints: shown, roots: MainRoot);

                return (scene.Runtime.IsSettled && loop.All(predicate: name => scene.Node(instance: name).IsReady));
            }
        );
        _ = scene.Produce(footprints: shown, roots: MainRoot);
        scene.StandsIn = true;

        for (var frame = 0; (frame < 8); frame++) {
            var displayed = scene.Produce(footprints: shown, roots: MainRoot);

            Assert.NotEqual(
                actual: displayed.ImageHandle,
                expected: 0
            );
            Assert.True(condition: scene.Runtime.TryLatestImage(image: out var latest, instance: "main"));
            Assert.Equal(
                actual: latest.ImageHandle,
                expected: displayed.ImageHandle
            );
            Assert.Null(@object: scene.Node(instance: "main").PublishedBinding);
        }
    }
}
