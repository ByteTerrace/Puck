using Puck.Maths;
using Puck.Physics.Fields;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    // Per-placement memo for the response sweep's skip: the raw cell values (and whether each cell was present) a
    // placement's State entries read the last time it was fully evaluated, and the holding mask the row carried after
    // that sweep. Populated only for a placement whose every entry is a State condition — a Field entry has no
    // comparably cheap "did anything move" proof (the field lattice steps every tick regardless), so such a placement
    // is never entered here and is swept in full every tick. The mask catches a write the sweep did not make (an undo,
    // a console upsert), which moves the row without moving any cell.
    private readonly Dictionary<string, (long[] Values, bool[] Present, int Holding)> m_responseObservedValues = [];
    // The sweep's scratch, reused every tick: each moved row with its index, its new holding and the snapshot that
    // admits a skip (null when the row cannot be skipped), and the selection the moves install.
    private readonly List<(WorldPlacement Placement, int Row, int Holding, long[]? Values, bool[]? Present)> m_responseMoves = [];
    private readonly List<(int Row, int Holding)> m_responseSelection = [];
    private readonly List<Responder> m_responders = [];
    private WorldDefinition? m_respondersOf;

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
    // Runs after StepFields, so a Field entry reads this tick's own lattice writes. A State entry reads the installed
    // document directly (WorldStateReader, the same reader every other row-name comparand in this file goes
    // through), so it sees a rule's own write the moment EvaluateWorldRules' end-of-tick fold installs it, and a field
    // reaction's or the console's write the moment it installs, both within this same tick's sweep. Every row whose
    // holding moved this tick lands in one selection install (WorldDocument.InstallResponseSelection), so a switch
    // that flips several rows replaces the live rows once and delivers them once.
    private void SweepPlacementResponses(ulong tick) {
        // Declared once and reused every iteration below — a stackalloc inside the loop body would not release its
        // frame slot between iterations, growing with the placement count instead of staying constant.
        Span<long> values = stackalloc long[(WorldResponseCapacity.MaxEntries * 2)];
        Span<bool> present = stackalloc bool[(WorldResponseCapacity.MaxEntries * 2)];

        m_responseMoves.Clear();

        var definition = Host.Document.Definition;
        var placements = definition.Placements;
        var arena = Host.Arena;

        IndexResponders(definition: definition);
        foreach (var responder in m_responders) {
            var placementIndex = responder.Index;
            var placement = placements[placementIndex];
            var responses = placement.Respond!;

            if (responder.RowsUnmoved(arena: arena, holding: placement.Holding)) {
                continue;
            }
            responder.Forget();

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
                responder.Observe(arena: arena, holding: placement.Holding);
                continue;
            }

            var holding = ResolveHolding(
                position: responder.Position,
                responses: responses,
                tick: tick
            );

            if (holding == placement.Holding) {
                if (skippable) {
                    // A changing comparand need not change the mask. Keep its memo storage across such ticks.
                    if (m_responseObservedValues.TryGetValue(key: placement.Id, value: out var cached) && (cached.Values.Length == count)) {
                        values[..count].CopyTo(destination: cached.Values);
                        present[..count].CopyTo(destination: cached.Present);
                        m_responseObservedValues[placement.Id] = (cached.Values, cached.Present, holding);
                    } else {
                        m_responseObservedValues[placement.Id] = (values[..count].ToArray(), present[..count].ToArray(), holding);
                    }
                    responder.Observe(arena: arena, holding: holding);
                } else if (m_responseObservedValues.Count > 0) {
                    m_responseObservedValues.Remove(key: placement.Id);
                }

                continue;
            }

            m_responseMoves.Add(item: (placement, placementIndex, holding, (skippable ? values[..count].ToArray() : null), (skippable ? present[..count].ToArray() : null)));
        }

        if (m_responseMoves.Count == 0) {
            return;
        }

        m_responseSelection.Clear();

        foreach (var move in m_responseMoves) {
            m_responseSelection.Add(item: (move.Row, move.Holding));
        }

        Host.Document.InstallResponseSelection(holdings: m_responseSelection);

        foreach (var (placement, _, holding, observed, observedPresent) in m_responseMoves) {
            if (observed is not null) {
                m_responseObservedValues[placement.Id] = (observed, observedPresent!, holding);
            } else if (m_responseObservedValues.Count > 0) {
                m_responseObservedValues.Remove(key: placement.Id);
            }

            var responses = placement.Respond!;

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
    // The placements carrying a response trait in the installed definition, in placement order, each with what the
    // sweep derives from the definition alone: its resolved position and the arena rows its State entries read.
    // Rebuilt when the definition is replaced; a response or deal install replaces it.
    private void IndexResponders(WorldDefinition definition) {
        if (ReferenceEquals(objA: m_respondersOf, objB: definition)) { return; }
        m_respondersOf = definition;
        m_responders.Clear();
        var placements = definition.Placements;

        for (var index = 0; (index < placements.Count); index++) {
            if (placements[index] is { Respond: { Count: > 0 } responses } placement) {
                m_responders.Add(item: new Responder(
                    index: index,
                    position: FixedVector3.FromVector3(value: WorldDefinitionRows.ResolvedPosition(definition: definition, placement: placement)),
                    rows: ResponderRows(responses: responses)
                ));
            }
        }
    }
    // The rows every State entry reads (its primary and its comparand), or null when an entry is a Field condition or
    // reads a row no arena version can vouch for: one that does not resolve, or one whose value moves with time.
    private static string[]? ResponderRows(IReadOnlyList<WorldPlacementResponse> responses) {
        var rows = new List<string>();

        foreach (var response in responses) {
            if (response.When is not WorldPlacementResponseCondition.StateCondition state) { return null; }
            rows.Add(item: state.State);
            if (state.ComparandState is { } comparand) { rows.Add(item: comparand); }
        }
        return rows.ToArray();
    }

    // One responding placement's derived inputs, and the arena row versions its last full evaluation read under the
    // holding it confirmed. While the same arena and catalog hold every one of those rows at the same version, no row
    // it reads moved, so the value snapshot that evaluation recorded still matches and the sweep skips it unread. A row
    // with a value-over-time trait never vouches: its read moves with the clock while its version stands still.
    private sealed class Responder(int index, FixedVector3 position, string[]? rows) {
        private readonly int[] m_ordinals = new int[(rows?.Length ?? 0)];
        private readonly ulong[] m_versions = new ulong[(rows?.Length ?? 0)];
        private StateArena? m_arena;
        private StateCatalog? m_catalog;
        private int m_holding;
        private bool m_observed;

        public int Index { get; } = index;
        public FixedVector3 Position { get; } = position;

        public bool RowsUnmoved(StateArena arena, int holding) {
            if (!m_observed || (m_holding != holding) || !ReferenceEquals(objA: m_arena, objB: arena) || !ReferenceEquals(objA: m_catalog, objB: arena.Catalog)) { return false; }
            for (var row = 0; (row < m_ordinals.Length); row++) {
                if (arena.RowVersion(rowOrdinal: m_ordinals[row]) != m_versions[row]) { return false; }
            }
            return true;
        }
        public void Forget() => m_observed = false;
        public void Observe(StateArena arena, int holding) {
            if (rows is null) { return; }
            for (var row = 0; (row < rows.Length); row++) {
                if (!arena.Catalog.TryResolve(lane: StateLane.Document, name: rows[row], handle: out var handle) || arena.Layout[handle.Ordinal].HasTraits) { return; }
                m_ordinals[row] = handle.Ordinal;
                m_versions[row] = arena.RowVersion(rowOrdinal: handle.Ordinal);
            }
            m_arena = arena;
            m_catalog = arena.Catalog;
            m_holding = holding;
            m_observed = true;
        }
    }

    // One bit per authored entry whose condition holds, bit i for entry i. A Field condition reads the cell coupled to
    // the placement's resolved position; a State condition needs no cell at all.
    private int ResolveHolding(FixedVector3 position, IReadOnlyList<WorldPlacementResponse> responses, ulong tick) {
        var holding = 0;

        for (var index = 0; (index < Math.Min(val1: responses.Count, val2: WorldResponseCapacity.MaxEntries)); index++) {
            var holds = responses[index].When switch {
                WorldPlacementResponseCondition.FieldCondition field => (Host.Population.TryFieldForRow(field.Field, out var region, out _) &&
                    region!.Lattice.TryBodyCellOf(cell: out var cell, position: position) && FieldConditionHolds(field, region.Lattice, cell, tick)),
                WorldPlacementResponseCondition.StateCondition state => state.Holds(arena: Host.Arena, definition: Host.Document.Definition, tick: tick, engineTick: CompletedEngineTicks),
                _ => false,
            };

            if (holds) { holding |= (1 << index); }
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
    private bool TryReadResponseSnapshot(WorldDefinition definition, IReadOnlyList<WorldPlacementResponse> responses, ulong tick, ulong engineTick, Span<long> values, Span<bool> present, out int count) {
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
    private void ReadResponseSnapshotCell(WorldDefinition definition, string row, string? key, ulong tick, ulong engineTick, Span<long> values, Span<bool> present, int index) {
        var found = (WorldStateReader.TryRead(arena: Host.Arena,
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
