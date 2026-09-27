namespace Puck.Hosting.Tests;

// Unchanged: the instances a frame declares unchanged since their latest render. An unchanged instance that has rendered
// is not due by its refresh, so its latest output stands and its consumers read it; one that has never rendered, one a
// rerender names, and one demanded at another extent render; a name that is no instance or names a source is refused.
public sealed partial class RenderGraphSchedulerLawTests {
    [Fact]
    public void AnUnchangedViewRendersOnlyFirstWhenNamedAgainAndWhenItsExtentMoves() {
        var set = Set(
            Instance(name: "view"),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "view")]
            )
        );
        var schedules = Run(
            count: 6,
            frame: index => (Frame(
                footprints: [new RenderGraphFootprint(Consumer: "main", Height: ((index >= 4) ? 0.25 : 0.5), Producer: "view", Width: 0.5)],
                index: index,
                roots: [Full(instance: "main")]
            ) with {
                Rerender = ((index == 2) ? ["view"] : null),
                Unchanged = ["view"],
            }),
            set: set
        );

        // Frame 0 renders the view it never rendered; frames 1, 3 and 5 hold its output; frame 2 names it again, and frame
        // 4 demands it at another extent, which frame 5 keeps.
        Assert.Equal(
            actual: schedules.Select(selector: schedule => Row(
                name: "view",
                schedule: schedule,
                set: set
            ).Status),
            expected: [
                RenderGraphInstanceStatus.Rendered,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Rendered,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Rendered,
                RenderGraphInstanceStatus.Waiting,
            ]
        );
        // Its consumer renders every frame and reads the latest output the view completed.
        Assert.Equal(expected: 6, actual: RenderCount(name: "main", schedules: schedules, set: set));
        Assert.Equal(
            actual: schedules[5].Reads.Single().Frame,
            expected: 4L
        );
    }
    [Fact]
    public void AnUnchangedNameThatIsNoInstanceOrASourceIsRefused() {
        var set = Set(
            RenderGraphInstance.Source(
                name: "camera",
                producer: "camera"
            ),
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "camera")]
            )
        );
        var schedule = new RenderGraphSchedule(set: set);

        foreach (var name in ((string[])["camera", "nowhere"])) {
            var refused = Assert.Throws<ArgumentException>(testCode: () => RenderGraphScheduler.Schedule(
                frame: (Frame(
                    index: 0,
                    roots: [Full(instance: "main")]
                ) with {
                    Unchanged = [name],
                }),
                history: RenderGraphHistory.Empty(set: set),
                schedule: schedule,
                set: set
            ));

            Assert.StartsWith(
                actualString: refused.Message,
                expectedStartString: $"Unchanged '{name}' names no instance whose host declares its inputs"
            );
        }
    }
}
