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
            // world.rates lists the session at its tier, and a tier view tints the glass that shows it.
            var reading = Assert.Single(collection: settings.RateReadings.Latest(lever: SdfUpdateRates.Views));

            Assert.Equal(expected: BootSession, actual: reading.Name);
            Assert.Equal(expected: 15, actual: reading.Hertz);

            var codes = new byte[4];

            m_host.WriteScreenRateCodes(codes: codes);
            Assert.Equal(expected: ((byte)SdfUpdateRateCode.Quarter), actual: ((byte)(codes[0] % ((byte)SdfUpdateRateCode.Updated))));
        } else {
            Assert.InRange(actual: renders, high: 16, low: 14);
            Assert.Equal(expected: 0L, actual: settings.RateCounts.Read(kind: SdfUpdateRateCounts.ViewsRated));
            Assert.Null(@object: settings.RateIntervals.Longest(hertz: 15, lever: SdfUpdateRates.Views));
            Assert.Empty(collection: settings.RateReadings.Latest(lever: SdfUpdateRates.Views));

            // Nothing rated tints nothing, and a host showing no screens tints none.
            var codes = new byte[] { 9, 9, 9, 9 };

            m_host.WriteScreenRateCodes(codes: codes);
            Assert.All(collection: codes, action: static code => Assert.Equal(expected: ((byte)SdfUpdateRateCode.None), actual: code));
            m_host.Screens = null;
            codes[0] = 9;
            m_host.WriteScreenRateCodes(codes: codes);
            Assert.Equal(expected: ((byte)SdfUpdateRateCode.None), actual: codes[0]);
        }
    }
}
