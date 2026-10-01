namespace Puck.Hosting.Tests;

// Unread against waiting: an instance is unread only when nothing the display shows reaches it. One that a consumer shows
// waits while that consumer's refresh is not due, however many frames it waits, because a render-graph runtime releases
// an unread instance's graph and a skipped frame must keep it.
public sealed partial class RenderGraphSchedulerLawTests {
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
}
