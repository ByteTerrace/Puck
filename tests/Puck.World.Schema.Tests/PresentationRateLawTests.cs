using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>World preparation owns analytic rate storage; renderers share it and evaluate only the active piece.</summary>
public sealed class PresentationRateLawTests {
    [Fact]
    public void The_world_budget_counts_shared_backing_once_and_refuses_the_named_crossing_rate() {
        static BindableScalar Steps(float low) => new(new WorldKeys<BindableScalar>("day", Enumerable.Range(0, 1024)
            .Select(index => new WorldKey<BindableScalar>(8d * index / 1024d, low + (index & 1), WorldKeyEase.Step)).ToArray()));
        var first = Steps(1f);
        var second = Steps(3f);
        var cloud = new WorldRenderSkyLayer.Clouds(Drift: new BindableVector2(first, second), Spin: first) { Name = "wind" };
        var definition = new WorldDefinition(RenderRaw: new WorldRenderDefaults(Sky: new([cloud])),
            TimelineRaw: new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]));
        var atLimit = WorldPresentationRates.Of(definition);
        Assert.Empty(atLimit.Errors);
        Assert.Equal(4096, atLimit.Cost.Coefficients);
        Assert.Equal(32768, atLimit.Cost.CoefficientBytes);
        Assert.Same(atLimit.Of(cloud)!.DriftX, atLimit.Of(cloud)!.Spin);
        var extra = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, 1f), new(4d, 2f)]));
        var over = definition with { RenderRaw = new WorldRenderDefaults(Sky: new([cloud with { Spin = extra }])) };
        var refused = WorldPresentationRates.Of(over);
        var reason = Assert.Single(refused.Errors);
        Assert.Contains("render.sky.layers.wind.spin", reason);
        Assert.Contains("4102 coefficients across this world; limit 4096", reason);
        Assert.Contains("render.sky.layers.wind.drift[0]=2048", reason);
        Assert.Contains("render.sky.layers.wind.drift[1]=2048", reason);
        Assert.Contains("render.sky.layers.wind.spin=6", reason);
        Assert.Equal(4096, refused.Cost.Coefficients);
    }

    [Fact]
    public void Rate_storage_is_compiled_once_and_shared_by_identical_authored_operands() {
        var rate = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, 1f), new(4d, 2f)]));
        var cloud = new WorldRenderSkyLayer.Clouds(Coverage: 1f, Spin: rate) { Name = "wind" };
        var stars = new WorldRenderSkyLayer.Stars(Twinkle: new(Rate: rate)) { Name = "stars" };
        var definition = new WorldDefinition(RenderRaw: new WorldRenderDefaults(Sky: new([cloud, stars])),
            TimelineRaw: new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]));
        var prepared = WorldPresentationRates.Of(definition);
        Assert.Empty(prepared.Errors);
        Assert.Same(prepared, WorldPresentationRates.Of(definition));
        var first = prepared.Of(cloud)!.Spin!;
        Assert.Same(first, prepared.Of(stars)!.Twinkle);
        Assert.Equal(first.Cost, prepared.Cost);
        Assert.Equal(2, prepared.Cost.Pieces);
        Assert.Equal(6, prepared.Cost.Coefficients);
        Assert.Equal(48, prepared.Cost.CoefficientBytes);
        _ = first.At(new PresentedTick(EngineTicks.PerSecond, 0d), 4096d, out var work);
        Assert.Equal(1, work.PieceSearches);
        Assert.Equal(3, work.CoefficientBlends);
        Assert.Equal(48, prepared.Cost.CoefficientBytes);
    }

    [Fact]
    public void Literal_rates_and_absent_motion_retain_no_coefficients() {
        var cloud = new WorldRenderSkyLayer.Clouds(Drift: new BindableVector2(0.02f, 0f));
        var definition = new WorldDefinition(RenderRaw: new WorldRenderDefaults(Sky: new([cloud])));
        var prepared = WorldPresentationRates.Of(definition);
        Assert.Empty(prepared.Errors);
        Assert.Equal(default, prepared.Cost);
        Assert.NotNull(prepared.Of(cloud)!.DriftX);
        Assert.Null(prepared.Of(cloud)!.DriftY);
        Assert.Equal(default, WorldPresentationRates.Of(new WorldDefinition()).Cost);
    }

    [Fact]
    public void A_state_driven_rate_is_refused_with_its_authored_layer_and_component_name() {
        var definition = new WorldDefinition(RenderRaw: new WorldRenderDefaults(Sky: new([
            new WorldRenderSkyLayer.Clouds(Drift: new BindableVector2(new BindableScalar("state.wind"), 0f)) { Name = "cirrus" },
        ])));
        var reason = Assert.Single(WorldPresentationRates.Of(definition).Errors);
        Assert.Contains("render.sky.layers.cirrus.drift[0]", reason);
        Assert.Contains("history", reason);
    }
}
