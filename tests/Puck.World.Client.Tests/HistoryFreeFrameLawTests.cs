using Puck.SdfVm;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A frame held for a settled capture culls by box rather than by the occlusion test and rates no work; any
/// other frame takes the session's levers.</summary>
public sealed class HistoryFreeFrameLawTests {
    [Fact]
    public void AFrameHeldForACaptureDropsTheOcclusionTestAndTheRates() {
        Assert.Equal(expected: SdfMeshCullLevel.Frustum, actual: WorldFramePresenter.CullOf(cull: SdfMeshCullLevel.Occlusion, settles: true));
        Assert.Equal(expected: SdfMeshCullLevel.Off, actual: WorldFramePresenter.CullOf(cull: SdfMeshCullLevel.Off, settles: true));
        Assert.Equal(expected: SdfMeshCullLevel.Occlusion, actual: WorldFramePresenter.CullOf(cull: SdfMeshCullLevel.Occlusion, settles: false));
        Assert.Equal(expected: SdfUpdateRates.None, actual: WorldFramePresenter.RatesOf(rates: SdfUpdateRates.All, settles: true));
        Assert.Equal(expected: SdfUpdateRates.All, actual: WorldFramePresenter.RatesOf(rates: SdfUpdateRates.All, settles: false));
    }
}
