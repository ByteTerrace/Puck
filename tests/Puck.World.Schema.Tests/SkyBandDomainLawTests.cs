using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Angular masks share the presentation pair guard without inheriting curvature's positive-width rule.</summary>
public sealed class SkyBandDomainLawTests {
    [Fact]
    public void A_closed_band_accepts_negative_and_equal_ends_then_holds_both_ends_on_reversal() {
        var guard = new WorldValueDomainGuard();
        var reports = new List<WorldValueDomainDiagnostic>();
        var domain = new WorldValueDomain(-Math.PI / 2, Math.PI / 2);

        guard.Transition += reports.Add;
        (double Low, double High) Read(double low, double high) => guard.OrderedPair("sky.mask", "low/high", low, high,
            -.5d, .5d, domain, strict: false);
        Assert.Equal((-.4d, .3d), Read(-.4d, .3d));
        Assert.Equal((-.2d, -.2d), Read(-.2d, -.2d));
        Assert.Equal((-.2d, -.2d), Read(.4d, -.4d));
        var checks = guard.Checks;
        Assert.Equal((-.2d, -.2d), Read(.4d, -.4d));
        Assert.Equal(checks, guard.Checks);
        Assert.Equal((-.1d, .2d), Read(-.1d, .2d));
        Assert.Equal(1, guard.Holds);
        Assert.Equal(2, reports.Count);
        Assert.Equal("held", reports[0].Action);
        Assert.Equal("recovered", reports[1].Action);
        Assert.Empty(guard.Diagnostics);
    }

    [Fact]
    public void An_out_of_domain_end_holds_the_entire_band_instead_of_clamping_one_end() {
        var guard = new WorldValueDomainGuard();
        var domain = new WorldValueDomain(-1d, 1d);

        Assert.Equal((-.2d, .7d), guard.OrderedPair("band", "state.band", -.2d, .7d, -.5d, .5d, domain, strict: false));
        Assert.Equal((-.2d, .7d), guard.OrderedPair("band", "state.band", -.8d, 2d, -.5d, .5d, domain, strict: false));
        Assert.Equal(0, guard.Clamps);
        Assert.Equal(.7d, Assert.Single(guard.Diagnostics).SecondUsed);
    }

    [Fact]
    public void Keyed_angle_bands_hold_the_latest_pair_and_reuse_metadata_on_unchanged_inputs() {
        var definition = new WorldDefinition(TimelineRaw: new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]));
        var low = new BindableAngle(new BindableScalar(keys: new WorldKeys<BindableScalar>("day", [new(0d, -.5f), new(4d, 1f)])));
        var high = new BindableAngle(.5f);
        var guard = new WorldValueDomainGuard();
        var group = new WorldValueDomainGroup(guard, definition, "sky");
        var domain = new WorldValueDomain(-MathF.PI / 2f, MathF.PI / 2f);
        WorldValueResolver Values(ulong seconds) => new(definition, new PresentedTick(seconds * EngineTicks.PerSecond, 0d));

        Assert.Equal((-.125f, .5f), group.AngleBand("mask.elevation", low, high, Values(1), -1f, 1f, domain));
        var held = Values(4);
        Assert.Equal((-.125f, .5f), group.AngleBand("mask.elevation", low, high, held, -1f, 1f, domain));
        var checks = guard.Checks;
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) {
            _ = group.AngleBand("mask.elevation", low, high, held, -1f, 1f, domain);
        }
        Assert.Equal(bytes, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(checks, guard.Checks);
        Assert.Equal(1, guard.Holds);
    }
}
