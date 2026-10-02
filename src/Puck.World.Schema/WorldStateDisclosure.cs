using Puck.Commands;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A disclosed cell, or, under <see cref="HiddenCells.Placeholder"/>, an anonymous card back
/// (<see cref="Hidden"/> true, empty key, zero value, no text, no observation).
/// <para>A cell under a value-over-time trait carries its stored value as <see cref="Value"/> beside the one trait
/// that governs it (<see cref="Advance"/>, <see cref="Cycle"/> or <see cref="Dynamics"/>) and the <see cref="Clock"/>
/// that trait reads, exactly as the cell is stored: an advancing cell its base and epoch engine tick, a cycling cell
/// its phase, epoch tick and substep remainder, and an eased cell its target, which a <c>.$target</c> read answers,
/// with the epoch tick and the follower's position and velocity. A reader evaluates the trait at the tick it presents
/// with the engine's own fixed-point computation, so it presents what the authority presents at every tick, and
/// nothing is sent while the value only moves as its trait says.</para></summary>
/// <param name="Key">The cell's key.</param>
/// <param name="Value">The stored value: the base an advance or a cycle turns from, or the target an eased cell
/// follows.</param>
/// <param name="Text">The text of a <see cref="CellKind.Text"/> cell.</param>
/// <param name="Observation">The cell's last-seen stamp.</param>
/// <param name="Hidden">Whether the cell is an anonymous placeholder.</param>
/// <param name="Vector">The vector of a <see cref="CellKind.Vector"/> cell.</param>
/// <param name="Advance">The accumulation an advancing cell moves by, or <see langword="null"/>.</param>
/// <param name="Cycle">The rotation a cycling cell turns by, or <see langword="null"/>.</param>
/// <param name="Dynamics">The follower an eased cell eases by, or <see langword="null"/>.</param>
/// <param name="Clock">The clock the cell's trait reads, or <see langword="null"/> for a trait that has never settled
/// anywhere but tick zero (a follower at rest on its target).</param>
public sealed record WorldObservedCell(string Key, long Value, string? Text = null, StateObservation? Observation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hidden = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateVector? Vector = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateAdvance? Advance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateCycle? Cycle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateDynamics? Dynamics = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateCellClock? Clock = null);
/// <summary>A presentation observation, without draw seeds, cursors, masks or grants: each cell as it is stored,
/// with the value-over-time trait that governs it (<see cref="WorldObservedCell"/>).
/// <see cref="HiddenCount"/> counts the cells the row's <see cref="StateVisibility.Hidden"/> policy withheld
/// from this observer (placeholders included), zero under <see cref="HiddenCells.Omit"/>. <see cref="Space"/> names
/// a <see cref="CellKind.Vector"/> row's vector space, and is absent for every other kind. <see cref="Min"/> and
/// <see cref="Max"/> are the row's declared envelope, which an eased read is clamped to and a bound value is read
/// against.</summary>
public sealed record WorldObservedRow(string Name, CellKind Kind, IReadOnlyList<WorldObservedCell> Cells,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int HiddenCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Space = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Min = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Max = null);
/// <summary>What one recipient's disclosure withheld from a single state row.</summary>
/// <param name="Count">The number of the row's published cells the recipient may not read.</param>
/// <param name="Policy">What the row's own policy shows a reader in place of a withheld cell.</param>
/// <param name="WholeRow">Whether the row's own policy excludes the recipient, withholding every cell it holds or
/// may later hold.</param>
public sealed record WorldWithheldRow(int Count, HiddenCells Policy, bool WholeRow);
/// <summary>The live document as one recipient may read it.</summary>
/// <param name="Definition">The document with every withheld cell removed from its row and every dealt placement
/// re-dealt from what remains; the live document itself when nothing is withheld.</param>
/// <param name="Withheld">What was withheld, keyed by row name; a row absent here is disclosed whole.</param>
public sealed record WorldStateDisclosed(WorldDefinition Definition, IReadOnlyDictionary<string, WorldWithheldRow> Withheld);
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
    public static bool CanRead(WorldDefinition definition, StateArena arena, WorldStateRow row, CellName key, Principal? recipient) {
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
    /// <summary>Projects the rows and cells with explicit observation policies, and every row in
    /// <paramref name="presented"/> whether or not it has one, each under the same per-cell rule; token attributes
    /// inherit their zone's restrictions. Discloses each cell as it is stored, with the value-over-time trait that
    /// governs it and the clock that trait reads (<see cref="WorldObservedCell"/>), so the observation holds at every
    /// tick until a write moves it.</summary>
    /// <param name="definition">The live document, for the row declarations.</param>
    /// <param name="arena">The live store.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <returns>The observed rows, or <see langword="null"/> when the document discloses nothing.</returns>
    /// <param name="unrestricted">Whether to disclose as a reader every restriction admits — the most any recipient could
    /// be handed, which a measurement sizing for every possible recipient reads — instead of as <paramref name="recipient"/>.</param>
    /// <param name="presented">The rows a presentation reads, which cross as observations of the cells the recipient
    /// may read whether or not they declare a policy, or <see langword="null"/> for none.</param>
    public static IReadOnlyList<WorldObservedRow>? Compose(WorldDefinition definition, StateArena arena, Principal? recipient, bool unrestricted = false, IReadOnlySet<string>? presented = null) {
        var observer = new Observer(
            arena: arena,
            definition: definition,
            recipient: recipient,
            unrestricted: unrestricted
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
            ) &&
                (presented?.Contains(item: row.Name.Value) != true)
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
                if (!arena.TryRead(
                    key: key,
                    rowOrdinal: rowOrdinal,
                    value: out var value
                )) {
                    continue;
                }
                if (observer.CanRead(
                    key: key,
                    row: row,
                    rowOrdinal: rowOrdinal
                )) {
                    var behavior = arena.LiveBehavior(
                        key: key,
                        rowOrdinal: rowOrdinal
                    );

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
                        ),
                        Advance: behavior.Advance,
                        Cycle: behavior.Cycle,
                        Dynamics: behavior.Dynamics,
                        Clock: (behavior.IsNone
                            ? null
                            : TraitClock(
                                arena: arena,
                                key: key,
                                rowOrdinal: rowOrdinal
                            ))
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
                hidden,
                ((row.Kind == CellKind.Vector)
                    ? row.Space
                    : null),
                row.Min,
                row.Max
            ));
        }

        return ((result.Count == 0)
            ? null
            : result
        );
    }
    /// <summary>Returns the live document as one recipient may read it: every authored state row keeps its
    /// declaration, and each keeps only the cells the recipient may read, with what was withheld recorded by row
    /// name. Pool storage carries no visibility and is left as it stands.
    /// <para>A dealt placement (<see cref="WorldPlacementDeal"/>) is a reading of the cells it was dealt from, so it
    /// is disclosed as the deal would deal it from the recipient's rows: a child whose dealt cell is withheld is absent,
    /// and a child whose variant cell is withheld shows what the disclosed variant row selects — the template's own
    /// prototype, the deal's documented choice for a key with no variant cell. A responsive placement
    /// (<see cref="WorldPlacement.Respond"/>) whose facet reads a row with a withheld cell is a reading of those cells
    /// too, so it loses the holding bit of every entry that reads a withheld cell and shows the first entry left, or its
    /// authored prototype (<see cref="WorldPlacementResponse.Disclosed"/>). Every other placement is left as it
    /// stands.</para></summary>
    /// <param name="definition">The live document, for the row declarations and their published cells.</param>
    /// <param name="arena">The live store, for each cell's own policy and its zone's.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <returns>The disclosed document; <paramref name="definition"/> itself when nothing is withheld.</returns>
    /// <param name="unrestricted">Whether to disclose as a reader every restriction admits — the most any recipient could
    /// be handed, which a measurement sizing for every possible recipient reads — instead of as <paramref name="recipient"/>.</param>
    public static WorldStateDisclosed Disclose(WorldDefinition definition, StateArena arena, Principal? recipient, bool unrestricted = false) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: arena);

        var observer = new Observer(
            arena: arena,
            definition: definition,
            recipient: recipient,
            unrestricted: unrestricted
        );
        var authored = definition.AuthoredState;
        var rows = new WorldStateRow[authored.Count];
        var withheld = new Dictionary<string, WorldWithheldRow>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < authored.Count); index++) {
            var row = authored[index];

            rows[index] = row;
            if (!observer.TryRow(
                ordinal: out var ordinal,
                row: row
            )) {
                continue;
            }

            var rowReadable = observer.Allows(policy: row.Visibility);
            var cells = (row.Cells ?? []);
            var kept = new List<StateCell>(capacity: cells.Count);

            foreach (var cell in cells) {
                if (
                    rowReadable &&
                    observer.CanRead(
                    key: (arena.Keys.TryResolve(
                        key: out var interned,
                        name: cell.Key
                    )
                        ? interned
                        : default),
                    row: row,
                    rowOrdinal: ordinal
                )
                ) {
                    kept.Add(item: cell);
                }
            }

            if (
                rowReadable &&
                (kept.Count == cells.Count) &&
                !observer.AnyCellWithheld(
                row: row,
                rowOrdinal: ordinal
            )
            ) {
                continue;
            }

            withheld[row.Name.Value] = new WorldWithheldRow(
                Count: (cells.Count - kept.Count),
                Policy: (row.Visibility?.Hidden ?? HiddenCells.Omit),
                WholeRow: !rowReadable
            );
            // The row's own draw and ring bookkeeping is a reading of the cells withheld from it, so it goes too.
            rows[index] = (row with {
                Cells = kept,
                DrawCursor = 0L,
                DrawnMasks = null,
                HistoryCursor = 0L,
            });
        }

        if (withheld.Count == 0) {
            return new WorldStateDisclosed(
                Definition: definition,
                Withheld: withheld
            );
        }

        var disclosed = (definition with { StateRaw = (definition.StateRaw! with { World = rows }) });

        if (DisclosePlacements(
            definition: definition,
            rows: rows,
            withheld: withheld
        ) is { } placements) {
            disclosed = (disclosed with { PlacementRowsRaw = placements });
        }

        return new WorldStateDisclosed(
            Definition: disclosed,
            Withheld: withheld
        );
    }

    // The placement half of Disclose, over the disclosed rows; null when no deal or response facet reads a withheld row.
    private static WorldPlacement[]? DisclosePlacements(WorldDefinition definition, WorldStateRow[] rows, Dictionary<string, WorldWithheldRow> withheld) {
        var placements = definition.Placements;
        Dictionary<string, WorldPlacement>? templates = null;
        var responsive = false;

        foreach (var placement in placements) {
            if (
                (placement.Deal is { } deal) &&
                (withheld.ContainsKey(key: deal.Row) || ((deal.Variants is { } variants) && withheld.ContainsKey(key: variants.Row)))
            ) {
                (templates ??= new(comparer: StringComparer.Ordinal))[placement.Id] = placement;
            }

            responsive |= ReadsWithheld(
                placement: placement,
                withheld: withheld
            );
        }

        if (
            (templates is null) &&
            !responsive
        ) {
            return null;
        }

        var authored = definition.AuthoredState;
        var result = new List<WorldPlacement>(capacity: placements.Count);
        var changed = false;

        foreach (var placement in placements) {
            if (ReadsWithheld(
                placement: placement,
                withheld: withheld
            )) {
                var reading = WorldPlacementResponse.Disclosed(
                    placement: placement,
                    withheld: (row, key) => CellWithheld(
                        key: key,
                        row: row,
                        rows: rows,
                        withheld: withheld
                    )
                );

                changed |= !ReferenceEquals(
                    objA: reading,
                    objB: placement
                );
                result.Add(item: reading);

                continue;
            }
            if (
                (templates is null) ||
                (placement.Parent is not { } parentId) ||
                !templates.TryGetValue(
                key: parentId,
                value: out var template
            ) ||
                !WorldPlacementDeal.IsChild(
                parent: template,
                placement: placement
            )
            ) {
                result.Add(item: placement);

                continue;
            }

            var deal = template.Deal!;
            var key = placement.Id.Substring(startIndex: (parentId.Length + 1));

            if (
                withheld.ContainsKey(key: deal.Row) &&
                !HasCell(
                key: key,
                row: WorldDefinitionRows.FindStateRow(
                    name: deal.Row,
                    rows: rows
                )
            )
            ) {
                changed = true;

                continue;
            }

            if (
                (deal.Variants is { } variants) &&
                withheld.ContainsKey(key: variants.Row)
            ) {
                var disclosedVariants = WorldDefinitionRows.FindStateRow(
                    name: variants.Row,
                    rows: rows
                );

                if (
                    !HasCell(
                    key: key,
                    row: disclosedVariants
                ) &&
                    HasCell(
                    key: key,
                    row: WorldDefinitionRows.FindStateRow(
                        name: variants.Row,
                        rows: authored
                    )
                )
                ) {
                    var prototype = deal.ResolvePrototype(
                        key: key,
                        templatePrototype: template.PrototypeId,
                        variantRow: disclosedVariants
                    );

                    if (!string.Equals(
                        a: prototype,
                        b: placement.PrototypeId,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        changed = true;
                        result.Add(item: (placement with { PrototypeId = prototype }));

                        continue;
                    }
                }
            }

            result.Add(item: placement);
        }

        return (changed
            ? [.. result]
            : null
        );
    }
    private static bool ReadsWithheld(WorldPlacement placement, Dictionary<string, WorldWithheldRow> withheld) => (
        (placement.Respond is { Count: > 0 }) &&
        WorldPlacementResponse.Reads(
        placement: placement,
        rows: withheld.ContainsKey
    )
    );
    // A cell the recipient's rows do not carry, of a row that withholds from it, is withheld: the disclosed row cannot
    // tell a withheld cell from an absent one, and neither may a reading of it.
    private static bool CellWithheld(string row, string? key, WorldStateRow[] rows, Dictionary<string, WorldWithheldRow> withheld) => (
        withheld.TryGetValue(
        key: row,
        value: out var withheldRow
    ) &&
        (withheldRow.WholeRow || !HasCell(
        key: (key ?? StateRow.SlotKey.Value),
        row: WorldDefinitionRows.FindStateRow(
            name: row,
            rows: rows
        )
    ))
    );
    private static bool HasCell(WorldStateRow? row, string key) => (
        CellName.TryParse(
        candidate: key,
        name: out var name,
        reason: out _
    ) &&
        (StateRows.FindCell(
        cells: row?.Cells,
        key: name
    ) is not null)
    );

    /// <summary>Refuses flattening a presentation binding that could disclose a restricted value.</summary>
    /// <param name="definition">The live document, for the row declarations.</param>
    /// <param name="arena">The live store.</param>
    /// <param name="graph">The presentation graph to flatten.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <exception cref="WorldDisclosureException">The graph references a row this recipient may not read whole.</exception>
    /// <param name="unrestricted">Whether to disclose as a reader every restriction admits — the most any recipient could
    /// be handed, which a measurement sizing for every possible recipient reads — instead of as <paramref name="recipient"/>.</param>
    public static void ValidateBindings(WorldDefinition definition, StateArena arena, object graph, Principal? recipient, bool unrestricted = false) {
        var observer = new Observer(
            arena: arena,
            definition: definition,
            recipient: recipient,
            unrestricted: unrestricted
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
                throw new WorldDisclosureException(message: "a presentation binding references restricted state; bind an explicit observation layer instead");
            }
        }
    }

    // The clock a cell's trait reads, as the arena stores it; none while it has never settled, which every trait reads
    // as settled at tick zero and a follower as at rest on its target.
    private static StateCellClock? TraitClock(StateArena arena, int rowOrdinal, CellKey key) => ((arena.TryReadClock(
        epochEngineTick: out var epochEngineTick,
        epochTick: out var epochTick,
        key: key,
        rowOrdinal: rowOrdinal,
        set: out var set,
        substepTicks: out var substepTicks,
        v0: out var v0,
        y0: out var y0
    ) && set)
        ? new StateCellClock(
            EpochEngineTick: epochEngineTick,
            EpochTick: epochTick,
            SubstepTicks: substepTicks,
            V0: v0,
            Y0: y0
        )
        : null);
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
        // Composing as a reader every restriction admits: the most any recipient could be handed.
        private readonly bool m_unrestricted;

        public Observer(WorldDefinition definition, StateArena arena, Principal? recipient, bool unrestricted = false) {
            m_arena = arena;
            m_definition = definition;
            m_unrestricted = unrestricted;
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
            if (
                (policy is null) ||
                m_unrestricted
            ) {
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
            if (m_unrestricted) {
                return true;
            }
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
