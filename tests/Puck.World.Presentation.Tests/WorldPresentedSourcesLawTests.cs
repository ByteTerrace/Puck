using Puck.Hosting;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>Laws for the sources of a world shown through a screen, or a world a seat is presented in, drawn from that
/// world's own: its text screens draw through its own font catalog, resolved beside its own document, and its cameras
/// are views of it that read each other at their previous frame, so two cameras of one world filming each other's
/// screens compose.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldPresentedSourcesLawTests {
    private static WorldView View(string name, IReadOnlyList<string> reads, IReadOnlyList<string>? previous = null) => new(
        Demand: WorldViewDemand.Screen,
        FilmsWorld: false,
        Height: 0.25,
        Name: name,
        Refresh: RenderGraphRefresh.EveryFrame,
        Width: 0.25
    ) {
        Parent = "session$0",
        PreviousReads = previous,
        Reads = reads,
    };
    // THE LAW: the cameras of a world shown through a screen read each other at their previous frame. Two cameras of one
    // level, each filming a screen that shows the other, compose with the session that shows them, which reads both
    // within the frame. The red leg reads each camera within the frame, as the session does: the set refuses the cycle.
    [Fact]
    public void TheCamerasOfAPresentedWorldReadEachOtherAtTheirPreviousFrame() {
        string[] cameras = ["session$0$camera$east", "session$0$camera$west"];

        RenderGraphInstanceRefusal? Compose(bool previous) => (RenderGraphInstanceSet.TryCreate(
            instances: WorldViewInstances.Of(views: [
                View(name: "session$0", reads: cameras) with { Parent = null },
                .. cameras.Select(selector: camera => (previous
                    ? View(name: camera, previous: cameras, reads: [])
                    : View(name: camera, reads: cameras))),
            ]).Instances(sources: []),
            refusal: out var refusal,
            set: out _
        )
            ? null
            : refusal);

        Assert.Null(@object: Compose(previous: true));
        Assert.Equal(
            actual: Compose(previous: false)?.Code,
            expected: RenderGraphInstanceRefusalCode.SameFrameCycle
        );
    }
}
