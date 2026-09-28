using System.Numerics;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Presentation clocks invalidate only their users; visible rates resolve without history and inactive
/// layers do no analytic work.</summary>
public sealed class WorldEnvironmentKeyLawTests {
    private static void Present(WorldStateMirror mirror, ulong seconds) {
        mirror.Refresh(new WorldStateStamp(seconds, seconds * EngineTicks.PerSecond, ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(1f);
    }

    [Fact]
    public void A_used_clock_changes_the_environment_but_an_unused_clock_and_a_repeated_phase_do_not() {
        var weight = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, 0f), new(4d, 1f)]));
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Lighting: new([new WorldRenderLight.Directional(Weight: weight)])),
            TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d), new WorldClock("unused", PeriodSeconds: 3d)]),
        };
        var mirror = ClientFixtures.StateMirror(definition);
        var resolve = new WorldEnvironmentResolve();
        Assert.Equal(0f, resolve.Resolve(definition, 0, mirror).GetLight(0).Weight);
        Present(mirror, 2);
        Assert.Equal(0.5f, resolve.Resolve(definition, 0, mirror).GetLight(0).Weight);
        Assert.Equal(2, resolve.Resolutions);
        Present(mirror, 10);
        Assert.Equal(0.5f, resolve.Resolve(definition, 0, mirror).GetLight(0).Weight);
        Assert.Equal(2, resolve.Resolutions);
        Assert.Equal(0, resolve.RateEvaluations);
        Assert.Equal(0, resolve.CoefficientBlends);
    }

    [Fact]
    public void Keyed_wind_evaluates_one_compiled_piece_and_seeking_repeats_the_same_displacement() {
        var rate = new BindableScalar(new WorldKeys<BindableScalar>("wind", [new(0d, 0f), new(4d, 2f)]));
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Sky: new([new WorldRenderSkyLayer.Clouds(Coverage: 1f, Drift: new BindableVector2(rate, 0f))])),
            TimelineRaw = new WorldTimelineSection([new WorldClock("wind", PeriodSeconds: 8d)]),
        };
        var mirror = ClientFixtures.StateMirror(definition);
        var resolve = new WorldEnvironmentResolve();
        Present(mirror, 2);
        Assert.Equal(new Vector2(1f, 0f), resolve.Resolve(definition, 0, mirror).CloudOffset);
        Assert.Equal(1, resolve.RateEvaluations);
        Assert.Equal(1, resolve.PieceSearches);
        Assert.Equal(3, resolve.CoefficientBlends);
        _ = resolve.Resolve(definition, 0, mirror);
        Assert.Equal(1, resolve.RateEvaluations);
        Present(mirror, 7);
        _ = resolve.Resolve(definition, 0, mirror);
        var freshMirror = ClientFixtures.StateMirror(definition);
        Present(freshMirror, 2);
        Assert.Equal(new Vector2(1f, 0f), resolve.Resolve(definition, 0, freshMirror).CloudOffset);
        Assert.Equal(WorldPresentationRates.Of(definition).Cost, resolve.CompiledRates);
    }

    [Fact]
    public void Invisible_clouds_and_disabled_twinkle_do_no_rate_work_when_the_tick_advances() {
        var definition = Fixtures.BuildDocument() with { RenderRaw = new WorldRenderDefaults(Sky: new([
            new WorldRenderSkyLayer.Clouds(Coverage: 0f, Drift: new BindableVector2(1f, 2f), Spin: 3f),
            new WorldRenderSkyLayer.Stars(Brightness: 1f, Twinkle: new(Share: 0f, Depth: 1f, Rate: 2f)),
        ])) };
        var mirror = ClientFixtures.StateMirror(definition);
        var resolve = new WorldEnvironmentResolve();
        _ = resolve.Resolve(definition, 0, mirror);
        Present(mirror, 123);
        var environment = resolve.Resolve(definition, 0, mirror);
        Assert.Equal(1, resolve.Resolutions);
        Assert.Equal(0, resolve.RateEvaluations);
        Assert.Equal(0, resolve.PieceSearches);
        Assert.Equal(0, resolve.CoefficientBlends);
        Assert.Equal(0, resolve.PeriodDoublings);
        Assert.Equal(Vector2.Zero, environment.CloudOffset);
        Assert.Equal(0f, environment.TwinklePhase);
    }

    [Fact]
    public void Literal_wind_and_twinkle_are_integrated_at_the_mirrors_presented_tick() {
        var definition = Fixtures.BuildDocument() with { RenderRaw = new WorldRenderDefaults(Sky: new([
            new WorldRenderSkyLayer.Clouds(Coverage: 1f, Drift: new BindableVector2(0.02f, -0.01f), Shear: new BindableVector2(0.005f, 0.003f), Spin: 0.1f),
            new WorldRenderSkyLayer.Stars(Brightness: 1f, Twinkle: new(Share: 1f, Depth: 1f, Rate: 1.25f)),
        ])) };
        var mirror = ClientFixtures.StateMirror(definition);
        Present(mirror, 1);
        var environment = new WorldEnvironmentResolve().Resolve(definition, 0, mirror);
        Assert.Equal(new Vector2(0.02f, -0.01f), environment.CloudOffset);
        Assert.Equal(new Vector2(0.005f, 0.003f), environment.CloudShearOffset);
        Assert.Equal(0.1f, environment.CloudSpinAngle);
        Assert.Equal(0.25f, environment.TwinklePhase);
        Assert.Equal(SdfEnvironment.DefaultStarDensity, environment.StarDensity);
    }
}
