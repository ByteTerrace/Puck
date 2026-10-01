using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Values that must stay strictly ascending (a gradient's stop elevations, an ink band's ends) are judged over
/// every phase of their clock, between keys as well as at them: no admitted document resolves out of order anywhere.
/// </summary>
public sealed class KeyedOrderLawTests {
    private static WorldDefinition Definition(WorldRenderSky? sky = null, WorldRenderLighting? lighting = null) => new(
        RenderRaw: new WorldRenderDefaults(Lighting: lighting, Sky: sky),
        TimelineRaw: new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 60d, SpanSeconds: 24d)])
    );
    private static string Validate(WorldDefinition definition) => (WorldDefinitionValidator.TryValidateLocally(
        definition: definition,
        reason: out var reason
    )
        ? string.Empty
        : reason
    );
    private static BindableScalar Keyed(params (double At, float Value, WorldEase Ease)[] keys) => new(keys: new WorldKeyTrack<float>(
        clock: "day",
        keys: [.. keys.Select(selector: static key => new WorldKey<float>(At: key.At, Ease: key.Ease, Value: key.Value))]
    ));
    private static WorldRenderSky Gradient(params BindableScalar[] elevations) => new(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [
        .. elevations.Select(selector: static (elevation, index) => new WorldRenderSkyStop(
            Color: new BindableColor(Raw: ((index == 0) ? "#000000" : "#FFFFFF")),
            Elevation: elevation
        )),
    ])]);
    private static WorldRenderLighting Ink(BindableScalar low, BindableScalar high) => new(Curvature: new WorldRenderCurvature(InkHigh: high, InkLow: low));

    // A lower stop rising linearly from -0.1 to 0.8 while the upper holds 0 until it steps to 0.9.
    private static readonly BindableScalar RisingLower = Keyed((0d, -0.1f, WorldEase.Linear), (12d, 0.8f, WorldEase.Linear));
    private static readonly BindableScalar SteppedUpper = Keyed((0d, 0f, WorldEase.Step), (12d, 0.9f, WorldEase.Step));

    [Fact]
    public void Values_ordered_at_every_key_that_cross_between_keys_are_refused() {
        var lower = RisingLower.Keys!;
        var upper = SteppedUpper.Keys!;

        // Red leg: a judge reading only the keys sees the pair ordered at both, yet halfway between they are reversed.
        foreach (var at in new[] { 0d, 12d }) {
            Assert.True(condition: (WorldKeyResolver.Scalar(phase: (at / 24d), span: 24d, track: lower) < WorldKeyResolver.Scalar(phase: (at / 24d), span: 24d, track: upper)));
        }

        Assert.True(condition: (WorldKeyResolver.Scalar(phase: 0.25d, span: 24d, track: lower) > WorldKeyResolver.Scalar(phase: 0.25d, span: 24d, track: upper)));
        Assert.Contains(
            actualString: Validate(definition: Definition(sky: Gradient(RisingLower, SteppedUpper))),
            expectedSubstring: "render.sky.layers[0].stops[1].elevation must exceed render.sky.layers[0].stops[0].elevation wherever they resolve; between 0 and 12 on clock 'day' the value below can reach 0.8 where the value above can fall to 0."
        );
        Assert.Contains(
            actualString: Validate(definition: Definition(lighting: Ink(
                high: Keyed((0d, 10f, WorldEase.Step), (12d, 90f, WorldEase.Step)),
                low: Keyed((0d, 5f, WorldEase.Linear), (12d, 80f, WorldEase.Linear))
            ))),
            expectedSubstring: "render.lighting.curvature.inkHigh must exceed render.lighting.curvature.inkLow wherever they resolve; between 0 and 12 on clock 'day' the value below can reach 80 where the value above can fall to 10; an absent end is the engine default."
        );
    }
    [Fact]
    public void A_crossing_just_before_a_step_is_refused_and_a_value_kept_below_the_held_one_is_admitted() {
        // The lower stop reaches 0.6 only as the upper steps from its held value to 0.9; the keys see 0 < h and 0.6 < 0.9.
        static string Judge(float held) => Validate(definition: Definition(sky: Gradient(
            Keyed((0d, 0f, WorldEase.Linear), (12d, 0.6f, WorldEase.Linear)),
            Keyed((0d, held, WorldEase.Step), (12d, 0.9f, WorldEase.Step))
        )));

        Assert.Contains(actualString: Judge(held: 0.55f), expectedSubstring: "between 0 and 12 on clock 'day' the value below can reach 0.6 where the value above can fall to 0.55");
        // Touching the held value at the step's limit is refused too: the pair must stay strictly apart.
        Assert.Contains(actualString: Judge(held: 0.6f), expectedSubstring: "between 0 and 12 on clock 'day' the value below can reach 0.6 where the value above can fall to 0.6");
        Assert.Equal(expected: string.Empty, actual: Judge(held: 0.65f));
    }
    [Fact]
    public void A_crossing_through_the_wrap_is_refused() {
        // Between the last key and the first, through the end of the span, the lower rises to 0.2 while the upper holds
        // 0.15 until it steps to 0.5; at both keys the pair is ordered.
        Assert.Contains(
            actualString: Validate(definition: Definition(sky: Gradient(
                Keyed((6d, 0.2f, WorldEase.Linear), (18d, 0.1f, WorldEase.Linear)),
                Keyed((6d, 0.5f, WorldEase.Linear), (18d, 0.15f, WorldEase.Step))
            ))),
            expectedSubstring: "between 18 and 6 on clock 'day'"
        );
    }
    [Fact]
    public void Ordered_steps_hold_until_the_key_even_when_the_wrap_fraction_rounds_to_one() {
        var lower = Keyed((0d, 0.4f, WorldEase.Step), (6d, 0.1f, WorldEase.Step), (18d, 0.4f, WorldEase.Step));
        var upper = Keyed((6d, 0.2f, WorldEase.Step), (18d, 0.5f, WorldEase.Step));

        Assert.Equal(expected: string.Empty, actual: Validate(definition: Definition(sky: Gradient(lower, upper))));

        var before = Math.BitDecrement(x: 0.25d);

        // The upper's wrap fraction rounds to one before the key, while the lower's does not. Both still hold.
        Assert.Equal(expected: 1d, actual: WorldKeyResolver.Segment(phase: before, span: 24d, track: upper.Keys!).Fraction);
        Assert.Equal(expected: 0.4f, actual: WorldKeyResolver.Scalar(phase: before, span: 24d, track: lower.Keys!));
        Assert.Equal(expected: 0.5f, actual: WorldKeyResolver.Scalar(phase: before, span: 24d, track: upper.Keys!));

        foreach (var phase in new[] { before, 0.25d, Math.BitIncrement(x: 0.25d), Math.BitDecrement(x: 1d), 0d }) {
            Assert.True(condition: (WorldKeyResolver.Scalar(phase: phase, span: 24d, track: lower.Keys!) < WorldKeyResolver.Scalar(phase: phase, span: 24d, track: upper.Keys!)));
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void An_empty_ordered_track_is_refused_by_name_without_resolving_it(bool emptyUpper) {
        var lower = (emptyUpper ? Keyed((0d, 0.1f, WorldEase.Linear)) : Keyed());
        var upper = (emptyUpper ? Keyed() : Keyed((0d, 0.9f, WorldEase.Linear)));

        Assert.Contains(
            actualString: Validate(definition: Definition(sky: Gradient(lower, upper))),
            expectedSubstring: $"render.sky.layers[0].stops[{(emptyUpper ? 1 : 0)}].elevation must carry at least one key."
        );
        Assert.Contains(
            actualString: Validate(definition: Definition(lighting: Ink(high: upper, low: lower))),
            expectedSubstring: $"render.lighting.curvature.{(emptyUpper ? "inkHigh" : "inkLow")} must carry at least one key."
        );
    }
    [Fact]
    public void A_mixed_ease_document_that_never_crosses_is_admitted() {
        var sky = Gradient(
            Keyed((0d, -0.5f, WorldEase.Smooth), (8d, -0.2f, WorldEase.Linear), (16d, -0.4f, WorldEase.Step)),
            Keyed((0d, 0.1f, WorldEase.Linear), (12d, 0.3f, WorldEase.Smooth)),
            0.6f,
            Keyed((4d, 0.9f, WorldEase.Step), (20d, 0.7f, WorldEase.Smooth))
        );
        var lighting = Ink(
            high: Keyed((6d, 60f, WorldEase.Step), (18d, 80f, WorldEase.Smooth)),
            low: Keyed((0d, 20f, WorldEase.Smooth), (12d, 40f, WorldEase.Linear))
        );

        Assert.Equal(expected: string.Empty, actual: Validate(definition: Definition(lighting: lighting, sky: sky)));
    }
    [Fact]
    public void Neighbours_whose_difference_is_monotone_are_judged_at_its_ends() {
        // Both rise linearly and their ranges overlap ([0, 0.3] against [0.2, 0.9]), but their difference is affine and
        // positive at both ends, so they never meet. Two smoothsteps over one window are judged the same way.
        Assert.Equal(expected: string.Empty, actual: Validate(definition: Definition(sky: Gradient(
            Keyed((0d, 0f, WorldEase.Linear), (12d, 0.3f, WorldEase.Linear)),
            Keyed((0d, 0.2f, WorldEase.Linear), (12d, 0.9f, WorldEase.Linear))
        ))));
        Assert.Equal(expected: string.Empty, actual: Validate(definition: Definition(sky: Gradient(
            Keyed((0d, 0f, WorldEase.Smooth), (12d, 0.3f, WorldEase.Smooth)),
            Keyed((0d, 0.2f, WorldEase.Smooth), (12d, 0.9f, WorldEase.Smooth))
        ))));
    }
    [Fact]
    public void Moving_values_that_touch_after_float_rounding_are_refused() {
        var lower = Keyed((0d, 0f, WorldEase.Linear), (12d, 0.9f, WorldEase.Step));
        var upper = Keyed((0d, float.Epsilon, WorldEase.Linear), (12d, MathF.BitIncrement(x: 0.9f), WorldEase.Step));

        // Their real affine difference stays positive, but both resolved floats are 0.63 at this interior phase.
        Assert.Equal(
            expected: WorldKeyResolver.Scalar(phase: 0.35d, span: 24d, track: lower.Keys!),
            actual: WorldKeyResolver.Scalar(phase: 0.35d, span: 24d, track: upper.Keys!)
        );
        Assert.Contains(
            actualString: Validate(definition: Definition(sky: Gradient(lower, upper))),
            expectedSubstring: "render.sky.layers[0].stops[1].elevation must exceed render.sky.layers[0].stops[0].elevation wherever they resolve; between 0 and 12 on clock 'day'"
        );
        Assert.Contains(
            actualString: Validate(definition: Definition(lighting: Ink(high: upper, low: lower))),
            expectedSubstring: "render.lighting.curvature.inkHigh must exceed render.lighting.curvature.inkLow wherever they resolve; between 0 and 12 on clock 'day'"
        );
    }
    [Fact]
    public void A_smoothstep_against_a_linear_value_is_bounded_by_its_range() {
        // Ordered at both keys and at both ends of the interval, yet a quarter from its end the smoothstep (0.675) has
        // passed the linear value (0.6425): a difference that is not monotone is judged by each value's range.
        var lower = Keyed((0d, 0f, WorldEase.Smooth), (12d, 0.8f, WorldEase.Linear));
        var upper = Keyed((0d, 0.02f, WorldEase.Linear), (12d, 0.85f, WorldEase.Linear));

        Assert.True(condition: (WorldKeyResolver.Scalar(phase: (9d / 24d), span: 24d, track: lower.Keys!) > WorldKeyResolver.Scalar(phase: (9d / 24d), span: 24d, track: upper.Keys!)));
        Assert.Contains(
            actualString: Validate(definition: Definition(sky: Gradient(lower, upper))),
            expectedSubstring: "between 0 and 12 on clock 'day' the value below can reach 0.8 where the value above can fall to 0.02"
        );
    }
    [Fact]
    public void An_ink_end_bound_to_a_state_row_is_refused_by_name() {
        Assert.Contains(
            actualString: Validate(definition: Definition(lighting: Ink(high: 90f, low: new BindableScalar(binding: "state.tide")))),
            expectedSubstring: "render.lighting.curvature.inkLow may not bind a state row: render.lighting.curvature must stay ascending, which a row's value cannot promise."
        );
    }
}
