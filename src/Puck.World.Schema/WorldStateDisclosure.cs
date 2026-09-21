using System.Text.Json.Serialization;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>A disclosed literal cell, or, under <see cref="HiddenCells.Placeholder"/>, an anonymous card back
/// (<see cref="Hidden"/> true, empty key, zero value, no text, no observation).</summary>
public sealed record WorldObservedCell(string Key, long Value, string? Text = null, StateObservation? Observation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hidden = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateVector? Vector = null);
/// <summary>A presentation observation, without draw seeds, cursors, masks, grants, or executable traits.
/// <see cref="HiddenCount"/> counts the cells the row's <see cref="StateVisibility.Hidden"/> policy withheld
/// from this observer (placeholders included), zero under <see cref="HiddenCells.Omit"/>.</summary>
public sealed record WorldObservedRow(string Name, CellKind Kind, IReadOnlyList<WorldObservedCell> Cells,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int HiddenCount = 0);
/// <summary>Composes state observations for one authenticated recipient.</summary>
/// <remarks>Every value, audience, zone membership and observation stamp is read from the live store, never from a
/// document snapshot taken per observer: which zone holds a token decides who may read it, so a disclosure composed
/// against a stale copy would answer for a table that has already moved.</remarks>
public static class WorldStateDisclosure {
    /// <summary>Determines whether the recipient may read one cell, including its containing zone's policy.</summary>
    /// <param name="definition">The live document, for the row declarations.</param>
    /// <param name="arena">The live store.</param>
    /// <param name="row">The row carrying the cell.</param>
    /// <param name="key">The cell's key.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <returns><see langword="true"/> when the recipient may read the cell.</returns>
    public static bool CanRead(WorldDefinition definition, StateArena arena, WorldStateRow row, CellName key, WorldPrincipal? recipient) {
        var observer = new Observer(
            arena: arena,
            definition: definition,
            recipient: recipient
        );

        return (!observer.TryRow(
            ordinal: out var ordinal,
            row: row
        ) || observer.CanRead(
            key: (arena.Keys.TryResolve(
                key: out var interned,
                name: key
            )
                ? interned
                : default),
            row: row,
            rowOrdinal: ordinal
        ));
    }
    /// <summary>Projects only rows and cells with explicit observation policies; token attributes inherit their
    /// zone's restrictions. Discloses stored truth for cells carrying dynamics (the arena never eases), while
    /// value-over-time traits such as advance and cycle evaluate live.</summary>
    /// <param name="definition">The live document, for the row declarations.</param>
    /// <param name="arena">The live store.</param>
    /// <param name="time">The clocks a cell's value-over-time trait is read at.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <returns>The observed rows, or <see langword="null"/> when the document discloses nothing.</returns>
    public static IReadOnlyList<WorldObservedRow>? Compose(WorldDefinition definition, StateArena arena, in ArenaTime time, WorldPrincipal? recipient) {
        var observer = new Observer(
            arena: arena,
            definition: definition,
            recipient: recipient
        );
        var result = new List<WorldObservedRow>();

        foreach (var row in definition.State) {
            if (!observer.TryRow(
                ordinal: out var rowOrdinal,
                row: row
            )) {
                continue;
            }

            if (
                (row.Visibility is null) &&
                !observer.AnyCellRestricted(
                rowOrdinal: rowOrdinal
            )
            ) {
                continue;
            }
            if (
                (row.Visibility is { } policy) &&
                !observer.Allows(policy: policy)
            ) {
                continue;
            }

            var cells = new List<WorldObservedCell>();
            var hidden = 0;
            var hiddenPolicy = (row.Visibility?.Hidden ?? HiddenCells.Omit);
            var cursor = 0;

            while (arena.TryNextCell(
                cursor: ref cursor,
                key: out var key,
                rowOrdinal: rowOrdinal
            )) {
                if (!arena.TryReadLive(
                    key: key,
                    rowOrdinal: rowOrdinal,
                    time: in time,
                    value: out var value
                )) {
                    continue;
                }
                if (observer.CanRead(
                    key: key,
                    row: row,
                    rowOrdinal: rowOrdinal
                )) {
                    cells.Add(item: new(
                        arena.Keys[key: key].Value,
                        (value.Kind switch {
                            CellKind.Bool => (value.AsBool
                                ? 1L
                                : 0L),
                            CellKind.Fixed => value.AsFixed,
                            CellKind.Int => value.AsInt,
                            _ => 0L,
                        }),
                        ((value.Kind == CellKind.Text)
                            ? value.AsText
                            : null),
                        arena.Observation(
                            key: key,
                            rowOrdinal: rowOrdinal
                        ),
                        Hidden: false,
                        Vector: Vector(
                            arena: arena,
                            key: key,
                            rowOrdinal: rowOrdinal,
                            value: value
                        )
                    ));

                    continue;
                }
                if (hiddenPolicy == HiddenCells.Omit) {
                    continue;
                }

                hidden++;
                if (hiddenPolicy == HiddenCells.Placeholder) {
                    cells.Add(item: new(
                        string.Empty,
                        0L,
                        Hidden: true
                    ));
                }
            }
            result.Add(item: new(
                row.Name.Value,
                row.Kind,
                cells,
                hidden
            ));
        }

        return ((result.Count == 0)
            ? null
            : result
        );
    }
    /// <summary>Refuses flattening a presentation binding that could disclose a restricted value.</summary>
    /// <param name="definition">The live document, for the row declarations.</param>
    /// <param name="arena">The live store.</param>
    /// <param name="graph">The presentation graph to flatten.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <exception cref="InvalidOperationException">The graph references a row this recipient may not read whole.</exception>
    public static void ValidateBindings(WorldDefinition definition, StateArena arena, object graph, WorldPrincipal? recipient) {
        var observer = new Observer(
            arena: arena,
            definition: definition,
            recipient: recipient
        );

        foreach (var row in definition.State) {
            if (!observer.TryRow(
                ordinal: out var rowOrdinal,
                row: row
            )) {
                continue;
            }
            if (
                !observer.AnyCellWithheld(row: row, rowOrdinal: rowOrdinal) &&
                ((row.Visibility is null) || observer.Allows(policy: row.Visibility))
            ) {
                continue;
            }
            if (WorldStateDocumentValues.ReferencesRow(
                definition: definition,
                graph: graph,
                rowName: row.Name.Value
            )) {
                throw new InvalidOperationException(message: "a presentation binding references restricted state; bind an explicit observation layer instead");
            }
        }
    }

    private static StateVector? Vector(StateArena arena, int rowOrdinal, CellKey key, in CellValue value) {
        if (
            (value.Kind != CellKind.Vector) ||
            !arena.TryReadVector(
            components: out var components,
            key: key,
            rowOrdinal: rowOrdinal
        ) ||
            !StateVector.TryCreate(
            components: components,
            error: out _,
            vector: out var vector
        )
        ) {
            return null;
        }

        return vector;
    }

    // One recipient's view of one live store: the canonical token is formatted once, and the ordered zones are
    // indexed by token domain once, so a cell's read check costs the zones over its own domain and nothing else.
    private readonly struct Observer {
        private readonly Dictionary<string, List<(int Ordinal, WorldStateRow Row)>> m_zonesByDomain;
        private readonly StateArena m_arena;
        private readonly WorldDefinition m_definition;

        public Observer(WorldDefinition definition, StateArena arena, WorldPrincipal? recipient) {
            m_arena = arena;
            m_definition = definition;
            Name = recipient?.Describe();
            m_zonesByDomain = new(comparer: StringComparer.Ordinal);

            foreach (var row in definition.State) {
                if (row.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zoneDomain) {
                    continue;
                }
                if (!TryRow(
                    ordinal: out var ordinal,
                    row: row
                )) {
                    continue;
                }

                var domain = zoneDomain.Row.Value;

                if (!m_zonesByDomain.TryGetValue(
                    key: domain,
                    value: out var zones
                )) {
                    zones = [];
                    m_zonesByDomain[domain] = zones;
                }

                zones.Add(item: (ordinal, row));
            }
        }

        public string? Name { get; }

        // A restriction the store holds for the recipient's own token, or one a live text row lists it in.
        public bool Allows(StateVisibility? policy) {
            if (policy is null) {
                return true;
            }
            if (policy.Allows(recipient: Name)) {
                return true;
            }
            if (
                (Name is null) ||
                (policy.ReadersFrom is null) ||
                !m_definition.StateCatalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: policy.ReadersFrom
            )
            ) {
                return false;
            }

            var readers = handle.Ordinal;
            var cursor = 0;

            while (m_arena.TryNextCell(
                cursor: ref cursor,
                key: out var key,
                rowOrdinal: readers
            )) {
                if (
                    m_arena.TryRead(
                    key: key,
                    rowOrdinal: readers,
                    value: out var value
                ) &&
                    (value.Kind == CellKind.Text) &&
                    string.Equals(
                    a: value.AsText,
                    b: Name,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    return true;
                }
            }

            return false;
        }
        public bool AnyCellRestricted(int rowOrdinal) {
            var cursor = 0;

            while (m_arena.TryNextCell(
                cursor: ref cursor,
                key: out var key,
                rowOrdinal: rowOrdinal
            )) {
                if (m_arena.Visibility(
                    key: key,
                    rowOrdinal: rowOrdinal
                ) is not null) {
                    return true;
                }
            }

            return false;
        }
        public bool AnyCellWithheld(WorldStateRow row, int rowOrdinal) {
            var cursor = 0;

            while (m_arena.TryNextCell(
                cursor: ref cursor,
                key: out var key,
                rowOrdinal: rowOrdinal
            )) {
                if (!CanRead(
                    key: key,
                    row: row,
                    rowOrdinal: rowOrdinal
                )) {
                    return true;
                }
            }

            return false;
        }
        public bool CanRead(WorldStateRow row, int rowOrdinal, CellKey key) {
            if (
                !Allows(policy: row.Visibility) ||
                !Allows(policy: m_arena.Visibility(
                key: key,
                rowOrdinal: rowOrdinal
            ))
            ) {
                return false;
            }

            var domain = ((row.EffectiveDomain is StateDomain.KeysOf keysOf)
                ? keysOf.Row.Value
                : row.Name.Value
            );

            if (
                (domain is null) ||
                !m_zonesByDomain.TryGetValue(
                key: domain,
                value: out var zones
            )
            ) {
                return true;
            }

            // A token's audience is its zone's: whichever ordered zone over this domain currently holds the key
            // decides, so this asks the store which one that is rather than a document's last export.
            foreach (var (zoneOrdinal, zone) in zones) {
                if (!m_arena.TryCellSlot(
                    key: key,
                    rowOrdinal: zoneOrdinal,
                    slot: out _
                )) {
                    continue;
                }
                if (
                    !Allows(policy: zone.Visibility) ||
                    !Allows(policy: m_arena.Visibility(
                    key: key,
                    rowOrdinal: zoneOrdinal
                ))
                ) {
                    return false;
                }
            }

            return true;
        }
        public bool TryRow(WorldStateRow row, out int ordinal) {
            if (m_definition.StateCatalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: row.Name
            )) {
                ordinal = handle.Ordinal;

                return true;
            }

            ordinal = -1;

            return false;
        }
    }
}
