using Puck.SdfVm;
using Xunit;

namespace Puck.World.Presentation.Tests;

// The world.rates views lever (SdfUpdateRates.Views): a session whose glass covers few pixels of the display renders at a
// reduced rate through its footprint's refresh, and every frame its consumer renders while the lever is off.
public sealed partial class WorldViewPaneMappingLawTests {
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ASessionSmallOnTheDisplayRendersAtAReducedRateWhileTheViewsLeverIsOn(bool rated) {
        _ = ShowTwoWorlds();
        m_host.ViewScenes = new GlassScenes(Bakes: false, Glass: FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")));

        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults()) {
            UpdateRates = (rated ? SdfUpdateRates.All : SdfUpdateRates.None),
        };

        m_host.RenderSettings = settings;
        m_rate = Paced60;

        var renders = 0;

        // The glass spans a third of a 64-pixel view, far below the 15 Hz tier: a quarter of 60 Hz once its camera is known.
        for (var frame = 0; (frame < 16); frame++) {
            Frame(pane: null);
            renders += RendersOf(instance: BootSession);
        }

        if (rated) {
            Assert.InRange(actual: renders, high: 6, low: 4);
            Assert.True(condition: (settings.RateCounts.Read(kind: SdfUpdateRateCounts.ViewsRated) > 0L));
            // The schedule shows the session rendering every fourth frame between holds: a 15 Hz tier's whole period.
            Assert.Equal(expected: (4d / 60d), actual: (settings.RateIntervals.Longest(hertz: 15, lever: SdfUpdateRates.Views) ?? 0d), tolerance: 1e-9d);
        } else {
            Assert.InRange(actual: renders, high: 16, low: 14);
            Assert.Equal(expected: 0L, actual: settings.RateCounts.Read(kind: SdfUpdateRateCounts.ViewsRated));
            Assert.Null(@object: settings.RateIntervals.Longest(hertz: 15, lever: SdfUpdateRates.Views));
        }
    }
}
