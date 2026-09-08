using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// One entry of a placement's response trait: while <see cref="When"/> holds, the row's rendered/collided prototype
/// becomes <see cref="PrototypeId"/> instead of its currently authored one — the bridge that lets a placement react
/// to live simulation state (a burning tree becomes a charred stump; a filled account swaps a granary's face).
/// Absent <see cref="WorldPlacement.Respond"/> is today's behavior exactly: the placement always shows its own
/// authored <see cref="WorldPlacement.PrototypeId"/>.
/// </summary>
/// <remarks>
/// <para>Entries are tried in authored order every response sweep
/// (<c>Server.WorldServer.SweepPlacementResponses</c>, run once per tick immediately after the field lattice steps);
/// the FIRST whose condition holds wins, and the sweep stops looking there. When no entry holds, the row is left
/// exactly as it currently reads — the facet only ever SELECTS among the authored responses on a match; it never
/// reverts a prior swap back toward the row's own base <see cref="WorldPlacement.PrototypeId"/>. A world that wants
/// a fall-through authors one, ordered last, whose condition is trivially true.</para>
/// <para><see cref="When"/> is a closed union (see <see cref="WorldPlacementResponseCondition"/>): the original
/// lattice-field condition, tested at the cell the placement's own authored <see cref="WorldPlacement.Position"/>
/// couples to (the identical body-coupling resolve <see cref="WorldReaction.Emit"/>/<see cref="WorldReaction.Expose"/>
/// already use for a population body, <c>Puck.Physics.Fields.FieldLattice.TryBodyCellOf</c>), and a state-cell
/// condition, which reads an ordinary <c>state.world</c> row independent of the placement's position and of any
/// <c>fields</c> section at all.</para>
/// <para>A matching swap lands as an ordinary <c>WorldMutation.UpsertPlacement</c> under
/// <c>WorldPrincipal.World</c>, so it revalidates, rebuilds derived state (colliders included), and journals through
/// the one mutation pipeline like any other engine-driven placement write — <c>world.undo</c> puts a swap back, and
/// a replay of the same tape reproduces it on the same tick because the trigger is simulation state.</para>
/// <para>Refused together with <see cref="WorldPlacement.Attach"/> and <see cref="WorldPlacement.Inhabit"/> (a
/// sibling concern owns body locomotion) and <see cref="WorldPlacement.FaceSources"/> (its per-instance overrides
/// pin to the creation the row validated against, which a response is free to change). Every response entry's
/// <see cref="PrototypeId"/>, and the row's own base one, must resolve to a declared, non-animated creation (no
/// timeline frames) — a response never turns a static stamp into an animated one.</para>
/// </remarks>
/// <param name="When">The condition tested every sweep — a lattice-field read or a state-cell read.</param>
/// <param name="PrototypeId">The creation the placement shows/collides as while <paramref name="When"/> holds. Must
/// resolve to a declared, non-animated creation row.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementResponse(WorldPlacementResponseCondition When, string PrototypeId);
/// <summary>
/// The closed union a response entry's <see cref="WorldPlacementResponse.When"/> speaks. Both arms evaluate
/// against the SAME per-tick document the rule frame folds a rule's writes into before this sweep runs
/// (<c>Server.WorldServer.SweepPlacementResponses</c> runs after <c>EvaluateWorldRules</c>' own end-of-tick fold),
/// so a cell a rule wrote this tick already reads through <see cref="StateCondition"/> on the same tick's sweep.
/// </summary>
[JsonDerivedType(typeof(FieldCondition), typeDiscriminator: "field")]
[JsonDerivedType(typeof(StateCondition), typeDiscriminator: "state")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldPlacementResponseCondition {
    // A Fixed row's literal keeps its exact fixed-point scale; an Int/Bool row's literal rounds to the nearest whole
    // number — the raw encoding StateCellWriter.TryParseNumericToken already gives every other author-typed literal
    // of that kind, so a condition's comparand reads the same way a console cell edit would.
    private static long LiteralToRaw(CellKind kind, float literal) => (kind switch {
        CellKind.Fixed => FixedQ4816.FromDouble(value: literal).Value,
        _ => ((long)MathF.Round(x: literal, mode: MidpointRounding.ToEven)),
    });
    /// <summary>The original lattice-field condition, unchanged from before this union existed: the named field
    /// read at the placement's own coupled cell, compared against a literal or another row's slot cell.</summary>
    /// <param name="Field">The field read at the cell.</param>
    /// <param name="Comparison">The comparison.</param>
    /// <param name="Value">The scalar compared against (literal or state-row reference) — the same
    /// <see cref="WorldLatticeScalar"/> grammar a <c>fields.reactions</c> Transform/Expose condition already uses.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record FieldCondition(string Field, ActionStateComparison Comparison, WorldLatticeScalar Value) : WorldPlacementResponseCondition;
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
        ActionStateComparison Comparison,
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
        public bool Holds(WorldDefinition definition, ulong tick) {
            ArgumentNullException.ThrowIfNull(argument: definition);

            if (!WorldStateReader.TryRead(definition: definition, rowName: State, key: Key, tick: tick, row: out var row, rawValue: out var raw, text: out _) || (raw is not { } rawValue)) {
                return false;
            }

            long expected;

            if (ComparandState is { } comparandRow) {
                if (!WorldStateReader.TryRead(definition: definition, rowName: comparandRow, key: ComparandKey, tick: tick, row: out _, rawValue: out var comparand, text: out _) || (comparand is not { } comparandValue)) {
                    return false;
                }

                expected = comparandValue;
            } else {
                expected = LiteralToRaw(kind: row.Kind, literal: (Value ?? 0f));
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
