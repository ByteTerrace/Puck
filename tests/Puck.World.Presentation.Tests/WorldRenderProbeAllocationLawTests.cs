using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>This suite's scene-probe classes run one at a time (<see cref="SceneProbeScan"/>).</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldRenderProbeAllocationLawTests(ITestOutputHelper output) {
    [Fact]
    public void NoTwoCpuSceneProbeClassesRunAtOnce() => SceneProbeScan.AssertProbesRunOneAtATime(
        assembly: typeof(WorldRenderProbeAllocationLawTests).Assembly,
        controls: [typeof(WorldRenderEnvelopeLawTests), typeof(SdfTapeInventoryLawTests), typeof(SdfTapePredictionLawTests)],
        output: output,
        suiteDirectory: "tests/Puck.World.Presentation.Tests"
    );
}
