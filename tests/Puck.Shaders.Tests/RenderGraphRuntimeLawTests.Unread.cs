using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// Demand loss: an instance nothing the display shows reaches any more gives its graph back, as a seat's view does when the
// seat leaves, and builds it again the next time something shows it. A frame that only skips an instance for its refresh
// keeps everything the instance has.
public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void AnInstanceNothingShowsReleasesItsGraphAndRebuildsWhenShownAgain() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var set = Set(
            Instance(name: "seat"),
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
                    new RenderGraphRead(Producer: "slow"),
                ]
            )
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(false, "near", "far"), ("near", "seat"), ("far", "slow"))
        );
        var seat = set.IndexOf(name: "seat");
        var slow = set.IndexOf(name: "slow");
        var index = 0L;

        // A seated frame shows the seat's view beside the slow view; an unseated one only the slow view.
        Surface Produce(bool seated) {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: (seated
                    ? [
                        new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "seat", Width: 0.5),
                        new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "slow", Width: 0.5),
                    ]
                    : [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "slow", Width: 0.5)]),
                Index: index++,
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)]
            );

            return runtime.ProduceFrame(
                context: default,
                frame: in frame
            );
        }
        void Settle(bool seated) {
            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        _ = Produce(seated: seated);

                        return runtime.IsSettled;
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: "The runtime's scheduled instances never all produced."
            );
            _ = Produce(seated: seated);
        }
        (ulong Live, ulong Owned, int Recorders) Seat() => (
            gpu.LiveBytes,
            runtime.Node(instance: seat).OwnedBytes,
            (recorders.Of(instance: "seat").Created - recorders.Of(instance: "seat").Disposed)
        );

        Settle(seated: false);

        var alone = gpu.LiveBytes;

        Assert.Equal(
            actual: Seat(),
            expected: (alone, 0UL, 0)
        );

        // The seat joins: its view builds and renders.
        Settle(seated: true);

        var joined = Seat();

        Assert.True(condition: (joined.Live > alone));
        Assert.True(condition: (joined.Owned > 0UL));
        Assert.Equal(
            actual: joined.Recorders,
            expected: 1
        );

        // The seat leaves: the first frame nothing shows its view releases every object the view created.
        _ = Produce(seated: false);
        Assert.Equal(
            actual: (Status: runtime.Latest!.Instances[seat].Status, Seat: Seat()),
            expected: (Status: RenderGraphInstanceStatus.Unread, Seat: (alone, 0UL, 0))
        );

        // The slow view waits three frames of every four and keeps what it has through them.
        var slowBytes = runtime.Node(instance: slow).OwnedBytes;
        var statuses = new List<RenderGraphInstanceStatus>();

        for (var frame = 0; (frame < 8); frame++) {
            _ = Produce(seated: false);
            statuses.Add(item: runtime.Latest!.Instances[slow].Status);
            Assert.Equal(
                actual: (runtime.Node(instance: slow).OwnedBytes, recorders.Of(instance: "slow").Created),
                expected: (slowBytes, 1)
            );
        }

        Assert.Contains(
            collection: statuses,
            expected: RenderGraphInstanceStatus.Waiting
        );

        // The seat joins again: its view rebuilds at the extent it is shown at and renders.
        var records = recorders.Of(instance: "seat").Records;

        Settle(seated: true);
        Assert.Equal(
            actual: Seat(),
            expected: joined
        );
        Assert.Equal(
            actual: recorders.Of(instance: "seat").Created,
            expected: 2
        );
        Assert.True(condition: (recorders.Of(instance: "seat").Records > records));
    }
}
