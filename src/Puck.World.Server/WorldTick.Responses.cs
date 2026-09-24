using Puck.Commands;
using Puck.Maths;
using Puck.Physics.Fields;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    // Per-placement memo for the response sweep's skip: the raw cell values (and whether each cell was present) a
    // placement's State entries read the last time it was fully evaluated, and the holding mask the row carried after
    // that sweep. Populated only for a placement whose every entry is a State condition — a Field entry has no
    // comparably cheap "did anything move" proof (the field lattice steps every tick regardless), so such a placement
    // is never entered here and is swept in full every tick. The mask catches a write the sweep did not make (an undo,
    // a console upsert), which moves the row without moving any cell.
    private readonly Dictionary<string, (long[] Values, bool[] Present, int Holding)> m_responseObservedValues = [];

    /// <summary>Describes every placement carrying a response trait: the prototype it shows, its authored one, and
    /// which entries the last sweep recorded as holding.</summary>
    internal string DescribeResponses() {
        var lines = new List<string>();
        var placements = Host.Document.Definition.Placements;

        for (var placementIndex = 0; (placementIndex < placements.Count); placementIndex++) {
            var placement = placements[placementIndex];

            if (placement.Respond is not { Count: > 0 } responses) {
                continue;
            }

            var holds = new List<string>();

            for (var index = 0; (index < responses.Count); index++) {
                if ((placement.Holding & (1 << index)) != 0) {
                    holds.Add(item: $"[{index}] {DescribeCondition(condition: responses[index].When)} -> {responses[index].PrototypeId}");
                }
            }

            lines.Add(item: $"'{placement.Id}' prototype={placement.ShownPrototypeId} authored={placement.PrototypeId} holds={((holds.Count == 0)
                ? "none"
                : string.Join(
                    separator: ", ",
                    values: holds
                ))}");
        }

        return ((lines.Count == 0)
            ? "[world.responses: none declared]"
            : $"[world.responses: {string.Join(
                separator: "; ",
                values: lines
            )}]"
        );
    }

    private static string DescribeCondition(WorldPlacementResponseCondition condition) => (condition switch {
        WorldPlacementResponseCondition.FieldCondition field => $"{field.Field} {field.Comparison}",
        WorldPlacementResponseCondition.StateCondition state => $"state:{state.State}{((state.Key is { } key)
        ? $".{key}"
        : string.Empty)} {state.Comparison}",
        _ => "?",
    });
    // Runs after StepFields, so a Field entry reads this tick's own lattice writes — unchanged from before this
    // facet gained a state arm. A State entry reads the installed document directly (WorldStateReader, the same
    // reader every other row-name comparand in this file already goes through), so it sees a rule's own write the
    // moment EvaluateWorldRules' end-of-tick fold installs it, and a field reaction's or the console's write the
    // moment IT installs, both still within this same tick's sweep.
    private void SweepPlacementResponses(ulong tick) {
        var lattice = Host.Population.Fields;
        // Declared once and reused every iteration below — a stackalloc inside the loop body would not release its
        // frame slot between iterations, growing with the placement count instead of staying constant.
        Span<long> values = stackalloc long[(WorldResponseCapacity.MaxEntries * 2)];
        Span<bool> present = stackalloc bool[(WorldResponseCapacity.MaxEntries * 2)];

        foreach (var placement in Host.Document.Definition.Placements) {
            if (placement.Respond is not { Count: > 0 } responses) {
                continue;
            }

            var skippable = TryReadResponseSnapshot(
                count: out var count,
                definition: Host.Document.Definition,
                present: present,
                responses: responses,
                tick: tick,
                engineTick: CompletedEngineTicks,
                values: values
            );

            if (
                skippable &&
                ObservedValuesUnchanged(
                holding: placement.Holding,
                placementId: placement.Id,
                values: values[..count],
                present: present[..count]
            )
            ) {
                continue;
            }

            var holding = ResolveHolding(
                lattice: lattice,
                placement: placement,
                responses: responses,
                tick: tick
            );
            var applied = (
                (holding == placement.Holding) ||
                Host.TryApplyMutation(
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                // Principal.World — the same structural-exemption door StampContribution/RetractContribution
                // use — so the write is journalled and undoable through the ordinary UpsertPlacement compose arm.
                mutation: new WorldMutation.UpsertPlacement(
                    Placement: (placement with { Holding = holding }),
                    Principal: Principal.World
                ),
                preMetered: false,
                tick: tick,
                engineTick: CompletedEngineTicks
            ));

            if (skippable) {
                m_responseObservedValues[placement.Id] = (values[..count].ToArray(), present[..count].ToArray(), (applied
                    ? holding
                    : placement.Holding));
            } else if (m_responseObservedValues.Count > 0) {
                m_responseObservedValues.Remove(key: placement.Id);
            }

            if (
                (holding == placement.Holding) ||
                !applied
            ) {
                continue;
            }

            if (Host.Output.HasNarrationSink) {
                var respondId = placement.Id;
                var respondShown = (placement with { Holding = holding });
                var respondPrevious = placement.ShownPrototypeId;
                var respondTarget = respondShown.ShownPrototypeId;
                var respondEntry = WorldPlacementResponse.ShownEntry(placement: respondShown);

                if (!string.Equals(
                    a: respondPrevious,
                    b: respondTarget,
                    comparisonType: StringComparison.Ordinal
                )) {
                    Host.Output.Narrate(
                        channel: "world.respond",
                        text: ((respondEntry >= 0)
                            ? $"[world.respond: '{respondId}' {respondPrevious} -> {respondTarget} (entry {respondEntry}: {DescribeCondition(condition: responses[respondEntry].When)})]"
                            : $"[world.respond: '{respondId}' {respondPrevious} -> {respondTarget} (no entry holds)]")
                    );
                }
            }
        }
    }
    // One bit per authored entry whose condition holds, bit i for entry i. A Field condition resolves the placement's
    // coupled cell once; a State condition needs no cell at all.
    private int ResolveHolding(FieldLattice? lattice, WorldPlacement placement, IReadOnlyList<WorldPlacementResponse> responses, ulong tick) {
        var cell = 0;
        var hasCell = ((lattice is not null) && lattice.TryBodyCellOf(
            position: FixedVector3.FromVector3(value: WorldDefinitionRows.ResolvedPosition(
                definition: Host.Document.Definition,
                placement: placement
            )),
            cell: out cell
        ));
        var holding = 0;

        for (var index = 0; (index < Math.Min(val1: responses.Count, val2: WorldResponseCapacity.MaxEntries)); index++) {
            var holds = (responses[index].When switch {
                WorldPlacementResponseCondition.FieldCondition field => (hasCell && FieldConditionHolds(
                cell: cell,
                condition: field,
                lattice: lattice!,
                tick: tick
            )),
                WorldPlacementResponseCondition.StateCondition state => state.Holds(
                definition: Host.Document.Definition,
                tick: tick,
                engineTick: CompletedEngineTicks
            ),
                _ => false,
            });

            if (holds) {
                holding |= (1 << index);
            }
        }

        return holding;
    }
    private bool FieldConditionHolds(WorldPlacementResponseCondition.FieldCondition condition, FieldLattice lattice, int cell, ulong tick) {
        if (!lattice.TryFieldIndex(
            name: condition.Field,
            field: out var field
        )) {
            return false;
        }

        var expected = ((condition.Value.Row is { } row)
            ? ReadScalarSlot(
                row: row,
                tick: tick
            )
            : FixedQ4816.FromDouble(value: (condition.Value.Literal ?? 0f))
        );

        return condition.Comparison.Holds(
            value: lattice.Value(
                cell: cell,
                field: field
            ),
            expected: expected
        );
    }
    // Reads every State entry's primary (and, when authored, comparand) cell straight off the installed document —
    // the snapshot the skip below compares tick to tick. Returns false (never skippable) the moment any entry is a
    // Field condition, so a mixed respond list is always swept in full.
    private static bool TryReadResponseSnapshot(WorldDefinition definition, IReadOnlyList<WorldPlacementResponse> responses, ulong tick, ulong engineTick, Span<long> values, Span<bool> present, out int count) {
        count = 0;

        for (var responseIndex = 0; (responseIndex < responses.Count); responseIndex++) {
            if (responses[responseIndex].When is not WorldPlacementResponseCondition.StateCondition state) {
                return false;
            }

            ReadResponseSnapshotCell(
                definition: definition,
                row: state.State,
                key: state.Key,
                tick: tick,
                engineTick: engineTick,
                values: values,
                present: present,
                index: count
            );
            count++;

            if (state.ComparandState is { } comparandRow) {
                ReadResponseSnapshotCell(
                    definition: definition,
                    row: comparandRow,
                    key: state.ComparandKey,
                    tick: tick,
                    engineTick: engineTick,
                    values: values,
                    present: present,
                    index: count
                );
                count++;
            }
        }

        return true;
    }
    private static void ReadResponseSnapshotCell(WorldDefinition definition, string row, string? key, ulong tick, ulong engineTick, Span<long> values, Span<bool> present, int index) {
        var found = (WorldStateReader.TryRead(
            definition: definition,
            engineTick: engineTick,
            key: key,
            rawValue: out var raw,
            row: out _,
            rowName: row,
            text: out _,
            tick: tick
        ) && (raw is not null));

        present[index] = found;
        values[index] = (raw ?? 0L);
    }
    private bool ObservedValuesUnchanged(string placementId, int holding, ReadOnlySpan<long> values, ReadOnlySpan<bool> present) {
        if (
            !m_responseObservedValues.TryGetValue(
            key: placementId,
            value: out var cached
        ) ||
            (cached.Holding != holding) ||
            (cached.Values.Length != values.Length)
        ) {
            return false;
        }

        return (
            cached.Values.AsSpan().SequenceEqual(other: values) &&
            cached.Present.AsSpan().SequenceEqual(other: present)
        );
    }
}
