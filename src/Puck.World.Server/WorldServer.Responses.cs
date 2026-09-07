using Puck.Maths;
using Puck.Physics.Fields;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Per-placement memo for the response sweep's skip: the raw cell values (and whether each cell was present) a
    // placement's State entries read the last time it was fully evaluated. Populated only for a placement whose
    // every entry is a State condition — a Field entry has no comparably cheap "did anything move" proof (the field
    // lattice steps every tick regardless), so such a placement is never entered here and is swept in full every
    // tick exactly as before this facet gained a state arm.
    private readonly Dictionary<string, (long[] Values, bool[] Present)> m_responseObservedValues = [];

    /// <summary>Describes every placement carrying a response trait: its current prototype, and which authored
    /// condition (if any) currently holds.</summary>
    public string DescribeResponses() {
        var lattice = m_population.Fields;
        var lines = new List<string>();

        var placements = m_definition.Placements;

        for (var placementIndex = 0; (placementIndex < placements.Count); placementIndex++) {
            var placement = placements[placementIndex];

            if (placement.Respond is not { Count: > 0 } responses) {
                continue;
            }

            var matchedIndex = ResolveMatchingResponse(lattice: lattice, placement: placement, responses: responses, tick: m_lastCompletedTick);

            lines.Add(item: ((matchedIndex >= 0)
                ? $"'{placement.Id}' prototype={placement.PrototypeId} holds=[{matchedIndex}] {DescribeCondition(condition: responses[matchedIndex].When)} -> {responses[matchedIndex].PrototypeId}"
                : $"'{placement.Id}' prototype={placement.PrototypeId} holds=none"
            ));
        }

        return ((lines.Count == 0)
            ? "[world.responses: none declared]"
            : $"[world.responses: {string.Join(separator: "; ", values: lines)}]"
        );
    }
    private static string DescribeCondition(WorldPlacementResponseCondition condition) => (condition switch {
        WorldPlacementResponseCondition.FieldCondition field => $"{field.Field} {field.Comparison}",
        WorldPlacementResponseCondition.StateCondition state => $"state:{state.State}{((state.Key is { } key) ? $".{key}" : string.Empty)} {state.Comparison}",
        _ => "?",
    });

    // Runs after StepFields, so a Field entry reads this tick's own lattice writes — unchanged from before this
    // facet gained a state arm. A State entry reads the installed document directly (WorldStateReader, the same
    // reader every other row-name comparand in this file already goes through), so it sees a rule's own write the
    // moment EvaluateWorldRules' end-of-tick fold installs it, and a field reaction's or the console's write the
    // moment IT installs, both still within this same tick's sweep.
    private void SweepPlacementResponses(ulong tick) {
        var lattice = m_population.Fields;
        // Declared once and reused every iteration below — a stackalloc inside the loop body would not release its
        // frame slot between iterations, growing with the placement count instead of staying constant.
        Span<long> values = stackalloc long[(WorldResponseCapacity.MaxEntries * 2)];
        Span<bool> present = stackalloc bool[(WorldResponseCapacity.MaxEntries * 2)];

        foreach (var placement in m_definition.Placements) {
            if (placement.Respond is not { Count: > 0 } responses) {
                continue;
            }

            var skippable = TryReadResponseSnapshot(definition: m_definition, responses: responses, tick: tick, values: values, present: present, count: out var count);

            if (skippable && ObservedValuesUnchanged(placementId: placement.Id, values: values[..count], present: present[..count])) {
                continue;
            }

            if (skippable) {
                m_responseObservedValues[placement.Id] = (values[..count].ToArray(), present[..count].ToArray());
            } else if (m_responseObservedValues.Count > 0) {
                m_responseObservedValues.Remove(key: placement.Id);
            }

            var matchedIndex = ResolveMatchingResponse(lattice: lattice, placement: placement, responses: responses, tick: tick);

            if (matchedIndex < 0) {
                continue;
            }

            var target = responses[matchedIndex].PrototypeId;

            if (string.Equals(
                a: placement.PrototypeId,
                b: target,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            var previous = placement.PrototypeId;

            // WorldPrincipal.World — the same structural-exemption door StampContribution/RetractContribution use —
            // so the swap is journalled and undoable through the ordinary UpsertPlacement compose arm.
            if (!TryApplyMutation(
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                mutation: new WorldMutation.UpsertPlacement(
                    Placement: (placement with { PrototypeId = target }),
                    Principal: WorldPrincipal.World
                ),
                preMetered: false,
                tick: tick
            )) {
                continue;
            }

            // A closure that captures this loop's own locals would be hoisted into a display class allocated on
            // every iteration reaching this far — regardless of whether a sink is attached — because the compiler
            // must ready that storage before the earlier writes to previous/target/matchedIndex above. Gating on
            // HasNarrationSink first, and re-binding what the line needs into locals scoped to this block alone,
            // keeps the format closure (and its allocation) inside the one branch that ever runs it.
            if (m_output.HasNarrationSink) {
                var respondId = placement.Id;
                var respondPrevious = previous;
                var respondTarget = target;
                var respondEntry = matchedIndex;
                var respondDescribe = DescribeCondition(condition: responses[matchedIndex].When);

                m_output.Narrate(
                    channel: "world.respond",
                    text: $"[world.respond: '{respondId}' {respondPrevious} -> {respondTarget} (entry {respondEntry}: {respondDescribe})]"
                );
            }
        }
    }
    // The first authored entry whose condition holds, or -1 when none do. A Field condition resolves the
    // placement's coupled cell once (unchanged from before this facet gained a state arm); a State condition needs
    // no cell at all.
    private int ResolveMatchingResponse(FieldLattice? lattice, WorldPlacement placement, IReadOnlyList<WorldPlacementResponse> responses, ulong tick) {
        var cell = 0;
        var hasCell = ((lattice is not null) && lattice.TryBodyCellOf(
            position: FixedVector3.FromVector3(value: WorldDefinitionRows.ResolvedPosition(definition: m_definition, placement: placement)),
            cell: out cell
        ));

        for (var index = 0; (index < responses.Count); index++) {
            var holds = (responses[index].When switch {
                WorldPlacementResponseCondition.FieldCondition field => (hasCell && FieldConditionHolds(condition: field, lattice: lattice!, cell: cell, tick: tick)),
                WorldPlacementResponseCondition.StateCondition state => StateConditionHolds(condition: state, definition: m_definition, tick: tick),
                _ => false,
            });

            if (holds) {
                return index;
            }
        }

        return -1;
    }
    private bool FieldConditionHolds(WorldPlacementResponseCondition.FieldCondition condition, FieldLattice lattice, int cell, ulong tick) {
        if (!lattice.TryFieldIndex(name: condition.Field, field: out var field)) {
            return false;
        }

        var expected = ((condition.Value.Row is { } row)
            ? ReadScalarSlot(row: row, tick: tick)
            : FixedQ4816.FromDouble(value: (condition.Value.Literal ?? 0f))
        );

        return condition.Comparison.Holds(
            value: lattice.Value(field: field, cell: cell),
            expected: expected
        );
    }
    private static bool StateConditionHolds(WorldPlacementResponseCondition.StateCondition condition, WorldDefinition definition, ulong tick) {
        if (!WorldStateReader.TryRead(definition: definition, rowName: condition.State, key: condition.Key, tick: tick, row: out var row, rawValue: out var raw, text: out _) || (raw is not { } rawValue)) {
            return false;
        }

        long expected;

        if (condition.ComparandState is { } comparandRow) {
            if (!WorldStateReader.TryRead(definition: definition, rowName: comparandRow, key: condition.ComparandKey, tick: tick, row: out _, rawValue: out var comparand, text: out _) || (comparand is not { } comparandValue)) {
                return false;
            }

            expected = comparandValue;
        } else {
            expected = LiteralToRaw(kind: row.Kind, literal: (condition.Value ?? 0f));
        }

        return condition.Comparison.Holds(
            value: FixedQ4816.FromRawBits(value: rawValue),
            expected: FixedQ4816.FromRawBits(value: expected)
        );
    }
    // A Fixed row's literal keeps its exact fixed-point scale; an Int/Bool row's literal rounds to the nearest whole
    // number — the raw encoding StateCellWriter.TryParseNumericToken already gives every other author-typed literal
    // of that kind, so a state condition's comparand reads the same way a console cell edit would.
    private static long LiteralToRaw(CellKind kind, float literal) => (kind switch {
        CellKind.Fixed => FixedQ4816.FromDouble(value: literal).Value,
        _ => ((long)MathF.Round(x: literal, mode: MidpointRounding.ToEven)),
    });
    // Reads every State entry's primary (and, when authored, comparand) cell straight off the installed document —
    // the snapshot the skip below compares tick to tick. Returns false (never skippable) the moment any entry is a
    // Field condition, so a mixed respond list is always swept in full.
    private static bool TryReadResponseSnapshot(WorldDefinition definition, IReadOnlyList<WorldPlacementResponse> responses, ulong tick, Span<long> values, Span<bool> present, out int count) {
        count = 0;

        for (var responseIndex = 0; (responseIndex < responses.Count); responseIndex++) {
            if (responses[responseIndex].When is not WorldPlacementResponseCondition.StateCondition state) {
                return false;
            }

            ReadResponseSnapshotCell(definition: definition, row: state.State, key: state.Key, tick: tick, values: values, present: present, index: count);
            count++;

            if (state.ComparandState is { } comparandRow) {
                ReadResponseSnapshotCell(definition: definition, row: comparandRow, key: state.ComparandKey, tick: tick, values: values, present: present, index: count);
                count++;
            }
        }

        return true;
    }
    private static void ReadResponseSnapshotCell(WorldDefinition definition, string row, string? key, ulong tick, Span<long> values, Span<bool> present, int index) {
        var found = (WorldStateReader.TryRead(definition: definition, rowName: row, key: key, tick: tick, row: out _, rawValue: out var raw, text: out _) && (raw is not null));

        present[index] = found;
        values[index] = (raw ?? 0L);
    }
    private bool ObservedValuesUnchanged(string placementId, ReadOnlySpan<long> values, ReadOnlySpan<bool> present) {
        if (!m_responseObservedValues.TryGetValue(key: placementId, value: out var cached) || (cached.Values.Length != values.Length)) {
            return false;
        }

        return (cached.Values.AsSpan().SequenceEqual(other: values) && cached.Present.AsSpan().SequenceEqual(other: present));
    }
}
