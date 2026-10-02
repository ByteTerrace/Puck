using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>timeline</c> section's clocks. A tick clock's period is a whole number of engine ticks and
/// its phase at a tick is exact; a state clock reads a Fixed or Int row; each clock is one or the other, named once, and
/// a cloud layer's rates are never keyed on a state clock or bound to a state row, whose value can jump between two
/// ticks.
/// </summary>
public sealed class WorldTimelineLawTests {
    private static WorldStateRow Row(string name, CellKind kind) => new(
        Name: CellName.Parse(candidate: name),
        Kind: kind,
        Cells: [new StateCell(
            Key: WorldStateRow.SlotKey,
            Value: ((kind == CellKind.Text) ? CellValue.Text(value: "x") : CellValue.Int(value: 0L))
        )]
    );
    private static WorldDefinition Definition(params WorldClock[] clocks) => new(
        StateRaw: new WorldStateSection(World: [Row(kind: CellKind.Int, name: "tide"), Row(kind: CellKind.Text, name: "label")]),
        TimelineRaw: new WorldTimelineSection(Clocks: clocks)
    );
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        )
            ? string.Empty
            : reason
        );

    [Fact]
    public void A_tick_clock_and_a_state_clock_are_admitted() {
        Assert.Equal(
            actual: Validate(definition: Definition(
                new WorldClock(Name: "day", PeriodSeconds: 1200d, SpanSeconds: 86400d, StartSeconds: 25200d),
                new WorldClock(Name: "tide", State: "tide")
            )),
            expected: string.Empty
        );
    }
    [InlineData("both", 60d, null, "tide", "names both a state row and a period")]
    [InlineData("neither", null, null, null, "names neither a period nor a state row")]
    [InlineData("fraction", 0.00001d, null, null, "is not a whole number of engine ticks")]
    [InlineData("negative", -1d, null, null, "must be finite and at least one engine tick")]
    [InlineData("start", 60d, 60d, null, "startSeconds must be finite and in [0, span)")]
    [InlineData("missing", null, null, "ebb", "names no state row 'ebb'")]
    [InlineData("text", null, null, "label", "must be a Fixed or Int row")]
    [Theory]
    public void A_malformed_clock_is_refused_by_name(string name, double? period, double? start, string? state, string refusal) {
        Assert.Contains(
            expectedSubstring: refusal,
            actualString: Validate(definition: Definition(new WorldClock(Name: name, PeriodSeconds: period, StartSeconds: start, State: state)))
        );
    }
    [Fact]
    public void A_clock_name_is_unique_and_authorable() {
        Assert.Contains(
            expectedSubstring: "'day' is duplicated",
            actualString: Validate(definition: Definition(
                new WorldClock(Name: "day", PeriodSeconds: 60d),
                new WorldClock(Name: "day", PeriodSeconds: 90d)
            ))
        );
        Assert.NotEqual(
            actual: Validate(definition: Definition(new WorldClock(Name: "day$1", PeriodSeconds: 60d))),
            expected: string.Empty
        );
    }
    [Fact]
    public void A_tick_clocks_phase_follows_its_period_and_start_exactly() {
        var clock = new WorldClock(Name: "day", PeriodSeconds: 1200d, SpanSeconds: 86400d, StartSeconds: 21600d);
        var period = WorldClocks.PeriodTicks(clock: clock);

        Assert.Equal(actual: period, expected: (1200UL * EngineTicks.PerSecond));
        Assert.Equal(expected: (period / 4UL), actual: WorldClocks.StartTicks(clock: clock));
        // Six hours into a twenty-four hour span at tick zero, then a whole period later past 2^32 ticks.
        Assert.Equal(expected: 0.25d, actual: WorldClocks.Phase(clock: clock, tick: new PresentedTick(Fraction: 0d, Whole: 0UL)));
        Assert.Equal(expected: 0.25d, actual: WorldClocks.Phase(clock: clock, tick: new PresentedTick(Fraction: 0d, Whole: (period * 100UL))));
        Assert.Equal(expected: 0.5d, actual: WorldClocks.Phase(clock: clock, tick: new PresentedTick(Fraction: 0d, Whole: ((period * 100UL) + (period / 4UL)))));
        Assert.Equal(expected: 0.75d, actual: WorldClocks.Phase(value: 41.75d));
    }
    [Fact]
    public void A_cloud_rate_keyed_on_a_state_clock_is_refused() {
        static WorldDefinition Spinning(string clock) => Definition(
            new WorldClock(Name: "day", PeriodSeconds: 60d),
            new WorldClock(Name: "tide", State: "tide")
        ) with {
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Clouds(
                Coverage: 0.4f,
                Spin: new BindableScalar(keys: new WorldKeyTrack<float>(
                    clock: clock,
                    keys: [new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0f), new WorldKey<float>(At: 0.5d, Ease: WorldEase.Linear, Value: 0.2f)]
                ))
            )])),
        };

        Assert.Contains(
            expectedSubstring: "may key only on a tick clock; clock 'tide' reads state row 'tide'",
            actualString: Validate(definition: Spinning(clock: "tide"))
        );
        // Control: the same rate keyed on a tick clock is admitted.
        Assert.Equal(
            actual: Validate(definition: Spinning(clock: "day")),
            expected: string.Empty
        );
    }
    [Fact]
    public void A_cloud_rate_may_not_bind_a_state_row() {
        Assert.Contains(
            expectedSubstring: "may not bind a state row",
            actualString: Validate(definition: Definition() with {
                RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Clouds(Spin: new BindableScalar(binding: "state.tide"))])),
            })
        );
    }
}
