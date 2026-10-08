using Xunit;

namespace Puck.World.Tests;

/// <summary>This suite's scene-probe classes run one at a time (<see cref="SceneProbeScan"/>).</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldRenderProbeAllocationLawTests(ITestOutputHelper output) {
    [Fact]
    public void NoTwoCpuSceneProbeClassesRunAtOnce() => SceneProbeScan.AssertProbesRunOneAtATime(
        assembly: typeof(WorldRenderProbeAllocationLawTests).Assembly,
        controls: [typeof(WorldBootCompositionLawTests), typeof(WorldRenderLeverFrameLawTests)],
        output: output,
        suiteDirectory: "tests/Puck.World.Tests"
    );
}
