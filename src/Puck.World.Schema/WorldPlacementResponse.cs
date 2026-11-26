using Puck.Abstractions.Documents;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// One entry of a placement's response trait: while <see cref="When"/> holds, the row draws and collides as
/// <see cref="PrototypeId"/> instead of its authored <see cref="WorldPlacement.PrototypeId"/> — the bridge that lets a
/// placement react to live simulation state (a burning tree shows a charred stump; a filled account swaps a granary's
/// face). Absent <see cref="WorldPlacement.Respond"/>, the placement always shows its authored prototype.
/// </summary>
/// <remarks>
/// <para>Every entry is tested every response sweep (<c>Server.WorldTick.SweepPlacementResponses</c>, run once per
/// tick immediately after the field lattice steps), and the sweep records which held in
/// <see cref="WorldPlacement.Holding"/>. The row shows the FIRST holding entry in authored order, and its authored
/// prototype while none holds: a response lasts exactly as long as its condition, and the authored
/// <see cref="WorldPlacement.PrototypeId"/> is never overwritten. A booting document is born with its state entries'
/// bits settled (<see cref="WorldStateSettlement.SettleResponses"/>), so it shows from its first frame what its first
/// sweep would. A world that wants a change to outlast its cause
/// latches the cause in state (a rule that sets a flag cell) and conditions the entry on the flag.</para>
/// <para><see cref="When"/> is a closed union (see <see cref="WorldPlacementResponseCondition"/>): the original
/// lattice-field condition, tested at the cell the placement's own authored <see cref="WorldPlacement.Position"/>
/// couples to (the identical body-coupling resolve <see cref="WorldReaction.Emit"/>/<see cref="WorldReaction.Expose"/>
/// already use for a population body, <c>Puck.Physics.Fields.FieldLattice.TryBodyCellOf</c>), and a state-cell
/// condition, which reads an ordinary <c>state.world</c> row independent of the placement's position and of any
/// <c>fields</c> section at all.</para>
/// <para>The mask is runtime state the sweep derives, never an authored edit: a change replaces the live rows'
/// masks outside the mutation pipeline (<see cref="WorldDefinition.WithPlacementHoldings"/>), unjournalled and
/// unvalidated beyond its bits, and reaches every reader as a value delivery. The server recompiles only colliders the
/// shown creation actually moves, and a client presents the change without rebuilding what a face-free swap cannot
/// change (<see cref="ComparePlacements"/>). A replay of the same tape reproduces it on the same tick because the
/// trigger is simulation state, and a checkpoint carries it in the exported rows.
/// The mask travels as data because a field condition reads the server's lattice, which neither a client's document
/// nor a projection carries; a reader whose disclosure withholds a cell an entry reads is handed the mask without
/// that entry (<see cref="Disclosed"/>).</para>
/// <para>An attached placement keeps its attachment while its prototype responds, so a moving door or indicator
/// can react to state. Refused together with <see cref="WorldPlacement.Inhabit"/> (a sibling concern owns body
/// locomotion) and <see cref="WorldPlacement.FaceSources"/> (its per-instance overrides
/// pin to the creation the row validated against, which a response is free to change). Every response entry's
/// <see cref="PrototypeId"/>, and the row's authored one, must resolve to a declared, non-animated creation (no
/// timeline frames) — a response never turns a static stamp into an animated one.</para>
/// </remarks>
/// <param name="When">The condition tested every sweep — a lattice-field read or a state-cell read.</param>
/// <param name="PrototypeId">The creation the placement shows/collides as while <paramref name="When"/> holds and no
/// earlier entry does. Must resolve to a declared, non-animated creation row.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementResponse(WorldPlacementResponseCondition When, [property: DocumentName(WorldNameKind.Prototype, WorldNameRole.Names)] string PrototypeId) {
    /// <summary>Returns the index of the entry a row shows: the lowest bit of <see cref="WorldPlacement.Holding"/>
    /// that names one of its <see cref="WorldPlacement.Respond"/> entries.</summary>
    /// <param name="placement">The placement.</param>
    /// <returns>The entry's index, or -1 when no entry holds and the row shows its authored prototype.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    public static int ShownEntry(WorldPlacement placement) {
        ArgumentNullException.ThrowIfNull(argument: placement);

        if (
            (placement.Holding == 0) ||
            (placement.Respond is not { Count: > 0 } responses)
        ) {
            return -1;
        }

        var index = BitOperations.TrailingZeroCount(value: ((uint)placement.Holding));

        return (((index < responses.Count) && (responses[index] is not null))
            ? index
            : -1
        );
    }
    /// <summary>Returns the creation a row shows: the prototype of its <see cref="ShownEntry"/>, else its authored
    /// <see cref="WorldPlacement.PrototypeId"/>.</summary>
    /// <param name="placement">The placement.</param>
    /// <returns>The shown prototype's id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    public static string Shown(WorldPlacement placement) {
        var entry = ShownEntry(placement: placement);

        return ((entry >= 0)
            ? placement.Respond![entry].PrototypeId
            : placement.PrototypeId
        );
    }
    /// <summary>Returns a responsive row as one reader may read it: its <see cref="WorldPlacement.Holding"/> without
    /// the bit of any entry that reads a cell withheld from the reader.
    /// <para>What remains is what the reader could compute itself: an entry over cells it reads, or over the lattice,
    /// which is not hidden state, holds for it exactly when it holds. So the row shows the reader the first entry that
    /// holds over what it may read, and its authored prototype when none does.</para></summary>
    /// <param name="placement">The live placement carrying a <see cref="WorldPlacement.Respond"/> facet.</param>
    /// <param name="withheld">Determines whether a cell is withheld from the reader: the row's name, and the cell's
    /// key or <see langword="null"/> for the row's slot cell.</param>
    /// <returns>The placement with the reader's mask; <paramref name="placement"/> itself when no set bit's entry reads
    /// a withheld cell.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> or <paramref name="withheld"/> is
    /// <see langword="null"/>.</exception>
    public static WorldPlacement Disclosed(WorldPlacement placement, Func<string, string?, bool> withheld) {
        ArgumentNullException.ThrowIfNull(argument: placement);
        ArgumentNullException.ThrowIfNull(argument: withheld);

        var responses = (placement.Respond ?? []);
        var holding = placement.Holding;

        for (var index = 0; (index < Math.Min(val1: responses.Count, val2: WorldResponseCapacity.MaxEntries)); index++) {
            var bit = (1 << index);

            if (
                ((holding & bit) != 0) &&
                (responses[index]?.When switch {
                    WorldPlacementResponseCondition.StateCondition state => (withheld(arg1: state.State, arg2: state.Key) || ((state.ComparandState is { } comparand) && withheld(arg1: comparand, arg2: state.ComparandKey))),
                    WorldPlacementResponseCondition.FieldCondition { Value.Row: { } row } => withheld(arg1: row, arg2: null),
                    _ => false,
                })
            ) {
                holding &= ~bit;
            }
        }

        return ((holding == placement.Holding)
            ? placement
            : (placement with { Holding = holding }));
    }
    /// <summary>Determines whether the creation a row shows derives a presentation facet: a face, a camera or a
    /// sound, which a presentation reconciles as part of the definition rather than as drawn geometry.</summary>
    /// <param name="definition">The definition whose creations the row resolves against.</param>
    /// <param name="placement">The placement.</param>
    /// <returns><see langword="true"/> when the shown creation declares a face, a camera or a sound.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="placement"/> is
    /// <see langword="null"/>.</exception>
    public static bool ShowsFacets(WorldDefinition definition, WorldPlacement placement) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: placement);

        return (
            (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: placement.ShownPrototypeId) is { } creation) &&
            ((creation.Document.Behavior?.Faces is { Count: > 0 }) || (creation.Document.Cameras is { Count: > 0 }) || (creation.Document.Behavior?.Sounds is { Count: > 0 }))
        );
    }
    /// <summary>Classifies what a value delivery moved in a definition's placement rows: nothing, rows that draw and
    /// collide as something else (a holding swap, a dealt child added, moved or taken away, a contribution), or a row that
    /// shows or stopped showing a creation deriving a presentation facet (<see cref="ShowsFacets"/>). A value delivery
    /// carries every placement projection the authority installs between definition deliveries (the response sweep's
    /// holdings, the deal sweep's children, tenure's contributions), so any row it replaces, adds or removes is a scene
    /// change to its reader. Rows are matched by id and judged by identity: a projection install keeps every row it did
    /// not touch as the same object, so a row that is not the reader's own object is one the delivery moved.</summary>
    /// <param name="before">The definition a reader held, or <see langword="null"/> when it held none.</param>
    /// <param name="after">The definition the delivery hands over.</param>
    /// <param name="solid">Set to whether a moved row is solid before or after, so a consumer of solid geometry alone
    /// holds across a lamp or a display swapping its creation.</param>
    /// <param name="holdings">Set to whether the change is an <see cref="WorldPlacementChange.Appearance"/> whose every
    /// moved row moved only its <see cref="WorldPlacement.Holding"/> (a row present in both whose replacement differs from
    /// it in nothing else, as the response sweep's install leaves it), which a reader keeping every response's shape at
    /// hand presents without composing again.</param>
    /// <returns>The kind of change; <see cref="WorldPlacementChange.None"/> when the rows are the same section or the
    /// reader held none (a first delivery is a definition delivery).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="after"/> is <see langword="null"/>.</exception>
    public static WorldPlacementChange ComparePlacements(WorldDefinition? before, WorldDefinition after, out bool solid, out bool holdings) {
        ArgumentNullException.ThrowIfNull(argument: after);

        solid = false;
        holdings = false;
        if ((before is null) || ReferenceEquals(objA: before.PlacementsRaw, objB: after.PlacementsRaw)) {
            return WorldPlacementChange.None;
        }

        var change = WorldPlacementChange.None;
        var holdsOnly = true;
        var rows = before.Placements;
        var next = after.Placements;
        // Rows keep their order across a projection install, which only replaces in place, appends or removes; the
        // first row out of step switches the walk to matching by id.
        IReadOnlyDictionary<string, WorldPlacement>? earlier = null;

        for (var index = 0; (index < next.Count); index++) {
            var row = next[index];
            WorldPlacement? previous;

            if ((earlier is null) && (index < rows.Count) && string.Equals(a: rows[index].Id, b: row.Id, comparisonType: StringComparison.Ordinal)) {
                previous = rows[index];
            } else {
                earlier ??= WorldPlacementIndex.ById(placements: rows);
                previous = earlier.GetValueOrDefault(key: row.Id);
            }
            if (ReferenceEquals(objA: previous, objB: row)) { continue; }
            holdsOnly &= ((previous is not null) && (previous.Holding != row.Holding) && SameBesidesHolding(previous: previous, next: row));
            change = Moved(change: change, definitions: (before, after), next: row, previous: previous, solid: ref solid);
        }
        // A row the delivery took away moved as much as one it added.
        if ((earlier is not null) || (rows.Count > next.Count)) {
            var kept = WorldPlacementIndex.ById(placements: next);

            foreach (var row in rows) {
                if (!kept.ContainsKey(key: row.Id)) {
                    holdsOnly = false;
                    change = Moved(change: change, definitions: (before, after), next: null, previous: row, solid: ref solid);
                }
            }
        }
        holdings = (holdsOnly && (change == WorldPlacementChange.Appearance));

        return change;
    }
    /// <summary>Classifies what a row delivery moved in a definition's placement rows, as
    /// <see cref="ComparePlacements"/> does for a whole definition, reading only the rows the delivery names.</summary>
    /// <param name="before">The definition the reader held.</param>
    /// <param name="after">The definition the rows brought it to (<see cref="WorldPresentationRows.TryApply"/>).</param>
    /// <param name="rows">The applied rows.</param>
    /// <param name="solid">Set to whether a moved row is solid before or after.</param>
    /// <param name="holdings">Set to whether every moved row moved only its holding and the change is an
    /// <see cref="WorldPlacementChange.Appearance"/>.</param>
    /// <returns>The kind of change; <see cref="WorldPlacementChange.None"/> when the rows move no placement.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldPlacementChange CompareRows(WorldDefinition before, WorldDefinition after, WorldPresentationRows rows, out bool solid, out bool holdings) {
        ArgumentNullException.ThrowIfNull(argument: before);
        ArgumentNullException.ThrowIfNull(argument: after);
        ArgumentNullException.ThrowIfNull(argument: rows);

        solid = false;
        var change = WorldPlacementChange.None;
        var (held, next) = (before.Placements, after.Placements);
        foreach (var (ordinal, _) in rows.Holdings) { change = Moved(change: change, definitions: (before, after), next: next[ordinal], previous: held[ordinal], solid: ref solid); }
        foreach (var (ordinal, _) in rows.Placements) { change = Moved(change: change, definitions: (before, after), next: next[ordinal], previous: held[ordinal], solid: ref solid); }
        holdings = ((rows.Placements.Length == 0) && (change == WorldPlacementChange.Appearance));

        return change;
    }
    // Whether two rows differ in nothing but their holding. A row the response sweep installed keeps every other member
    // as the same object, which the record's own equality proves at once; a row a remote reader decoded holds equal
    // members as other objects, so it is judged by the document form both serialize to.
    internal static bool SameBesidesHolding(WorldPlacement previous, WorldPlacement next) {
        var held = (previous with { Holding = next.Holding });

        if (held.Equals(obj: next)) { return true; }
        var info = WorldJsonContext.Default.For<WorldPlacement>();

        return JsonSerializer.SerializeToUtf8Bytes(value: held, jsonTypeInfo: info).AsSpan().SequenceEqual(other: JsonSerializer.SerializeToUtf8Bytes(value: next, jsonTypeInfo: info));
    }
    // Folds one moved row into the change so far: a facet anywhere outranks an appearance.
    private static WorldPlacementChange Moved(WorldPlacementChange change, (WorldDefinition Before, WorldDefinition After) definitions, WorldPlacement? previous, WorldPlacement? next, ref bool solid) {
        solid |= ((previous?.Solid is not null) || (next?.Solid is not null));
        if (change == WorldPlacementChange.Facets) { return change; }
        return ((((previous is not null) && ShowsFacets(definition: definitions.Before, placement: previous)) || ((next is not null) && ShowsFacets(definition: definitions.After, placement: next)))
            ? WorldPlacementChange.Facets
            : WorldPlacementChange.Appearance);
    }
}
/// <summary>What a value delivery moved in a definition's placement rows
/// (<see cref="WorldPlacementResponse.ComparePlacements"/>).</summary>
public enum WorldPlacementChange {
    /// <summary>No placement row moved.</summary>
    None,
    /// <summary>Rows were replaced, added or removed, none showing a creation that derives a face, a camera or a
    /// sound.</summary>
    Appearance,
    /// <summary>A row shows, or stopped showing, a creation that derives a face, a camera or a sound.</summary>
    Facets,
}
/// <summary>
/// The closed union a response entry's <see cref="WorldPlacementResponse.When"/> speaks. Both arms evaluate
/// against the SAME per-tick document the rule frame folds a rule's writes into before this sweep runs
/// (<c>Server.WorldTick.SweepPlacementResponses</c> runs after <c>EvaluateWorldRules</c>' own end-of-tick fold),
/// so a cell a rule wrote this tick already reads through <see cref="StateCondition"/> on the same tick's sweep.
/// </summary>
[JsonDerivedType(typeof(FieldCondition), typeDiscriminator: "field")]
[JsonDerivedType(typeof(StateCondition), typeDiscriminator: "state")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldPlacementResponseCondition {
    // A Fixed row's literal keeps its exact fixed-point scale; an Int/Bool row's literal rounds to the nearest whole
    // number — the raw encoding CellValue.TryParse already gives every other author-typed literal of that kind, so a
    // condition's comparand reads the same way a console cell edit would.
    private static long LiteralToRaw(CellKind kind, float literal) => (kind switch {
        CellKind.Fixed => FixedQ4816.FromDouble(value: literal).Value,
        _ => ((long)MathF.Round(
        mode: MidpointRounding.ToEven,
        x: literal
    )),
    });

    /// <summary>The original lattice-field condition, unchanged from before this union existed: the named field
    /// read at the placement's own coupled cell, compared against a literal or another row's slot cell.</summary>
    /// <param name="Field">The field read at the cell.</param>
    /// <param name="Comparison">The comparison.</param>
    /// <param name="Value">The scalar compared against (literal or state-row reference) — the same
    /// <see cref="WorldLatticeScalar"/> grammar a <c>fields.reactions</c> Transform/Expose condition already uses.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record FieldCondition(
        [property: DocumentName(WorldNameKind.Field, WorldNameRole.Names)] string Field,
        [property: JsonConverter(typeof(ExpressionComparisonJsonConverter))] ExpressionOp Comparison,
        WorldLatticeScalar Value
    ) : WorldPlacementResponseCondition;
    /// <summary>A state-cell condition: compares <paramref name="State"/>'s cell (its slot cell when
    /// <paramref name="Key"/> is absent, else the cell at <paramref name="Key"/>) against
    /// <paramref name="Value"/>, or — when <paramref name="ComparandState"/> is authored instead — against another
    /// declared row's cell, read live at the same evaluation. Exactly one of <paramref name="Value"/> and
    /// <paramref name="ComparandState"/> may be present, the same one-comparand rule
    /// <see cref="ActionPredicate.CompareState"/> already enforces for a rule's own gate — this facet reuses that
    /// field convention rather than inventing a second reading of "compare a state cell." Independent of the
    /// placement's position, and of whether the document declares a <c>fields</c> section at all.</summary>
    /// <param name="State">The state row compared — any declared <c>state.world</c> row of a numeric kind (never
    /// <see cref="CellKind.Text"/>).</param>
    /// <param name="Comparison">The comparison.</param>
    /// <param name="Value">The literal comparand, or <see langword="null"/> when <paramref name="ComparandState"/>
    /// spells the comparand instead.</param>
    /// <param name="Key">The cell inside <paramref name="State"/>, or <see langword="null"/> for its slot cell.
    /// Refused when <paramref name="State"/> is keyed and this is absent, or unkeyed and this is present.</param>
    /// <param name="ComparandState">Another declared <c>state.world</c> row, read live and compared instead of
    /// <paramref name="Value"/>. Must share <paramref name="State"/>'s cell kind (refused by name otherwise).</param>
    /// <param name="ComparandKey">The cell inside <paramref name="ComparandState"/>, on the same terms as
    /// <paramref name="Key"/>. Refused when <paramref name="ComparandState"/> is absent.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record StateCondition(
        [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)] string State,
        [property: JsonConverter(typeof(ExpressionComparisonJsonConverter))] ExpressionOp Comparison,
        float? Value = null,
        [property: DocumentName(WorldNameKind.State, WorldNameRole.Key)]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandState = null,
        [property: DocumentName(WorldNameKind.State, WorldNameRole.Key)]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandKey = null
    ) : WorldPlacementResponseCondition {
        /// <summary>Evaluates this condition against the live document — the one reading every consumer (the
        /// response sweep, a binding overlay's own gate) shares, so they can never disagree about whether a state
        /// condition currently holds.</summary>
        /// <param name="definition">The document to read.</param>
        /// <param name="tick">The tick to read the referenced cell(s) as of.</param>
        /// <param name="engineTick">The engine tick to read the referenced cell(s) as of.</param>
        /// <param name="arena">The live store, or declaration seeds at admission.</param>
        public bool Holds(WorldDefinition definition, ulong tick, ulong engineTick, StateArena? arena = null) {
            ArgumentNullException.ThrowIfNull(argument: definition);

            if (
                !WorldStateReader.TryRead(arena: arena,
                definition: definition,
                rowName: State,
                key: Key,
                tick: tick,
                engineTick: engineTick,
                row: out var row,
                rawValue: out var raw,
                text: out _
            ) ||
                (raw is not { } rawValue)
            ) {
                return false;
            }

            long expected;

            if (ComparandState is { } comparandRow) {
                if (
                    !WorldStateReader.TryRead(arena: arena,
                    definition: definition,
                    rowName: comparandRow,
                    key: ComparandKey,
                    tick: tick,
                    engineTick: engineTick,
                    row: out _,
                    rawValue: out var comparand,
                    text: out _
                ) ||
                    (comparand is not { } comparandValue)
                ) {
                    return false;
                }

                expected = comparandValue;
            } else {
                expected = LiteralToRaw(
                    kind: row.Kind,
                    literal: (Value ?? 0f)
                );
            }

            return Comparison.Holds(
                value: FixedQ4816.FromRawBits(value: rawValue),
                expected: FixedQ4816.FromRawBits(value: expected)
            );
        }
    }
}
/// <summary>Capacity constants for <see cref="WorldPlacement.Respond"/>.</summary>
public static class WorldResponseCapacity {
    /// <summary>The most response entries a single placement may declare.</summary>
    public const int MaxEntries = 8;
}
