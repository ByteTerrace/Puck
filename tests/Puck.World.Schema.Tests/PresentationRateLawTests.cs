using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>World preparation owns analytic rate storage; renderers share it and evaluate only the active piece.</summary>
public sealed class PresentationRateLawTests {
    [Fact]
    public void The_world_budget_counts_shared_backing_once_and_refuses_the_named_crossing_rate() {
        static BindableScalar Steps(float low) => new(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: Enumerable.Range(count: 1024, start: 0)
            .Select(selector: index => new WorldKey<BindableScalar>(At: ((8d * index) / 1024d), Ease: WorldKeyEase.Step, Value: (low + (index & 1)))).ToArray()));
        var first = Steps(low: 1f);
        var second = Steps(low: 3f);
        var cloud = new WorldRenderSkyLayer.Clouds(Drift: new BindableVector2(x: first, y: second), Spin: first) { Name = "wind" };
        var definition = new WorldDefinition(RenderRaw: new WorldRenderDefaults(Sky: new(Layers: [cloud])),
            TimelineRaw: new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]));
        var atLimit = WorldPresentationRates.Of(definition: definition);

        Assert.Empty(collection: atLimit.Errors);
        Assert.Equal(4096, atLimit.Cost.Coefficients);
        Assert.Equal(32768, atLimit.Cost.CoefficientBytes);
        Assert.Same(atLimit.Of(layer: cloud)!.DriftX, atLimit.Of(layer: cloud)!.Spin);
        var extra = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(0d, 1f), new(4d, 2f)]));
        var over = definition with { RenderRaw = new WorldRenderDefaults(Sky: new(Layers: [cloud with { Spin = extra }])) };
        var refused = WorldPresentationRates.Of(definition: over);
        var reason = Assert.Single(collection: refused.Errors);

        Assert.Contains(actualString: reason, expectedSubstring: "render.sky.layers.wind.spin");
        Assert.Contains(actualString: reason, expectedSubstring: "4102 coefficients across this world; limit 4096");
        Assert.Contains(actualString: reason, expectedSubstring: "render.sky.layers.wind.drift[0]=2048");
        Assert.Contains(actualString: reason, expectedSubstring: "render.sky.layers.wind.drift[1]=2048");
        Assert.Contains(actualString: reason, expectedSubstring: "render.sky.layers.wind.spin=6");
        Assert.Equal(4096, refused.Cost.Coefficients);
    }
    [Fact]
    public void Rate_storage_is_compiled_once_and_shared_by_identical_authored_operands() {
        var rate = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(0d, 1f), new(4d, 2f)]));
        var cloud = new WorldRenderSkyLayer.Clouds(Coverage: 1f, Spin: rate) { Name = "wind" };
        var stars = new WorldRenderSkyLayer.Stars(Twinkle: new(Rate: rate)) { Name = "stars" };
        var definition = new WorldDefinition(RenderRaw: new WorldRenderDefaults(Sky: new(Layers: [cloud, stars])),
            TimelineRaw: new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]));
        var prepared = WorldPresentationRates.Of(definition: definition);

        Assert.Empty(collection: prepared.Errors);
        Assert.Same(prepared, WorldPresentationRates.Of(definition: definition));
        var first = prepared.Of(layer: cloud)!.Spin!;

        Assert.Same(first, prepared.Of(layer: stars)!.Twinkle);
        Assert.Equal(first.Cost, prepared.Cost);
        Assert.Equal(2, prepared.Cost.Pieces);
        Assert.Equal(6, prepared.Cost.Coefficients);
        Assert.Equal(48, prepared.Cost.CoefficientBytes);
        _ = first.At(new PresentedTick(Fraction: 0d, Whole: EngineTicks.PerSecond), 4096d, out var work);
        Assert.Equal(1, work.PieceSearches);
        Assert.Equal(3, work.CoefficientBlends);
        Assert.Equal(48, prepared.Cost.CoefficientBytes);
    }
    [Fact]
    public void Literal_rates_and_absent_motion_retain_no_coefficients() {
        var cloud = new WorldRenderSkyLayer.Clouds(Drift: new BindableVector2(x: 0.02f, y: 0f));
        var definition = new WorldDefinition(RenderRaw: new WorldRenderDefaults(Sky: new(Layers: [cloud])));
        var prepared = WorldPresentationRates.Of(definition: definition);

        Assert.Empty(collection: prepared.Errors);
        Assert.Equal(default, prepared.Cost);
        Assert.NotNull(@object: prepared.Of(layer: cloud)!.DriftX);
        Assert.Null(@object: prepared.Of(layer: cloud)!.DriftY);
        Assert.Equal(default, WorldPresentationRates.Of(definition: new WorldDefinition()).Cost);
    }
    [Fact]
    public void A_state_driven_rate_is_refused_with_its_authored_layer_and_component_name() {
        var definition = new WorldDefinition(RenderRaw: new WorldRenderDefaults(Sky: new(Layers: [
            new WorldRenderSkyLayer.Clouds(Drift: new BindableVector2(x: new BindableScalar(binding: "state.wind"), y: 0f)) { Name = "cirrus" },
        ])));
        var reason = Assert.Single(collection: WorldPresentationRates.Of(definition: definition).Errors);

        Assert.Contains(actualString: reason, expectedSubstring: "render.sky.layers.cirrus.drift[0]");
        Assert.Contains(actualString: reason, expectedSubstring: "history");
    }
}
