namespace Puck.Hosting.Tests;

// Rerenders: the instances a capture frame names to render again. A named instance something shows renders whatever its
// refresh and the budget, before the instances that read it; one nothing shows stays unread; a name that is no instance
// or names a source is refused.
public sealed partial class RenderGraphSchedulerLawTests {
    [Fact]
    public void ARerenderedViewRendersWhateverItsRefreshAndTheBudgetOnlyWhereItIsShown() {
        var set = Set(
            Instance(
                name: "view",
                refresh: RenderGraphRefresh.Every(divisor: 8)
            ),
            Instance(name: "hidden"),
            Instance(
                name: "main",
                reads: [
                    new RenderGraphRead(Producer: "view"),
                    new RenderGraphRead(Producer: "hidden"),
                ]
            )
        );
        RenderGraphFootprint[] footprints = [new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "view", Width: 0.5)];
        // A budget no instance past the root fits, so only the rerender admits the view.
        const long Budget = 1L;
        var schedules = Run(
            count: 4,
            frame: index => (Frame(
                budget: ((index == 0) ? 0L : Budget),
                footprints: footprints,
                index: index,
                roots: [Full(instance: "main")]
            ) with {
                Rerender = ((index == 3) ? ["view", "hidden"] : null),
            }),
            set: set
        );

        // Frame 0 renders the view; frames 1 and 2 are neither due nor affordable; frame 3 names it and renders it first.
        Assert.Equal(
            actual: schedules.Select(selector: schedule => Row(
                name: "view",
                schedule: schedule,
                set: set
            ).Status),
            expected: [
                RenderGraphInstanceStatus.Rendered,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Waiting,
                RenderGraphInstanceStatus.Rendered,
            ]
        );
        Assert.Equal(
            actual: schedules[3].Renders.Select(selector: index => set.Instances[index].Name),
            expected: ["view", "main"]
        );
        Assert.Equal(
            actual: Row(
                name: "hidden",
                schedule: schedules[3],
                set: set
            ).Status,
            expected: RenderGraphInstanceStatus.Unread
        );
    }
    [Fact]
    public void ARerenderNamingNoInstanceOrASourceIsRefused() {
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
                    Rerender = [name],
                }),
                history: RenderGraphHistory.Empty(set: set),
                schedule: schedule,
                set: set
            ));

            Assert.StartsWith(
                actualString: refused.Message,
                expectedStartString: $"Rerender '{name}' names no instance that renders again"
            );
        }
    }
}
