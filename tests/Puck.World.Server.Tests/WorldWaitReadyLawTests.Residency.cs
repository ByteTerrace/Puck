using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class WorldWaitReadyLawTests {
    [Fact]
    public void AResidencyWaitNeedsFreshBakedGeometryAndItsUploadCompletion() {
        using var row = HostRow.Build(definition: Fixtures.BuildDocument(), name: "boot");
        var bakes = new FlagBakes { Frames = 5 };
        var (source, session, answered) = Console(row, readiness: null, bakes);
        session.Enqueue(line: "world.wait residency 600");
        session.Enqueue(line: "probe");
        source.Collect();
        Assert.Single(answered);
        Assert.Contains("holding until the geometry is resident", answered[0].Result.Output);

        bakes.IsSettled = true;
        bakes.IsResident = true;
        source.Collect();
        Assert.Single(answered); // The old content's completion cannot release a newly armed wait.
        bakes.IsResident = false;
        bakes.Frames++;
        source.Collect();
        Assert.Single(answered); // CPU bake/landing alone is insufficient.
        bakes.IsResident = true;
        source.Collect();
        Assert.Equal(["world.wait residency 600", "probe"], answered.Select(answer => answer.Line));

        var (headless, headlessSession, headlessAnswers) = Console(row, readiness: null);
        headlessSession.Enqueue(line: "world.wait residency 5");
        headless.Collect();
        Assert.True(Assert.Single(headlessAnswers).Result.IsError);
    }
}