using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// Naming against showing: an instance nothing names any more gives its graph back, as a seat's view does when the seat
// leaves, and builds it again the next time something names and shows it. One that is named but not shown, as a camera
// screen out of view is, keeps everything it has and shows its last image when it is shown again, and so does one a frame
// only skips for its refresh.
public sealed partial class RenderGraphRuntimeLawTests {
    // A root reading a seat's view, a camera screen and a slow view refreshed every fourth frame, over the fake device; each
    // frame shows the views it is asked to and names the screen when asked to.
    private sealed class NamingScene : IDisposable {
        private long m_index;

        public NamingScene() {
            Recorders = new Recorders(Camera);
            Set = RenderGraphRuntimeLawTests.Set(
                Instance(name: "seat"),
                Instance(name: "screen"),
                new RenderGraphInstance(
                    Name: "slow",
                    Output: ShaderPipelineResourceKind.Image,
                    Passes: 1,
                    Reads: [],
                    Refresh: RenderGraphRefresh.Every(divisor: 4)
                ),
                Instance(
                    name: "main",
                    reads: [
                        new RenderGraphRead(Producer: "seat"),
                        new RenderGraphRead(Producer: "screen"),
                        new RenderGraphRead(Producer: "slow"),
                    ]
                )
            );
            Runtime = RenderGraphRuntimeLawTests.Runtime(
                Gpu,
                Recorders,
                Set,
                "main",
                Graph(pipeline: CameraGraph()),
                Graph(pipeline: CameraGraph()),
                Graph(pipeline: CameraGraph()),
                Graph(ScreensGraph(false, "near", "mid", "far"), ("near", "seat"), ("mid", "screen"), ("far", "slow"))
            );
        }

        public FakePipelineGpu Gpu { get; } = new();
        public Recorders Recorders { get; }
        public RenderGraphRuntime Runtime { get; }
        public RenderGraphInstanceSet Set { get; }

        public void Dispose() => Runtime.Dispose();
        // The seat's view shows while the seat is seated; the screen shows while it is in view and is named while it is
        // bound; the slow view always shows.
        public Surface Produce(bool seated, bool screenInView, bool screenBound = true) {
            var footprints = new List<RenderGraphFootprint> {
                new(Consumer: "main", Height: 1.0, Producer: "slow", Width: 0.25),
            };

            if (seated) {
                footprints.Add(item: new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "seat", Width: 0.5));
            }
            if (screenInView) {
                footprints.Add(item: new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "screen", Width: 0.25));
            }

            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: footprints,
                Index: m_index++,
                Named: (screenBound ? ["screen"] : []),
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)]
            );

            return Runtime.ProduceFrame(
                context: default,
                frame: in frame
            );
        }
        public void Settle(bool seated, bool screenInView) {
            Assert.True(
                condition: SpinWait.SpinUntil(
                    // The slow view renders one frame in four, so a settled frame may not have scheduled it: wait for its
                    // graph too.
                    condition: () => {
                        _ = Produce(
                            screenInView: screenInView,
                            seated: seated
                        );

                        return (Runtime.IsSettled && Runtime.Node(instance: Set.IndexOf(name: "slow")).IsReady);
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: "The runtime's scheduled instances never all produced."
            );
            _ = Produce(
                screenInView: screenInView,
                seated: seated
            );
        }
        public RenderGraphInstanceStatus Status(string instance) => Runtime.Latest!.Instances[Set.IndexOf(name: instance)].Status;
        // The device's live bytes, and the instance's owned bytes and live package recorders.
        public (ulong Live, ulong Owned, int Recorders) Holdings(string instance) => (
            Gpu.LiveBytes,
            Runtime.Node(instance: Set.IndexOf(name: instance)).OwnedBytes,
            (Recorders.Of(instance: instance).Created - Recorders.Of(instance: instance).Disposed)
        );
    }

    [Fact]
    public void AnUnnamedGraphKeepsItsPersistentBindingLeaseUntilTheBindingIsReleased() {
        var scene = new NamingScene();
        var retired = 0;

        using (scene) {
            scene.Settle(screenInView: true, seated: true);
            scene.Runtime.Node(instance: scene.Set.IndexOf(name: "main")).HoldBinding(
                lease: new GpuImageLease(ImageViewHandle: 0, Release: _ => retired++),
                name: "near"
            );
            _ = scene.Runtime.ProduceFrame(
                context: default,
                frame: new RenderGraphFrame(DisplayHeight: Display, DisplayHertz: 60, DisplayWidth: Display,
                    Footprints: [], Index: (scene.Runtime.Latest!.Frame + 1), Named: [], Roots: [])
            );

            Assert.False(condition: scene.Runtime.Node(instance: scene.Set.IndexOf(name: "main")).IsReady);
            Assert.Equal(actual: retired, expected: 0);
        }
        Assert.Equal(actual: retired, expected: 1);
    }
    [Fact]
    public void ACaptureWaitingOnTheRuntimeKeepsItsUnnamedTargetUntilWithdrawn() {
        using var scene = new NamingScene();

        scene.Settle(screenInView: true, seated: true);
        var held = scene.Holdings(instance: "seat");
        var request = CaptureRequest();

        scene.Runtime.CaptureTarget(instance: "seat").RequestCapture(request: request);
        Assert.Null(@object: scene.Runtime.Node(instance: scene.Set.IndexOf(name: "seat")).PendingCapturePath);
        _ = scene.Produce(screenInView: true, seated: false);

        Assert.Equal(expected: request.Path, actual: scene.Runtime.PendingCapturePath);
        Assert.Equal(expected: RenderGraphInstanceStatus.Unnamed, actual: scene.Status(instance: "seat"));
        Assert.Equal(expected: held, actual: scene.Holdings(instance: "seat"));

        Assert.True(condition: request.TryFail(error: new OperationCanceledException()));
        _ = scene.Produce(screenInView: true, seated: false);
        Assert.Equal(expected: (0UL, 0), actual: (scene.Holdings(instance: "seat").Owned, scene.Holdings(instance: "seat").Recorders));
    }
    [Fact]
    public void ALossWhileDrainingAnUnnamedGraphReachesTheHostBeforeAnythingIsReleased() {
        using var scene = new NamingScene();

        scene.Settle(screenInView: true, seated: true);
        var held = scene.Holdings(instance: "seat");
        var submissions = scene.Gpu.Submissions;

        scene.Gpu.LoseNextIdleWait = true;

        Assert.Throws<DeviceLostException>(testCode: () => scene.Produce(screenInView: true, seated: false));
        Assert.Equal(expected: submissions, actual: scene.Gpu.Submissions);
        Assert.Equal(expected: held, actual: scene.Holdings(instance: "seat"));
    }
    [Fact]
    public void AnInstanceNothingNamesReleasesItsGraphAndRebuildsWhenShownAgain() {
        using var scene = new NamingScene();

        scene.Settle(
            screenInView: true,
            seated: false
        );

        var alone = scene.Holdings(instance: "seat");

        Assert.Equal(
            actual: (alone.Owned, alone.Recorders),
            expected: (0UL, 0)
        );

        // The seat joins: its view builds and renders.
        scene.Settle(
            screenInView: true,
            seated: true
        );

        var joined = scene.Holdings(instance: "seat");

        Assert.True(condition: (joined.Live > alone.Live));
        Assert.True(condition: (joined.Owned > 0UL));
        Assert.Equal(
            actual: joined.Recorders,
            expected: 1
        );

        // The seat leaves: the first frame nothing names its view releases every object the view created.
        _ = scene.Produce(
            screenInView: true,
            seated: false
        );
        Assert.Equal(
            actual: (Status: scene.Status(instance: "seat"), Seat: scene.Holdings(instance: "seat")),
            expected: (Status: RenderGraphInstanceStatus.Unnamed, Seat: alone)
        );

        // The slow view waits three frames of every four and keeps what it has through them.
        var slowBytes = scene.Holdings(instance: "slow").Owned;
        var statuses = new List<RenderGraphInstanceStatus>();

        for (var frame = 0; (frame < 8); frame++) {
            _ = scene.Produce(
                screenInView: true,
                seated: false
            );
            statuses.Add(item: scene.Status(instance: "slow"));
            Assert.Equal(
                actual: (scene.Holdings(instance: "slow").Owned, scene.Recorders.Of(instance: "slow").Created),
                expected: (slowBytes, 1)
            );
        }

        Assert.Contains(
            collection: statuses,
            expected: RenderGraphInstanceStatus.Waiting
        );

        // The seat joins again: its view rebuilds at the extent it is shown at and renders.
        var records = scene.Recorders.Of(instance: "seat").Records;

        scene.Settle(
            screenInView: true,
            seated: true
        );
        Assert.Equal(
            actual: scene.Holdings(instance: "seat"),
            expected: joined
        );
        Assert.Equal(
            actual: scene.Recorders.Of(instance: "seat").Created,
            expected: 2
        );
        Assert.True(condition: (scene.Recorders.Of(instance: "seat").Records > records));
    }
    [Fact]
    public void ANamedScreenOutOfViewKeepsItsGraphAndShowsItsLastImageWhenBackInView() {
        using var scene = new NamingScene();

        scene.Settle(
            screenInView: true,
            seated: false
        );

        var shown = (scene.Holdings(instance: "screen").Owned, scene.Holdings(instance: "screen").Recorders);
        var screen = scene.Runtime.Node(instance: scene.Set.IndexOf(name: "screen"));

        // The screen leaves view for many frames while it stays bound: it is unread, and keeps every byte and recorder it has.
        for (var frame = 0; (frame < 64); frame++) {
            _ = scene.Produce(
                screenInView: false,
                seated: false
            );
            Assert.Equal(
                actual: (Status: scene.Status(instance: "screen"), Screen: (scene.Holdings(instance: "screen").Owned, scene.Holdings(instance: "screen").Recorders)),
                expected: (Status: RenderGraphInstanceStatus.Unread, Screen: shown)
            );
        }

        // Back in view, the root reads the screen's completed output on the first frame, with nothing rebuilt.
        var frames = screen.FrameCounter;

        _ = scene.Produce(
            screenInView: true,
            seated: false
        );

        var read = scene.Runtime.Latest!.Reads.Single(predicate: static row => ((row.Consumer == "main") && (row.Producer == "screen")));

        Assert.True(condition: screen.IsReady);
        Assert.True(condition: (read.Frame >= 0L));
        Assert.True(condition: (screen.FrameCounter > frames));
        Assert.Equal(
            actual: (scene.Holdings(instance: "screen").Owned, scene.Holdings(instance: "screen").Recorders),
            expected: shown
        );
        Assert.Equal(
            actual: scene.Recorders.Of(instance: "screen").Created,
            expected: 1
        );

        // Unbound, the screen is named by nothing, and releases its graph.
        _ = scene.Produce(
            screenBound: false,
            screenInView: false,
            seated: false
        );
        Assert.Equal(
            actual: (Status: scene.Status(instance: "screen"), Owned: scene.Holdings(instance: "screen").Owned, Recorders: scene.Holdings(instance: "screen").Recorders),
            expected: (Status: RenderGraphInstanceStatus.Unnamed, Owned: 0UL, Recorders: 0)
        );
    }
}
