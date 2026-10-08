
namespace Puck.World.Testing;

/// <summary>The world the timeline lever laws present.</summary>
internal static class TimelineLeverFixtures {
    internal static WorldDefinition Definition() => (Fixtures.BuildDocument() with {
        TimelineRaw = new WorldTimelineSection(Clocks: [
            new WorldClock("day", PeriodSeconds: 1d),
            new WorldClock("tide", State: "phase"),
        ]),
        RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Noise() { Clock = "tide" }]), Atmosphere: new WorldRenderAtmosphere(
            Fog: new WorldRenderFog(Density: new BindableScalar(keys: new WorldKeyTrack<float>(clock: "tide", keys: [
                new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0f),
                new WorldKey<float>(At: 0.5d, Ease: WorldEase.Linear, Value: 0.1f),
            ])))
        )),
    }).WithWorldState([new WorldStateRow(
        Name: CellName.Parse(candidate: "phase"), Kind: CellKind.Fixed,
        Advance: new StateAdvance(PerSecondDenominator: 1L, PerSecondNumerator: 1L),
        Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: 0L))])]);
}
