namespace Puck.Hosting.Tests;

public sealed partial class RenderGraphSchedulerLawTests {
    [Fact]
    public void AuthoredPixelsSurviveReaderScaleDisplayResizeAndUnchangedHolds() {
        var camera = Instance(name: "camera", passes: 3) with { OutputExtent = new RenderGraphPixelExtent(Height: 144, Width: 160) };
        var set = Set(camera, Instance(name: "main", reads: [new RenderGraphRead(Producer: "camera")]));
        var schedules = Run(set: set, count: 4, frame: index => Frame(index: index,
            roots: [new RenderGraphRoot(Height: ((index < 2) ? 0.5 : 1), Instance: "main", Width: ((index < 2) ? 0.5 : 1))],
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: (144d / DisplayHeight), Producer: "camera", Width: (160d / DisplayWidth))]) with {
            DisplayWidth = ((index < 2) ? DisplayWidth : 1280),
            DisplayHeight = ((index < 2) ? DisplayHeight : 720),
            Unchanged = ["camera"],
        });

        foreach (var schedule in schedules) {
            var row = Row(name: "camera", schedule: schedule, set: set);

            Assert.Equal(expected: (160, 144), actual: (row.Width, row.Height));
            Assert.Equal(expected: ((row.Status == RenderGraphInstanceStatus.Rendered) ? ((3L * 160) * 144) : 0L), actual: row.PassPixels);
        }
        Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: Row(schedule: schedules[1], set: set, name: "camera").Status);
        Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: Row(schedule: schedules[3], set: set, name: "camera").Status);
    }
    [Fact]
    public void AuthoredPixelExtentRejectsInvalidShapesAndNegotiatedSources() {
        foreach (var instance in new[] {
            Instance(name: "zero") with { OutputExtent = new RenderGraphPixelExtent(Height: 1, Width: 0) },
            Instance(name: "negative") with { OutputExtent = new RenderGraphPixelExtent(Height: -1, Width: 1) },
            Bricks() with { OutputExtent = new RenderGraphPixelExtent(Height: 1, Width: 1) },
            RenderGraphInstance.Source(name: "camera", producer: "camera") with { OutputExtent = new RenderGraphPixelExtent(Height: 1, Width: 1) },
        }) {
            Assert.False(condition: RenderGraphInstanceSet.TryCreate(instances: [instance], refusal: out var refusal, set: out _));
            Assert.Equal(expected: RenderGraphInstanceRefusalCode.ExtentInvalid, actual: refusal!.Code);
        }
    }
}
