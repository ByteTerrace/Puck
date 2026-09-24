using System.Numerics;
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
/// <see cref="WorldPlacement.PrototypeId"/> is never overwritten. A world that wants a change to outlast its cause
/// latches the cause in state (a rule that sets a flag cell) and conditions the entry on the flag.</para>
/// <para><see cref="When"/> is a closed union (see <see cref="WorldPlacementResponseCondition"/>): the original
/// lattice-field condition, tested at the cell the placement's own authored <see cref="WorldPlacement.Position"/>
/// couples to (the identical body-coupling resolve <see cref="WorldReaction.Emit"/>/<see cref="WorldReaction.Expose"/>
/// already use for a population body, <c>Puck.Physics.Fields.FieldLattice.TryBodyCellOf</c>), and a state-cell
/// condition, which reads an ordinary <c>state.world</c> row independent of the placement's position and of any
/// <c>fields</c> section at all.</para>
/// <para>A changed <see cref="WorldPlacement.Holding"/> lands as an ordinary <c>WorldMutation.UpsertPlacement</c>
/// under <c>Principal.World</c>, so it revalidates, rebuilds derived state (colliders included), reaches every
/// client that reads the document, and journals through the one mutation pipeline like any other engine-driven
/// placement write. A replay of the same tape reproduces it on the same tick because the trigger is simulation state.
/// The mask travels as data because a field condition reads the server's lattice, which neither a client's document
/// nor a projection carries; a reader whose disclosure withholds a cell an entry reads is handed the mask without
/// that entry (<see cref="Disclosed"/>).</para>
/// <para>Refused together with <see cref="WorldPlacement.Attach"/> and <see cref="WorldPlacement.Inhabit"/> (a
/// sibling concern owns body locomotion) and <see cref="WorldPlacement.FaceSources"/> (its per-instance overrides
/// pin to the creation the row validated against, which a response is free to change). Every response entry's
/// <see cref="PrototypeId"/>, and the row's authored one, must resolve to a declared, non-animated creation (no
/// timeline frames) — a response never turns a static stamp into an animated one.</para>
/// </remarks>
/// <param name="When">The condition tested every sweep — a lattice-field read or a state-cell read.</param>
/// <param name="PrototypeId">The creation the placement shows/collides as while <paramref name="When"/> holds and no
/// earlier entry does. Must resolve to a declared, non-animated creation row.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementResponse(WorldPlacementResponseCondition When, string PrototypeId) {
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
    /// <summary>Determines whether any entry of a row's response facet reads one of the named rows — a state
    /// condition's row or comparand row, or a field condition's scalar row.</summary>
    /// <param name="placement">The placement.</param>
    /// <param name="rows">Determines whether a row name is one of the named rows.</param>
    /// <returns><see langword="true"/> when an entry reads a named row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> or <paramref name="rows"/> is
    /// <see langword="null"/>.</exception>
    public static bool Reads(WorldPlacement placement, Func<string, bool> rows) {
        ArgumentNullException.ThrowIfNull(argument: placement);
        ArgumentNullException.ThrowIfNull(argument: rows);

        foreach (var response in (placement.Respond ?? [])) {
            var read = (response?.When switch {
                WorldPlacementResponseCondition.StateCondition state => (rows(arg: state.State) || ((state.ComparandState is { } comparand) && rows(arg: comparand))),
                WorldPlacementResponseCondition.FieldCondition { Value.Row: { } row } => rows(arg: row),
                _ => false,
            });

            if (read) {
                return true;
            }
        }

        return false;
    }
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
    public sealed record FieldCondition(string Field, [property: JsonConverter(typeof(ExpressionComparisonJsonConverter))] ExpressionOp Comparison, WorldLatticeScalar Value) : WorldPlacementResponseCondition;
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
        string State,
        [property: JsonConverter(typeof(ExpressionComparisonJsonConverter))] ExpressionOp Comparison,
        float? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandKey = null
    ) : WorldPlacementResponseCondition {
        /// <summary>Evaluates this condition against the live document — the one reading every consumer (the
        /// response sweep, a binding overlay's own gate) shares, so they can never disagree about whether a state
        /// condition currently holds.</summary>
        /// <param name="definition">The document to read.</param>
        /// <param name="tick">The tick to read the referenced cell(s) as of.</param>
        /// <param name="engineTick">The engine tick to read the referenced cell(s) as of.</param>
        public bool Holds(WorldDefinition definition, ulong tick, ulong engineTick) {
            ArgumentNullException.ThrowIfNull(argument: definition);

            if (
                !WorldStateReader.TryRead(
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
                    !WorldStateReader.TryRead(
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
