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
/// against. <see cref="Domain"/> carries the disclosed storage shape (slot or keys), without authority-only domain
/// dependencies; <see cref="Capacity"/> is the authored ceiling, never a live reservation or member count.</summary>
public sealed record WorldObservedRow(string Name, CellKind Kind, IReadOnlyList<WorldObservedCell> Cells, StateDomain Domain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int HiddenCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Space = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Min = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Max = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Capacity = null);
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
public sealed record WorldStateDisclosed(WorldDefinition Definition, IReadOnlyDictionary<string, WorldWithheldRow> Withheld) {
    /// <summary>Gets the names of the rows whose disclosure shaped the disclosed placements: every dealt and variant row
    /// of a template the recipient is shown re-dealt or masked, and every row a response entry reads. Any other row's
    /// cells leave the placements as they stand, so a cached composition follows only these rows' images.</summary>
    public IReadOnlySet<string> PlacementReads { get; init; } = new HashSet<string>();
}
/// <summary>Composes state observations for one authenticated recipient.</summary>
/// <remarks>Every value, audience, zone membership and observation stamp is read from the live store, never from a
/// document snapshot taken per observer: which zone holds a token decides who may read it, so a disclosure composed
/// against a stale copy would answer for a table that has already moved.</remarks>
public static class WorldStateDisclosure {
    /// <summary>Discloses physical rows and their authored seeds through live reader policies. Public seeds remain
    /// unchanged; paint and reactions cross only with readable owning rows and dependencies. Current cell values
    /// arrive separately through the same recipient's checked snapshot deltas.</summary>
    public static IReadOnlyList<WorldFieldsSection> FieldProjection(WorldDefinition definition, StateArena arena, Principal? recipient, bool unrestricted = false) {
        var observer = new Observer(arena: arena, recipient: recipient, unrestricted: unrestricted);
        var result = new List<WorldFieldsSection>();

        for (var regionIndex = 0; (regionIndex < definition.FieldRegions.Count); regionIndex++) {
            var region = definition.FieldRegions[regionIndex];
            var fields = new List<WorldFieldRow>();
            var names = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var field in region.Fields) {
                if (!observer.ReadsRow(name: field.Name)) { continue; }
                fields.Add(item: field);
                names.Add(item: field.Name);
            }
            if (fields.Count == 0) { continue; }
            var reactions = new List<WorldReaction>();

            if (region.Reactions is { Count: > 0 }) {
                var program = definition.FieldPrograms[regionIndex];

                for (var index = 0; (index < region.Reactions.Count); index++) {
                    if (FieldNodeDisclosed(program.Nodes[index], program, names, ref observer)) { reactions.Add(item: region.Reactions[index]); }
                }
            }
            result.Add(item: region with {
                Fields = fields,
                Paint = region.Paint?.Where(predicate: fill => names.Contains(item: fill.Field)).ToArray(),
                Reactions = reactions,
                TravelResistance = (((region.TravelResistance?.Row is { } row) && !observer.ReadsRow(name: row)) ? null : region.TravelResistance),
            });
        }
        return result;
    }

    private static bool FieldNodeDisclosed(WorldFieldNode node, WorldFieldProgram program, HashSet<string> fields, ref Observer observer) {
        foreach (var field in node.FieldReads) { if (!fields.Contains(item: program.Fields[field.Ordinal].Name)) { return false; } }
        foreach (var field in node.FieldWrites) { if (!fields.Contains(item: program.Fields[field.Ordinal].Name)) { return false; } }
        foreach (var row in node.StateReads) { if (!observer.ReadsRow(name: program.StateCatalog[row].Name)) { return false; } }
        foreach (var row in node.StateWrites) { if (!observer.ReadsRow(name: program.StateCatalog[row].Name)) { return false; } }
        return true;
    }

    /// <summary>Determines whether the recipient may read one cell, including its containing zone's policy.</summary>
    /// <param name="definition">The live document, for the row declarations.</param>
    /// <param name="arena">The live store.</param>
    /// <param name="row">The row carrying the cell.</param>
    /// <param name="key">The cell's key.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <returns><see langword="true"/> when the recipient may read the cell.</returns>
    public static bool CanRead(WorldDefinition definition, StateArena arena, StateRow row, CellName key, Principal? recipient) {
        var observer = new Observer(
            arena: arena,
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
    /// <summary>Determines, from declarations alone, whether a document row can never withhold a cell from the
    /// recipient: the row's own policy admits the recipient, the row declares no cell restriction
    /// (<see cref="StateArena.DeclaresCellVisibility"/>), and no ordered zone containing it declares one or excludes
    /// the recipient. Every door that answers a restricted row differently (a whole-row read or write, a
    /// presentation binding, an observation's row list, a delta's row replacement) asks this one question, so its
    /// answer never depends on which members are present or hidden now.</summary>
    /// <param name="arena">The live store and its declarations.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <returns><see langword="true"/> when no cell of the row can be withheld from the recipient;
    /// <see langword="false"/> when one can, or the ordinal names no row.</returns>
    public static bool ReadsWholeRow(StateArena arena, int rowOrdinal, Principal? recipient) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        return new Observer(arena: arena, recipient: recipient).ReadsWhole(rowOrdinal: rowOrdinal);
    }
    /// <summary>Returns what one row withholds from the recipient, read from the live store.</summary>
    /// <param name="arena">The live store and its declarations.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <returns><see langword="null"/> when the row reads whole for the recipient (<see cref="ReadsWholeRow"/>);
    /// otherwise the record, present whether zero or many cells are withheld now.</returns>
    public static WorldWithheldRow? WithheldFrom(StateArena arena, int rowOrdinal, Principal? recipient) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        var observer = new Observer(arena: arena, recipient: recipient);

        if (observer.ReadsWhole(rowOrdinal: rowOrdinal)) { return null; }
        var row = arena.Rows[rowOrdinal];
        var rowReadable = observer.Allows(policy: row.Visibility);
        var count = 0;
        var cursor = 0;

        while (arena.TryNextCell(rowOrdinal: rowOrdinal, cursor: ref cursor, key: out var key)) {
            if (!rowReadable || !observer.CanRead(row: row, rowOrdinal: rowOrdinal, key: key)) { count++; }
        }
        return new WorldWithheldRow(Count: count, Policy: (row.Visibility?.Hidden ?? HiddenCells.Omit), WholeRow: !rowReadable);
    }

    // A live audience admits only while every recipient reads it whole: a decision read from cells some recipient
    // cannot see would tell that recipient what they hold (whether its own write is admitted, whether a row
    // reaches it). The validator refuses such an audience by name; this keeps the decision a function of public
    // state when a later install restricts the audience anyway, so it then admits no one.
    private static bool Allows(StateArena arena, StateVisibility? policy, string? recipient) {
        if ((policy is null) || policy.Allows(recipient: recipient)) { return true; }
        if ((recipient is null) || (policy.ReadersFrom is null) ||
            !arena.Catalog.TryResolve(lane: StateLane.Document, name: policy.ReadersFrom, handle: out var readers) ||
            !new Observer(arena: arena, recipient: null).ReadsWhole(rowOrdinal: readers.Ordinal)) { return false; }
        var cursor = 0;

        while (arena.TryNextCell(rowOrdinal: readers.Ordinal, cursor: ref cursor, key: out var key)) {
            if (arena.TryRead(rowOrdinal: readers.Ordinal, key: key, value: out var value) &&
                (value.Kind == CellKind.Text) && string.Equals(a: value.AsText, b: recipient, comparisonType: StringComparison.Ordinal)) { return true; }
        }
        return false;
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
    /// <param name="onlyRow">An optional single row to compose for the shared image table.</param>
    public static IReadOnlyList<WorldObservedRow>? Compose(WorldDefinition definition, StateArena arena, Principal? recipient, bool unrestricted = false, IReadOnlySet<string>? presented = null, int? onlyRow = null) {
        var observer = new Observer(
            arena: arena,
            recipient: recipient,
            unrestricted: unrestricted
        );
        var result = new List<WorldObservedRow>();

        foreach (var row in arena.Rows) {
            // Physical cells cross through their disclosed region and snapshot stream, never twice as arena rows.
            if (row.HostOwned) { continue; }
            if (!observer.TryRow(
                ordinal: out var rowOrdinal,
                row: row
            )) {
                continue;
            }

            if ((onlyRow is { } selected) && (rowOrdinal != selected)) { continue; }

            // Which rows cross is decided by declaration, never by whether a member is restricted now.
            if (
                (row.Visibility is null) &&
                !arena.DeclaresCellVisibility(rowOrdinal: rowOrdinal) &&
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
                // Presentation stores the disclosed cells without the authority's domain dependencies or cursors.
                row.IsSlot ? StateDomain.Slot.Instance : StateDomain.Keys.Instance,
                hidden,
                ((row.Kind == CellKind.Vector)
                    ? row.Space
                    : null),
                row.Min,
                row.Max,
                row.Capacity
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
    /// <para>A dealt placement (<see cref="WorldPlacementDeal"/>) is a reading of the cells it was dealt from, so a
    /// template whose dealt row can withhold from the recipient is re-dealt from the row as the recipient reads it: its
    /// readable cells take slots in the row's order, each child standing where the deal stamps that slot, and under
    /// <see cref="HiddenCells.Placeholder"/> each withheld cell takes its place in that order as an anonymous back
    /// (<see cref="WorldPlacementDeal.PlaceholderId"/>); a placement authored under a child the recipient is not shown
    /// goes with it. A child whose variant cell is withheld shows the template's own prototype, the deal's documented
    /// choice for a key with no variant cell, whether or not the dealt row withholds. A responsive placement
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

        return DiscloseDocument(
            arena: arena,
            definition: definition,
            fields: DisclosedFields.For(arena: arena, definition: definition, recipient: recipient, unrestricted: unrestricted),
            recipient: recipient,
            unrestricted: unrestricted
        );
    }
    internal static WorldStateDisclosed DiscloseDocument(WorldDefinition definition, StateArena arena, Principal? recipient, bool unrestricted, DisclosedFields? fields) {
        var observer = new Observer(
            arena: arena,
            recipient: recipient,
            unrestricted: unrestricted
        );
        var rows = new WorldStateRow[definition.AuthoredState.Count];
        var withheld = new Dictionary<string, WorldWithheldRow>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < rows.Length); index++) {
            rows[index] = DiscloseRow(arena: arena, fields: fields, index: index, observer: ref observer, withheld: out var record);
            if (record is not null) { withheld[rows[index].Name.Value] = record; }
        }

        if (fields is not null) {
            definition = definition with { StateRaw = definition.StateRaw! with { World = rows, Lattices = fields.Lattices } };
        }

        if (withheld.Count == 0) {
            return new WorldStateDisclosed(
                Definition: definition.WithWorldState(rows: rows),
                Withheld: withheld
            );
        }

        var disclosed = (definition with { StateRaw = (definition.StateRaw! with { World = rows }) });
        var reads = new HashSet<string>(comparer: StringComparer.Ordinal);
        var placements = DisclosedPlacements(definition: definition, arena: arena, observer: observer, reads: reads);

        if (!ReferenceEquals(objA: placements, objB: definition.Placements)) {
            disclosed = (disclosed with { PlacementRowsRaw = [.. placements] });
        }

        return new WorldStateDisclosed(
            Definition: disclosed,
            Withheld: withheld
        ) { PlacementReads = reads };
    }
    // One authored row as the recipient's document carries it: the live export, and for a declaration that can
    // withhold, only the readable cells. Disclose and the shared image table's row refresh both derive a row here, so a
    // refreshed row and a whole composition agree.
    internal static WorldStateRow DiscloseRow(StateArena arena, int index, ref Observer observer, DisclosedFields? fields, out WorldWithheldRow? withheld) {
        var exported = arena.ToRow(rowOrdinal: index);
        var row = ((exported as WorldStateRow) ?? new WorldStateRow(exported, false, null));

        withheld = null;
        if (observer.TryRow(ordinal: out var ordinal, row: row) && !observer.ReadsWhole(rowOrdinal: ordinal)) {
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

            withheld = new WorldWithheldRow(
                Count: (cells.Count - kept.Count),
                Policy: (row.Visibility?.Hidden ?? HiddenCells.Omit),
                WholeRow: !rowReadable
            );
            // A row that can withhold has one shape whether zero or many cells are withheld now: its storage and cursor
            // bookkeeping is a reading of the cells withheld from it, so it goes too.
            row = WithoutStorageMetadata(row: row) with { Cells = kept };
        }

        return (fields?.Project(row: row) ?? row);
    }
    // The physical-field half of a replica document. Fields are authored in state rows and physical topologies; this
    // rebuilds that part from the same projection presentation uses, since copying the authored field traits would
    // disclose their seeds and reaction inputs.
    internal sealed class DisclosedFields {
        private readonly HashSet<string> m_authored;
        private readonly Dictionary<string, WorldStateRow> m_disclosed;

        private DisclosedFields(HashSet<string> authored, Dictionary<string, WorldStateRow> disclosed, IReadOnlyList<LatticeTopology> lattices) {
            m_authored = authored; m_disclosed = disclosed; Lattices = lattices;
        }

        // The document's topologies with its field topologies replaced by the disclosed ones.
        public IReadOnlyList<LatticeTopology> Lattices { get; }

        // Null when the document has no field to project or the reader is unrestricted.
        public static DisclosedFields? For(WorldDefinition definition, StateArena arena, Principal? recipient, bool unrestricted) {
            if (unrestricted || (definition.FieldRegions.Count == 0) || (definition.StateRaw is not { } state)) { return null; }
            var fields = WorldFieldsSection.ToStateSection(FieldProjection(definition, arena, recipient));

            return new DisclosedFields(
                authored: definition.AuthoredState.Where(row => row.Field is not null).Select(row => row.Name.Value).ToHashSet(StringComparer.Ordinal),
                disclosed: (fields.World ?? []).ToDictionary(row => row.Name.Value, StringComparer.Ordinal),
                lattices: [.. (state.Lattices ?? []).Where(topology => topology is not WorldFieldTopology), .. (fields.Lattices ?? [])]
            );
        }
        public WorldStateRow Project(WorldStateRow row) => !m_authored.Contains(row.Name.Value) ? row :
            m_disclosed.TryGetValue(row.Name.Value, out var field) ? row with { Field = field.Field } :
            // Replica snapshot row ordinals must stay aligned. An undisclosed field keeps only its row name and policy,
            // with no physical topology, trait, seed or paint that could reconstruct the field; a host serves only slot
            // and lattice shapes (StateCatalog refuses any other host-owned row), and with no topology it is a slot.
            new WorldStateRow(Name: row.Name, Kind: row.Kind, Domain: StateDomain.Slot.Instance, HostOwned: true, Visibility: row.Visibility);
    }
    /// <summary>Removes reservation, physical slot and cursor metadata that can reveal omitted membership, preserving
    /// the declared or inferred domain; the kept cells then load into contiguous slots. A readback applies this even
    /// when no current cell is withheld, so zero and nonzero hidden inventories have the same metadata shape.</summary>
    /// <param name="row">The exported row.</param>
    /// <returns>The row without storage metadata.</returns>
    public static WorldStateRow WithoutStorageMetadata(WorldStateRow row) => row with {
        Domain = row.Domain ?? row.InferDomain(),
        CellSlots = null,
        ReservedCapacity = null,
        DrawCursor = 0L,
        DrawnMasks = null,
        HistoryCursor = 0L,
    };

    /// <summary>Reads the live placements as one recipient may read them, without exporting state rows
    /// (<see cref="DisclosedPlacements"/>).</summary>
    /// <param name="definition">The live document, for the placement rows and declarations.</param>
    /// <param name="arena">The live store, for each dealt row's members and their policies.</param>
    /// <param name="recipient">The recipient, or <see langword="null"/> for the public observer.</param>
    /// <param name="unrestricted">Whether to read as a reader every restriction admits, which reads the authority's
    /// layout.</param>
    /// <returns>The recipient's placements; <paramref name="definition"/>'s own when nothing is read for it.</returns>
    public static IReadOnlyList<WorldPlacement> PlacementProjection(WorldDefinition definition, StateArena arena, Principal? recipient, bool unrestricted = false) {
        var observer = new Observer(arena: arena, recipient: recipient, unrestricted: unrestricted);

        return DisclosedPlacements(definition: definition, arena: arena, observer: observer, reads: null);
    }

    // The one reading of the placements for a recipient, which the replica document, the presentation projection and
    // every view door share. A responsive row loses the holding bit of each entry over a cell the recipient may not
    // read. A template whose dealt row can withhold from the recipient is re-dealt from the row as the recipient reads
    // it: its readable cells, in the row's order, take slots 0..n-1, and under Placeholder each withheld cell of a row
    // the recipient may read takes its place in that order as an anonymous back. A kept child moves to its re-dealt slot
    // with its own displacement, so nothing of the authority's slot history, which hidden arrivals shape, survives
    // into a slot, a position or the order of the rows, and zero, one or many hidden cells give
    // one layout; a placement authored under a child the recipient is not shown is absent too. A template whose variant
    // row alone withholds keeps its children's slots and masks each face the recipient may not read. Every row whose
    // disclosure shaped the result is added to reads, by declaration, so a cached composition knows which rows' images
    // to follow.
    internal static IReadOnlyList<WorldPlacement> DisclosedPlacements(WorldDefinition definition, StateArena arena, Observer observer, HashSet<string>? reads) {
        var placements = definition.Placements;
        // A template enters by declaration when the recipient does not read its dealt row or its variant row whole:
        // the value is the dealt row's ordinal when that row withholds, so the template is re-dealt, and -1 when only
        // the variant row does, so its children keep their slots, which a row the recipient reads whole shaped, and
        // lose a face the recipient may not read.
        Dictionary<string, int>? restricted = null;
        var responsive = false;

        foreach (var placement in placements) {
            if ((placement.Deal is { } deal) && arena.Catalog.TryResolve(handle: out var dealt, lane: StateLane.Document, name: deal.Row)) {
                var redealt = !observer.ReadsWhole(rowOrdinal: dealt.Ordinal);

                if (redealt || ((deal.Variants is { } variants) && !observer.ReadsRow(name: variants.Row))) {
                    (restricted ??= new(comparer: StringComparer.Ordinal))[placement.Id] = (redealt ? dealt.Ordinal : -1);
                    reads?.Add(item: deal.Row);
                    if (deal.Variants is { } read) { reads?.Add(item: read.Row); }
                }
            }
            responsive |= placement.Respond is { Count: > 0 };
        }
        if ((restricted is null) && !responsive) { return placements; }

        bool Withheld(string name, string? key) {
            if (!arena.Catalog.TryResolve(handle: out var row, lane: StateLane.Document, name: name) || observer.ReadsWhole(rowOrdinal: row.Ordinal)) {
                return false;
            }
            reads?.Add(item: name);
            return !observer.Discloses(rowOrdinal: row.Ordinal, key: key);
        }
        Dictionary<string, Dictionary<string, WorldPlacement>>? children = null;
        Dictionary<string, WorldPlacement>? byId = null;

        if (restricted is not null) {
            children = new(comparer: StringComparer.Ordinal);
            byId = new(capacity: placements.Count, comparer: StringComparer.Ordinal);
            foreach (var placement in placements) {
                byId.TryAdd(key: placement.Id, value: placement);
                if (DealtKey(placement: placement, restricted: restricted, key: out var key)) {
                    if (!children.TryGetValue(key: placement.Parent!, value: out var keyed)) { children[placement.Parent!] = keyed = new(comparer: StringComparer.Ordinal); }
                    keyed.TryAdd(key: key, value: placement);
                }
            }
        }
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
        // Each re-dealt template's children as the recipient is shown them, dealt before any row is emitted, and the ids
        // among them: a placement authored under any other child of a re-dealt template stands at a frame only the
        // authority holds, so it goes with that child wherever it sits in the rows.
        Dictionary<string, List<WorldPlacement>>? redeals = null;
        HashSet<string>? shown = null;

        if (restricted is not null) {
            foreach (var (templateId, rowOrdinal) in restricted) {
                if (rowOrdinal < 0) { continue; }
                var dealt = new List<WorldPlacement>();

                Redeal(
                    arena: arena,
                    children: children!.GetValueOrDefault(key: templateId),
                    observer: ref observer,
                    result: dealt,
                    rowOrdinal: rowOrdinal,
                    template: byId![templateId],
                    withheld: Withheld,
                    worldSeed: worldSeed
                );
                (redeals ??= new(comparer: StringComparer.Ordinal))[templateId] = dealt;
                foreach (var child in dealt) { (shown ??= new(comparer: StringComparer.Ordinal)).Add(item: child.Id); }
            }
        }
        var result = new List<WorldPlacement>(capacity: placements.Count);
        var changed = false;

        foreach (var placement in placements) {
            if ((restricted is not null) && DealtKey(placement: placement, restricted: restricted, key: out var dealtKey)) {
                var template = byId![placement.Parent!];

                if (restricted[template.Id] >= 0) {
                    changed = true;
                    continue;
                }
                // Only the variant row withholds: the child keeps its slot and shows its face only when the recipient
                // may read the variant cell that selects it.
                var masked = (Withheld(name: template.Deal!.Variants!.Row, key: dealtKey) ? (placement with { PrototypeId = template.PrototypeId }) : placement);

                changed |= !ReferenceEquals(objA: masked, objB: placement);
                result.Add(item: masked);
                continue;
            }
            if ((restricted is not null) && Unshown(placement: placement, byId: byId!, restricted: restricted, shown: shown)) {
                changed = true;
                continue;
            }
            var reading = WorldPlacementResponse.Disclosed(placement: placement, withheld: Withheld);

            changed |= !ReferenceEquals(objA: reading, objB: placement);
            result.Add(item: reading);
            if ((redeals is not null) && redeals.TryGetValue(key: placement.Id, value: out var redealt)) {
                changed = true;
                result.AddRange(collection: redealt);
            }
        }
        return (changed ? result : placements);
    }
    // Whether placement is a dealt child of a restricted template, and the cell key it was dealt for.
    private static bool DealtKey(WorldPlacement placement, Dictionary<string, int> restricted, out string key) {
        key = string.Empty;
        return ((placement.Parent is { } parent) && restricted.ContainsKey(key: parent) &&
            WorldPlacementDeal.TrySplitChildId(id: placement.Id, template: out var template, key: out key) &&
            string.Equals(a: template, b: parent, comparisonType: StringComparison.Ordinal));
    }
    // Whether a placement authored under a dealt child stands under one the recipient is not shown: the first dealt child
    // on its parent chain belongs to a re-dealt template and is absent from the re-deal. The walk is bounded by the row
    // count against a parent cycle, which frame validation refuses.
    private static bool Unshown(WorldPlacement placement, Dictionary<string, WorldPlacement> byId, Dictionary<string, int> restricted, HashSet<string>? shown) {
        var current = placement;

        for (var steps = 0; (steps <= byId.Count) && (current.Parent is { } parentId) && byId.TryGetValue(key: parentId, value: out var parent); steps++) {
            if (DealtKey(placement: current, restricted: restricted, key: out _)) {
                return ((restricted[parentId] >= 0) && ((shown is null) || !shown.Contains(item: current.Id)));
            }
            current = parent;
        }
        return false;
    }
    // Every cell the recipient is shown takes the next slot whether or not the sweep has dealt its child yet, and a
    // back stands for a withheld cell whatever child the authority holds for it: an install between sweeps (an
    // operator's key rename, a cell not yet dealt) then moves nothing the recipient is shown that the row it reads did
    // not move. A kept child changes only its slot: its position is its displacement from its slot, and its frame
    // (WorldPlacementFrameCompilation.LocalFrame) is the re-dealt slot's fixed-point offset plus that displacement, so
    // nothing of the authority's slot, its offset or that offset's rounding reaches the recipient.
    private static void Redeal(WorldPlacement template, int rowOrdinal, Dictionary<string, WorldPlacement>? children, StateArena arena, ref Observer observer, Func<string, string?, bool> withheld, ulong worldSeed, List<WorldPlacement> result) {
        var slots = WorldPlacementDeal.Offsets(template: template, worldSeed: worldSeed).Length;
        var row = arena.Rows[rowOrdinal];
        // Under Placeholder a reader the row admits is shown a back for each withheld cell, in order, as the row's
        // observation shows it; a reader the row's own policy excludes is shown nothing of it.
        var rowReadable = observer.Allows(policy: row.Visibility);
        var placeholder = rowReadable && (row.Visibility?.Hidden == HiddenCells.Placeholder);
        var variants = template.Deal!.Variants;
        var slot = 0;
        var cursor = 0;

        while ((slot < slots) && arena.TryNextCell(cursor: ref cursor, key: out var cell, rowOrdinal: rowOrdinal)) {
            if (!arena.TryRead(key: cell, rowOrdinal: rowOrdinal, value: out _)) { continue; }
            if (rowReadable && observer.CanRead(key: cell, row: row, rowOrdinal: rowOrdinal)) {
                var key = arena.Keys[key: cell].Value;

                if ((children is not null) && children.TryGetValue(key: key, value: out var child)) {
                    result.Add(item: child with {
                        DealSlot = slot,
                        PrototypeId = (((variants is not null) && withheld(arg1: variants.Row, arg2: key)) ? template.PrototypeId : child.PrototypeId),
                    });
                }
            } else if (placeholder) {
                result.Add(item: WorldPlacementDeal.Child(
                    id: WorldPlacementDeal.PlaceholderId(template: template.Id, slot: slot),
                    prototype: template.PrototypeId,
                    slot: slot,
                    template: template
                ));
            } else {
                continue;
            }
            slot++;
        }
    }

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
            recipient: recipient,
            unrestricted: unrestricted
        );

        foreach (var row in arena.Rows) {
            if (!observer.TryRow(
                ordinal: out var rowOrdinal,
                row: row
            )) {
                continue;
            }
            if (observer.ReadsWhole(rowOrdinal: rowOrdinal)) {
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

    // One recipient's view of one live store: format the canonical token only for a restricted policy. Ordered
    // zones are indexed by token domain, so a cell's read check costs the zones over its own domain and nothing else.
    internal struct Observer {
        private readonly StateArena m_arena;
        private readonly bool m_unrestricted;
        private readonly Principal? m_recipient;

        private string? m_name;

        // name, when given, is the recipient's canonical token already formatted (a view's, formatted once).
        public Observer(StateArena arena, Principal? recipient, bool unrestricted = false, string? name = null) {
            m_arena = arena; m_unrestricted = unrestricted; m_recipient = recipient; m_name = name;
        }

        // A restriction the store holds for the recipient's own token, or one a live text row lists it in.
        public bool Allows(StateVisibility? policy) => (m_unrestricted || (policy is null) || policy.IsPublic ||
            WorldStateDisclosure.Allows(arena: m_arena, policy: policy, recipient: m_name ??= m_recipient?.Describe()));
        public bool ReadsRow(string name) => (m_arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: name) &&
            ReadsWhole(rowOrdinal: handle.Ordinal));
        // The one policy question: whether a declaration on the row's path (its own policy, a cell policy its storage
        // admits, a containing zone's) can withhold a cell from this recipient. Current members never enter it.
        public bool ReadsWhole(int rowOrdinal) {
            if (m_unrestricted) { return true; }
            if ((((uint)rowOrdinal) >= ((uint)m_arena.Rows.Count)) || !Allows(policy: m_arena.Rows[rowOrdinal].Visibility) ||
                m_arena.DeclaresCellVisibility(rowOrdinal: rowOrdinal)) {
                return false;
            }
            foreach (var zoneOrdinal in m_arena.Catalog.ContainingZones(rowOrdinal: rowOrdinal)) {
                if (!Allows(policy: m_arena.Rows[zoneOrdinal].Visibility) || m_arena.DeclaresCellVisibility(rowOrdinal: zoneOrdinal)) { return false; }
            }
            return true;
        }
        // Whether a reading of one cell (the slot cell for a null key) shows the recipient what the live cell holds. In a
        // row that can withhold, an absent cell answers as a withheld one, so the two never read apart.
        public bool Discloses(int rowOrdinal, string? key) {
            if (ReadsWhole(rowOrdinal: rowOrdinal)) { return true; }

            return (CellName.TryParse(candidate: (key ?? StateRow.SlotKey.Value), name: out var name, reason: out _) &&
                m_arena.Keys.TryResolve(key: out var interned, name: name) &&
                m_arena.TryRead(rowOrdinal: rowOrdinal, key: interned, value: out _) &&
                CanRead(row: m_arena.Rows[rowOrdinal], rowOrdinal: rowOrdinal, key: interned));
        }
        public bool CanRead(StateRow row, int rowOrdinal, CellKey key) {
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

            foreach (var zoneOrdinal in m_arena.Catalog.ContainingZones(rowOrdinal: rowOrdinal)) {
                var zone = m_arena.Rows[zoneOrdinal];

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
        public bool TryRow(StateRow row, out int ordinal) {
            if (m_arena.Catalog.TryResolve(
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
