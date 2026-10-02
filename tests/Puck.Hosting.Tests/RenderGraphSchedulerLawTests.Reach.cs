namespace Puck.Hosting.Tests;

// Waiting, unread and unnamed: an instance a consumer shows waits while that consumer's refresh is not due, however many
// frames it waits. One nothing the display shows reaches is unread while something still names it, and unnamed, whose
// graph a render-graph runtime releases, only once nothing does.
public sealed partial class RenderGraphSchedulerLawTests {
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [Theory]
    public void AZeroExtentRootStillNamesItsProducers(double width, double height) {
        var set = Set(
            Instance(name: "camera"),
            Instance(name: "pane", reads: [new RenderGraphRead(Producer: "camera")])
        );
        var schedules = Run(
            count: 2,
            frame: index => Frame(
                footprints: [new RenderGraphFootprint(Consumer: "pane", Height: 1, Producer: "camera", Width: 1)],
                index: index,
                roots: [new RenderGraphRoot(Height: ((index == 0) ? 1 : height), Instance: "pane", Width: ((index == 0) ? 1 : width))]
            ) with { Named = [] },
            set: set
        );

        Assert.Empty(collection: schedules[1].Renders);
        foreach (var name in new[] { "pane", "camera" }) {
            var row = Row(name: name, schedule: schedules[1], set: set);

            Assert.Equal(expected: RenderGraphInstanceStatus.Unread, actual: row.Status);
            Assert.Equal(expected: 0L, actual: row.LatestFrame);
        }
    }
    [Fact]
    public void AnInstanceOnlyAWaitingConsumerShowsWaitsAndOneNothingShowsIsUnread() {
        var set = Set(
            Instance(name: "camera"),
            Instance(name: "hidden"),
            Instance(
                name: "pane",
                reads: [
                    new RenderGraphRead(Producer: "camera"),
                    new RenderGraphRead(Producer: "hidden"),
                ],
                refresh: RenderGraphRefresh.Every(divisor: 4)
            ),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "pane")]
            )
        );
        RenderGraphFootprint[] footprints = [
            new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "pane", Width: 0.5),
            new RenderGraphFootprint(Consumer: "pane", Height: 1.0, Producer: "camera", Width: 1.0),
        ];
        var schedules = Run(
            count: 8,
            frame: index => Frame(
                footprints: footprints,
                index: index,
                roots: [Full(instance: "main")]
            ),
            set: set
        );

        // The pane renders on frames 0 and 4; on the frames between, the camera it shows waits with it.
        Assert.Equal(
            actual: schedules.Select(selector: schedule => Row(
                name: "camera",
                schedule: schedule,
                set: set
            ).Status),
            expected: [
                RenderGraphInstanceStatus.Rendered,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Rendered,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Waiting,
            ]
        );
        // The pane declares a read of the hidden instance but shows it nowhere, so it is unread on every frame.
        Assert.All(
            action: schedule => Assert.Equal(
                actual: Row(
                    name: "hidden",
                    schedule: schedule,
                    set: set
                ).Status,
                expected: RenderGraphInstanceStatus.Unread
            ),
            collection: schedules
        );
    }
    [Fact]
    public void AnInstanceTheHostNamesIsUnreadAndOneNothingNamesIsUnnamed() {
        var set = Set(
            Instance(name: "screen"),
            Instance(name: "seat"),
            Instance(name: "camera"),
            Instance(
                name: "pane",
                reads: [new RenderGraphRead(Producer: "camera")]
            ),
            Instance(
                name: "main",
                reads: [
                    new RenderGraphRead(Producer: "screen"),
                    new RenderGraphRead(Producer: "seat"),
                    new RenderGraphRead(Producer: "pane"),
                ]
            )
        );
        // The root shows nothing but a zero-extent pane; the host names the screen.
        RenderGraphFootprint[] footprints = [
            new RenderGraphFootprint(Consumer: "main", Height: 0.0, Producer: "pane", Width: 0.0),
            new RenderGraphFootprint(Consumer: "pane", Height: 1.0, Producer: "camera", Width: 1.0),
        ];
        var schedule = Run(
            count: 1,
            frame: index => (Frame(
                footprints: footprints,
                index: index,
                roots: [Full(instance: "main")]
            ) with {
                Named = ["screen"],
            }),
            set: set
        )[0];

        Assert.Equal(
            actual: new[] { "screen", "seat", "pane", "camera" }.Select(selector: name => Row(
                name: name,
                schedule: schedule,
                set: set
            ).Status),
            expected: [
                RenderGraphInstanceStatus.Unread,
                RenderGraphInstanceStatus.Unnamed,
                RenderGraphInstanceStatus.Unread,
                RenderGraphInstanceStatus.Unread,
            ]
        );
        Assert.Throws<ArgumentException>(testCode: () => Run(
            count: 1,
            frame: index => (Frame(
                index: index,
                roots: [Full(instance: "main")]
            ) with {
                Named = ["nowhere"],
            }),
            set: set
        ));
    }
}
