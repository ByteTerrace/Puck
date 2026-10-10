using Puck.Hosting;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A presentation-tier projection carries the world's tick clocks, so a value keyed on one resolves on the
/// recipient as it does on the authority, and each state clock a value keys on as an anchor of its phase, never its
/// row; a projection that carries a row, a malformed anchor or no clock a value keys on refuses to hydrate by
/// name.</summary>
public sealed class ProjectionTimelineLawTests {
    private static readonly WorldClock Day = new(Name: "day", PeriodSeconds: 60d, SpanSeconds: 24d, StartSeconds: 6d);
    private static readonly WorldClock Mode = new(Name: "mode", State: "phase");

    private static WorldDefinition Keyed(string clock, bool section = false) {
        var track = new WorldKeyTrack<float>(clock: clock, keys: [
            new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0f),
            new WorldKey<float>(At: 0.5d, Ease: WorldEase.Smooth, Value: 0.1f),
        ]);
        var render = (section
            ? new WorldRenderDefaults(Sky: new WorldRenderSky(
                Clock: clock,
                Keys: [new WorldRenderSkyKey(At: 0d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["haze"] = new WorldRenderSkyLayer.SunDisc(Intensity: 0.02f) })],
                Layers: [new WorldRenderSkyLayer.SunDisc(Intensity: 0.01f, Name: "haze")]
            ))
            : new WorldRenderDefaults(Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: new BindableScalar(keys: track)))));

        return (Fixtures.BuildDocument() with {
            RenderRaw = render,
            TimelineRaw = new WorldTimelineSection(Clocks: [Day, Mode]),
        }).WithWorldState(rows: [new WorldStateRow(
            Name: CellName.Parse(candidate: "phase"),
            Kind: CellKind.Fixed,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: 0L))]
        )]);
    }
    private static WorldProjectionDocument Project(WorldDefinition definition) => Fixtures.Project(
        authority: "boot",
        definition: definition,
        revision: 1,
        tier: WorldDisclosureTier.Presentation
    )!;

    [Fact]
    public void A_value_keyed_on_a_tick_clock_resolves_on_the_recipient_as_on_the_authority() {
        var definition = Keyed(clock: "day");
        var projection = Project(definition: definition);

        Assert.Equal(expected: [Day], actual: projection.Timeline!.Clocks!);
        Assert.True(condition: WorldProjection.TryToDefinition(definition: out var hydrated, projection: projection, reason: out var reason), userMessage: reason);

        var authored = definition.Render.Atmosphere!.Fog!.Density!.Value.Keys!;
        var delivered = hydrated!.Render.Atmosphere!.Fog!.Density!.Value.Keys!;

        Assert.True(condition: WorldKeyResolver.TryClock(clock: out var clock, name: "day", timeline: hydrated.Timeline));

        foreach (var whole in new[] { 0UL, 1234567UL, (WorldClocks.PeriodTicks(clock: Day) / 3UL) }) {
            var tick = new PresentedTick(Fraction: 0.25d, Whole: whole);

            Assert.Equal(
                expected: WorldKeyResolver.Scalar(phase: WorldClocks.Phase(clock: Day, tick: tick), span: Day.Span, track: authored),
                actual: WorldKeyResolver.Scalar(phase: WorldClocks.Phase(clock: clock, tick: tick), span: clock.Span, track: delivered)
            );
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void A_value_keyed_on_a_state_clock_crosses_as_an_anchor_and_resolves_as_on_the_authority(bool section) {
        var definition = Keyed(clock: "mode", section: section).WithWorldState(rows: [new WorldStateRow(
            Name: CellName.Parse(candidate: "phase"),
            Kind: CellKind.Fixed,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: (3L << 14)))]
        )]);
        var projection = Project(definition: definition);
        var carried = Assert.Single(collection: projection.Timeline!.Clocks!, predicate: static clock => (clock.Name == "mode"));

        // The row never crosses: the clock carries an anchor of its phase, three quarters of a turn, held still.
        Assert.True(condition: carried.IsAnchored);
        Assert.Null(@object: carried.State);
        Assert.Equal(expected: new WorldClockAnchor(Phase: (3UL << 62), Tick: 0UL), actual: carried.Anchor);
        Assert.True(condition: WorldProjection.TryToDefinition(definition: out var hydrated, projection: projection, reason: out var reason), userMessage: reason);

        var host = ClientFixtures.StateMirror(definition: definition);
        var recipient = ClientFixtures.StateMirror(definition: hydrated);

        Assert.True(condition: host.TryPhase(clock: out _, name: "mode", phase: out var hostPhase));
        Assert.True(condition: recipient.TryPhase(clock: out _, name: "mode", phase: out var recipientPhase));
        Assert.Equal(actual: hostPhase, expected: 0.75d);
        Assert.Equal(actual: recipientPhase, expected: hostPhase);
    }
    [Fact]
    public void A_projection_that_carries_a_state_clocks_row_or_a_malformed_anchor_refuses_to_hydrate_by_name() {
        var projection = Project(definition: Keyed(clock: "mode"));

        foreach (var (clock, refusal) in new[] {
            (Mode, "projection carries clock 'mode' over state row 'phase'; a projection carries a state clock as an anchor of its phase, never its row."),
            (new WorldClock(Anchor: new WorldClockAnchor(Phase: 0UL, Rate: 5L, Tick: 0UL), Name: "mode"), "projection anchors clock 'mode' with rate 5 over 0 engine ticks; a rate names the ticks it is per, and only a rate does."),
        }) {
            var forged = projection with { Timeline = new WorldTimelineSection(Clocks: [Day, clock]) };

            Assert.False(condition: WorldProjection.TryToDefinition(definition: out _, projection: forged, reason: out var reason));
            Assert.Equal(actual: reason, expected: refusal);
        }

        var uncarried = projection with { Timeline = new WorldTimelineSection(Clocks: [Day]) };

        Assert.False(condition: WorldProjection.TryToDefinition(definition: out _, projection: uncarried, reason: out var uncarriedReason));
        Assert.Equal(actual: uncarriedReason, expected: "projection keys render.atmosphere.fog.density on clock 'mode', which it does not carry.");
    }
}
