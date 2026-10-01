using Puck.Hosting;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A presentation-tier projection carries the world's tick clocks, so a value keyed on one resolves on the
/// recipient as it does on the authority, and carries no state clock: a projection whose values key on a state clock
/// refuses to hydrate by name rather than resolving them to their fallbacks.</summary>
public sealed class ProjectionTimelineLawTests {
    private static readonly WorldClock Day = new(Name: "day", PeriodSeconds: 60d, SpanSeconds: 24d, StartSeconds: 6d);
    private static readonly WorldClock Mode = new(Name: "mode", State: "phase");

    private static WorldDefinition Keyed(string clock, bool section = false) {
        var track = new WorldKeyTrack<float>(clock: clock, keys: [
            new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0f),
            new WorldKey<float>(At: 0.5d, Ease: WorldEase.Smooth, Value: 0.1f),
        ]);
        var sky = (section
            ? new WorldRenderSky(
                Clock: clock,
                Keys: [new WorldRenderSkyKey(At: 0d, Layers: new Dictionary<string, WorldRenderSkyLayer> { ["haze"] = new WorldRenderSkyLayer.Fog(Density: 0.02f) })],
                Layers: [new WorldRenderSkyLayer.Fog(Density: 0.01f, Name: "haze")]
            )
            : new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: new BindableScalar(keys: track))]));

        return (Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Sky: sky),
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

        var authored = Assert.IsType<WorldRenderSkyLayer.Fog>(@object: definition.Render.Sky!.Layers![0]).Density!.Value.Keys!;
        var delivered = Assert.IsType<WorldRenderSkyLayer.Fog>(@object: hydrated!.Render.Sky!.Layers![0]).Density!.Value.Keys!;

        Assert.True(condition: WorldKeyResolver.TryClock(clock: out var clock, name: "day", timeline: hydrated.Timeline));

        foreach (var whole in new[] { 0UL, 1234567UL, (WorldClocks.PeriodTicks(clock: Day) / 3UL) }) {
            var tick = new PresentedTick(Fraction: 0.25d, Whole: whole);

            Assert.Equal(
                expected: WorldKeyResolver.Scalar(phase: WorldClocks.Phase(clock: Day, tick: tick), span: Day.Span, track: authored),
                actual: WorldKeyResolver.Scalar(phase: WorldClocks.Phase(clock: clock, tick: tick), span: clock.Span, track: delivered)
            );
        }
    }
    [InlineData(false, "render.sky.layers[0].density")]
    [InlineData(true, "render.sky")]
    [Theory]
    public void A_value_keyed_on_a_state_clock_refuses_to_hydrate_by_name(bool section, string path) {
        var projection = Project(definition: Keyed(clock: "mode", section: section));

        // The state clock never crosses: neither its name nor the row it reads.
        Assert.DoesNotContain(collection: projection.Timeline!.Clocks!, filter: static clock => clock.IsStateClock);
        Assert.False(condition: WorldProjection.TryToDefinition(definition: out _, projection: projection, reason: out var reason));
        Assert.Equal(
            actual: reason,
            expected: $"projection keys {path} on clock 'mode', which it does not carry: a state clock does not cross to a presentation-tier recipient, which receives no anchor of its phase."
        );
    }
}
