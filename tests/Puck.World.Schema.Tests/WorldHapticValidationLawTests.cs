using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: the <c>haptics</c> section is optional, admits well-formed <c>Level</c>,
/// <c>Rise</c>, and <c>Change</c> rows (including <c>$body</c> keyed bindings), refuses duplicate ids,
/// out-of-range intensities/durations, mismatched <c>mode</c>/<c>signal</c>, non-numeric bindings, and unknown seats,
/// and round-trips through world projections.</summary>
public sealed class WorldHapticValidationLawTests {
    private static bool Validate(WorldDefinition definition, out string reason) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out reason
    );
    private static void Refuses(WorldDefinition definition, string needle) {
        var valid = Validate(definition: definition, reason: out var reason);

        Assert.False(condition: valid);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: needle
        );
    }

    [Fact]
    public void Absent_haptics_is_valid() {
        var definition = Fixtures.BuildDocument();

        Assert.Empty(collection: definition.Haptics);
        Assert.True(
            condition: Validate(definition: definition, reason: out var reason),
            userMessage: reason
        );
    }
    [Fact]
    public void Valid_rows_admit_and_round_trip_through_projection() {
        var definition = Fixtures.BuildDocument() with {
            HapticsRaw = [
                new WorldHapticRow(
                    Id: "hum",
                    Low: new BindableScalar(binding: "state.pulse"),
                    High: new BindableScalar(literal: 0.2f),
                    DurationSeconds: 0.1f,
                    Mode: WorldHapticMode.Level
                ),
                new WorldHapticRow(
                    Id: "beat",
                    Low: new BindableScalar(literal: 0.5f),
                    High: new BindableScalar(literal: 0.8f),
                    DurationSeconds: 0.08f,
                    Signal: new BindableScalar(binding: "state.trigger"),
                    Mode: WorldHapticMode.Rise,
                    Seat: "seat1"
                ),
                new WorldHapticRow(
                    Id: "note",
                    Low: new BindableScalar(literal: 0.3f),
                    High: new BindableScalar(literal: 0.6f),
                    DurationSeconds: 0.05f,
                    Signal: new BindableScalar(binding: "state.count.$body"),
                    Mode: WorldHapticMode.Change
                ),
            ],
        };
        definition = definition.WithWorldState(rows: [
            .. definition.State,
            new WorldStateRow(
                Name: CellName.Parse(candidate: "pulse"),
                Kind: CellKind.Fixed,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: 0.4d).Value))]
            ),
            new WorldStateRow(
                Name: CellName.Parse(candidate: "trigger"),
                Kind: CellKind.Int,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 0L))]
            ),
            new WorldStateRow(
                Name: CellName.Parse(candidate: "count"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    new StateCell(Key: CellName.Parse(candidate: "0"), Value: CellValue.Int(value: 0L)),
                    new StateCell(Key: CellName.Parse(candidate: "1"), Value: CellValue.Int(value: 0L)),
                ]
            ),
        ]);

        Assert.True(
            condition: Validate(definition: definition, reason: out var reason),
            userMessage: reason
        );

        var projection = Fixtures.Project(
            authority: "boot",
            definition: definition,
            revision: 0,
            tier: WorldDisclosureTier.Presentation
        );

        Assert.NotNull(@object: projection);
        Assert.True(
            condition: WorldProjection.TryDeserialize(
                projection: out var decoded,
                reason: out var deserializeReason,
                utf8Json: WorldProjection.Serialize(projection: projection!)
            ),
            userMessage: deserializeReason
        );
        Assert.True(
            condition: WorldProjection.TryToDefinition(
                definition: out var hydrated,
                projection: decoded!,
                reason: out var hydrateReason
            ),
            userMessage: hydrateReason
        );
        Assert.Equal(expected: definition.Haptics.Count, actual: hydrated!.Haptics.Count);
        Assert.Equal(expected: "beat", actual: hydrated.Haptics[1].Id);
        Assert.Equal(expected: WorldHapticMode.Rise, actual: hydrated.Haptics[1].Mode);
        Assert.Equal(expected: "seat1", actual: hydrated.Haptics[1].Seat);
        Assert.Equal(expected: "state.count.$body", actual: hydrated.Haptics[2].Signal?.Binding);
    }
    [Fact]
    public void Duplicate_ids_are_refused() {
        var row = new WorldHapticRow(Id: "beat", Low: 0.5f, High: 0f, DurationSeconds: 0.1f);
        var definition = Fixtures.BuildDocument() with { HapticsRaw = [row, row] };

        Refuses(definition: definition, needle: "haptics[1].id 'beat' is duplicated");
    }
    [Fact]
    public void Level_mode_refuses_signal_and_edge_modes_require_signal() {
        var levelWithSignal = Fixtures.BuildDocument() with {
            HapticsRaw = [
                new WorldHapticRow(
                    Id: "hum",
                    Low: 0.2f,
                    High: 0.2f,
                    DurationSeconds: 0.1f,
                    Signal: new BindableScalar(literal: 1f),
                    Mode: WorldHapticMode.Level
                ),
            ],
        };
        var riseWithoutSignal = Fixtures.BuildDocument() with {
            HapticsRaw = [
                new WorldHapticRow(
                    Id: "beat",
                    Low: 0.5f,
                    High: 0.5f,
                    DurationSeconds: 0.1f,
                    Mode: WorldHapticMode.Rise
                ),
            ],
        };

        Refuses(definition: levelWithSignal, needle: "haptics[0].signal cannot be authored when mode is 'Level'");
        Refuses(definition: riseWithoutSignal, needle: "haptics[0].signal is required when mode is 'Rise'");
    }
    [Fact]
    public void Invalid_duration_and_invalid_seat_are_refused() {
        var zeroDuration = Fixtures.BuildDocument() with {
            HapticsRaw = [new WorldHapticRow(Id: "a", Low: 0.5f, High: 0f, DurationSeconds: 0f)],
        };
        var excessiveDuration = Fixtures.BuildDocument() with {
            HapticsRaw = [new WorldHapticRow(Id: "a", Low: 0.5f, High: 0f, DurationSeconds: (WorldHapticCapacity.MaxDurationSeconds + 1f))],
        };
        var badSeat = Fixtures.BuildDocument() with {
            HapticsRaw = [new WorldHapticRow(Id: "a", Low: 0.5f, High: 0f, DurationSeconds: 0.1f, Seat: "seat9")],
        };

        Refuses(definition: zeroDuration, needle: "haptics[0].durationSeconds");
        Refuses(definition: excessiveDuration, needle: "haptics[0].durationSeconds");
        Refuses(definition: badSeat, needle: "haptics[0].seat 'seat9'");
    }
    [Fact]
    public void Intensities_outside_unit_range_are_refused() {
        var lowAboveOne = Fixtures.BuildDocument() with {
            HapticsRaw = [new WorldHapticRow(Id: "a", Low: 1.5f, High: 0f, DurationSeconds: 0.1f)],
        };
        var highNegative = Fixtures.BuildDocument() with {
            HapticsRaw = [new WorldHapticRow(Id: "a", Low: 0f, High: -0.1f, DurationSeconds: 0.1f)],
        };

        Refuses(definition: lowAboveOne, needle: "haptics[0].low");
        Refuses(definition: highNegative, needle: "haptics[0].high");
    }
    [Fact]
    public void Bool_state_binding_is_refused() {
        var definition = Fixtures.BuildDocument() with {
            HapticsRaw = [
                new WorldHapticRow(
                    Id: "beat",
                    Low: 0.5f,
                    High: 0f,
                    DurationSeconds: 0.1f,
                    Signal: new BindableScalar(binding: "state.flag"),
                    Mode: WorldHapticMode.Rise
                ),
            ],
        };

        definition = definition.WithWorldState(rows: [
            .. definition.State,
            new WorldStateRow(
                Name: CellName.Parse(candidate: "flag"),
                Kind: CellKind.Bool,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Bool(value: false))]
            ),
        ]);

        Refuses(definition: definition, needle: "haptics[0].signal");
    }
}