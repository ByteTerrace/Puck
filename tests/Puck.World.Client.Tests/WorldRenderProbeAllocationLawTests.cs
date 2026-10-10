using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>This suite's scene-probe classes run one at a time (<see cref="SceneProbeScan"/>).</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldRenderProbeAllocationLawTests(ITestOutputHelper output) {
    [Fact]
    public void NoTwoCpuSceneProbeClassesRunAtOnce() => SceneProbeScan.AssertProbesRunOneAtATime(
        assembly: typeof(WorldRenderProbeAllocationLawTests).Assembly,
        controls: [typeof(BodyStampScaleLawTests), typeof(WorldCostLawTests)],
        output: output,
        suiteDirectory: "tests/Puck.World.Client.Tests"
    );
}
