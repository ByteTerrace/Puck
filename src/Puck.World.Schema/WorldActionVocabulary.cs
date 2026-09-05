using Puck.Assets.Documents;
using System.Text.Json.Serialization;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>A data-composable gate over body facts and named action state. A trigger fires only while its gate holds.
/// The <c>$type</c> string is
/// the JSON discriminator, the same convention every polymorphic row family uses; a new predicate kind is a new
/// derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.</summary>
[JsonDerivedType(typeof(ActionPredicate.Now), typeDiscriminator: "now")]
[JsonDerivedType(typeof(ActionPredicate.Recently), typeDiscriminator: "recently")]
[JsonDerivedType(typeof(ActionPredicate.CompareState), typeDiscriminator: "compareState")]
[JsonDerivedType(typeof(ActionPredicate.CompareValue), typeDiscriminator: "compareValue")]
[JsonDerivedType(typeof(ActionPredicate.TimerElapsed), typeDiscriminator: "timerElapsed")]
[JsonDerivedType(typeof(ActionPredicate.All), typeDiscriminator: "all")]
[JsonDerivedType(typeof(ActionPredicate.Any), typeDiscriminator: "any")]
[JsonDerivedType(typeof(ActionPredicate.Not), typeDiscriminator: "not")]
[JsonDerivedType(typeof(ActionPredicate.Held), typeDiscriminator: "held")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionPredicate {
    /// <summary>World-scope comparison of two bounded numeric expressions. Arithmetic failure makes this comparison false.</summary>
    /// <param name="Left">Left numeric expression.</param>
    /// <param name="Comparison">The comparison operation.</param>
    /// <param name="Right">Right numeric expression.</param>
    /// <param name="Kind">The common Int or Fixed domain; no implicit conversion is performed.</param>
    public sealed record CompareValue(WorldValueExpression Left, ActionStateComparison Comparison, WorldValueExpression Right, CellKind Kind = CellKind.Fixed) : ActionPredicate;
    /// <summary>The fact holds this tick.</summary>
    public sealed record Now(ActionFact Fact) : ActionPredicate;
    /// <summary>The fact held within the last <paramref name="WindowSeconds"/> — a per-instance recency clock,
    /// refreshed while the fact holds and decaying otherwise (coyote time is <c>Recently(Grounded, w)</c>).</summary>
    public sealed record Recently(ActionFact Fact, float WindowSeconds) : ActionPredicate;
    /// <summary>Compares a named state cell against either a fixed authored value, or — world scope only — another
    /// named state cell/reserved channel read live at the same evaluation. Both spellings are authorable; exactly one
    /// of <paramref name="Value"/> and <paramref name="ComparandState"/> may be present (refused by name when both or
    /// neither are). The comparand-row spelling is what lets a gate track a moving threshold — <c>$tick</c> compared
    /// against a schedule row the rule's own effects advance is "every N ticks"; a round row compared against a
    /// declared length row is a round boundary — composition over the same two-sided comparison, never a new
    /// mechanism.</summary>
    /// <param name="State">At body scope, a named counter slot the kit declares. At world scope (see
    /// <see cref="WorldRule"/>), a declared <c>state</c>-section row name, or one of
    /// <see cref="WorldRuleFacts"/>'s reserved channels.</param>
    /// <param name="Comparison">The comparison to apply.</param>
    /// <param name="Value">The authored constant comparand, or <see langword="null"/> when
    /// <paramref name="ComparandState"/> spells the comparand instead. Required (non-null) at body scope, where a
    /// comparand row reference is refused.</param>
    /// <param name="Key">At world scope, the cell inside <paramref name="State"/> to read —
    /// <see langword="null"/> reads the row's slot cell, which a keyed row does not have (refused by name rather
    /// than silently reading <c>cells[0]</c>). At body scope a non-null key is refused: a per-body action-state slot
    /// is not keyed, and a parsed-and-discarded field is worse than no field.</param>
    /// <param name="ComparandState">world scope only (refused at body scope, on the same terms as
    /// <paramref name="Key"/>): another declared <c>state</c>-section row name, or one of
    /// <see cref="WorldRuleFacts"/>'s reserved channels, read live and compared instead of <paramref name="Value"/>.
    /// A dotted spelling (an author reaching for <c>row.key</c> in one string) is refused by name — address the cell
    /// with <paramref name="ComparandKey"/> instead. Comparing across incompatible cell kinds (an <c>int</c> row
    /// against a <c>fixed</c> row, say) is refused by name — mixing scales silently is worse than naming the
    /// mismatch.</param>
    /// <param name="ComparandKey">The cell inside <paramref name="ComparandState"/>, on the same (row, key) terms as
    /// <paramref name="Key"/>. Refused when <paramref name="ComparandState"/> names a reserved channel or is absent.</param>
    public sealed record CompareState(
        string State,
        ActionStateComparison Comparison,
        decimal? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandKey = null
    ) : ActionPredicate;
    /// <summary>Whether a named timer slot has drained.</summary>
    public sealed record TimerElapsed(string State) : ActionPredicate;
    /// <summary>Every inner predicate holds (conjunction).</summary>
    public sealed record All(IReadOnlyList<ActionPredicate> Predicates) : ActionPredicate;
    /// <summary>At least one inner predicate holds (disjunction). The list must be non-empty.</summary>
    /// <param name="Predicates">The non-empty child-predicate list.</param>
    public sealed record Any(IReadOnlyList<ActionPredicate> Predicates) : ActionPredicate;
    /// <summary>Inverts one predicate.</summary>
    /// <param name="Predicate">The child predicate to invert.</param>
    public sealed record Not(ActionPredicate Predicate) : ActionPredicate;
    /// <summary>The named composition channel's own live read is at or above its declared threshold — the same test
    /// a held sprint/drift channel makes today. Legitimate only inside a kit's <c>shaping</c>-row gate, where the
    /// world's channel table resolves <paramref name="Channel"/> to an ordinal at kit-compile time; refused
    /// everywhere else a predicate is authored.</summary>
    /// <param name="Channel">The declared composition channel name.</param>
    public sealed record Held(string Channel) : ActionPredicate;
}

/// <summary>A bounded postfix numeric expression evaluated by a world rule, decision, or flock affinity. Each token either pushes a value or
/// consumes preceding values; the compiler proves stack shape and numeric kind before simulation begins.</summary>
/// <param name="Tokens">The postfix tokens, in evaluation order.</param>
public sealed record WorldValueExpression(IReadOnlyList<WorldValueToken> Tokens);
/// <summary>One authored token in a <see cref="WorldValueExpression"/>.</summary>
[JsonDerivedType(typeof(WorldValueToken.Constant), typeDiscriminator: "constant")]
[JsonDerivedType(typeof(WorldValueToken.State), typeDiscriminator: "state")]
[JsonDerivedType(typeof(WorldValueToken.Social), typeDiscriminator: "social")]
[JsonDerivedType(typeof(WorldValueToken.SocialClock), typeDiscriminator: "socialClock")]
[JsonDerivedType(typeof(WorldValueToken.SocialResult), typeDiscriminator: "socialResult")]
[JsonDerivedType(typeof(WorldValueToken.Add), typeDiscriminator: "add")]
[JsonDerivedType(typeof(WorldValueToken.Subtract), typeDiscriminator: "subtract")]
[JsonDerivedType(typeof(WorldValueToken.Multiply), typeDiscriminator: "multiply")]
[JsonDerivedType(typeof(WorldValueToken.Divide), typeDiscriminator: "divide")]
[JsonDerivedType(typeof(WorldValueToken.Min), typeDiscriminator: "min")]
[JsonDerivedType(typeof(WorldValueToken.Max), typeDiscriminator: "max")]
[JsonDerivedType(typeof(WorldValueToken.Clamp), typeDiscriminator: "clamp")]
[JsonDerivedType(typeof(WorldValueToken.Modulo), typeDiscriminator: "modulo")]
[JsonDerivedType(typeof(WorldValueToken.BitAnd), typeDiscriminator: "bitAnd")]
[JsonDerivedType(typeof(WorldValueToken.BitOr), typeDiscriminator: "bitOr")]
[JsonDerivedType(typeof(WorldValueToken.BitXor), typeDiscriminator: "bitXor")]
[JsonDerivedType(typeof(WorldValueToken.BitNot), typeDiscriminator: "bitNot")]
[JsonDerivedType(typeof(WorldValueToken.ShiftLeft), typeDiscriminator: "shiftLeft")]
[JsonDerivedType(typeof(WorldValueToken.ShiftRight), typeDiscriminator: "shiftRight")]
[JsonDerivedType(typeof(WorldValueToken.ShiftRightLogical), typeDiscriminator: "shiftRightLogical")]
[JsonDerivedType(typeof(WorldValueToken.Equal), typeDiscriminator: "equal")]
[JsonDerivedType(typeof(WorldValueToken.NotEqual), typeDiscriminator: "notEqual")]
[JsonDerivedType(typeof(WorldValueToken.Less), typeDiscriminator: "less")]
[JsonDerivedType(typeof(WorldValueToken.LessOrEqual), typeDiscriminator: "lessOrEqual")]
[JsonDerivedType(typeof(WorldValueToken.Greater), typeDiscriminator: "greater")]
[JsonDerivedType(typeof(WorldValueToken.GreaterOrEqual), typeDiscriminator: "greaterOrEqual")]
[JsonDerivedType(typeof(WorldValueToken.Select), typeDiscriminator: "select")]
[JsonDerivedType(typeof(WorldValueToken.PopCount), typeDiscriminator: "popCount")]
[JsonDerivedType(typeof(WorldValueToken.LeadingZeroCount), typeDiscriminator: "leadingZeroCount")]
[JsonDerivedType(typeof(WorldValueToken.TrailingZeroCount), typeDiscriminator: "trailingZeroCount")]
[JsonDerivedType(typeof(WorldValueToken.LowestSetBit), typeDiscriminator: "lowestSetBit")]
[JsonDerivedType(typeof(WorldValueToken.ClearLowestSetBit), typeDiscriminator: "clearLowestSetBit")]
[JsonDerivedType(typeof(WorldValueToken.RotateLeft), typeDiscriminator: "rotateLeft")]
[JsonDerivedType(typeof(WorldValueToken.RotateRight), typeDiscriminator: "rotateRight")]
[JsonDerivedType(typeof(WorldValueToken.ByteSwap), typeDiscriminator: "byteSwap")]
[JsonDerivedType(typeof(WorldValueToken.BitReverse), typeDiscriminator: "bitReverse")]
[JsonDerivedType(typeof(WorldValueToken.Negate), typeDiscriminator: "negate")]
[JsonDerivedType(typeof(WorldValueToken.Abs), typeDiscriminator: "abs")]
[JsonDerivedType(typeof(WorldValueToken.Sign), typeDiscriminator: "sign")]
[JsonDerivedType(typeof(WorldValueToken.ParallelBitExtract), typeDiscriminator: "parallelBitExtract")]
[JsonDerivedType(typeof(WorldValueToken.ParallelBitDeposit), typeDiscriminator: "parallelBitDeposit")]
[JsonDerivedType(typeof(WorldValueToken.BitField), typeDiscriminator: "bitField")]
[JsonDerivedType(typeof(WorldValueToken.BitInsert), typeDiscriminator: "bitInsert")]
[JsonDerivedType(typeof(WorldValueToken.BoardShift), typeDiscriminator: "boardShift")]
[JsonDerivedType(typeof(WorldValueToken.BoardImage), typeDiscriminator: "boardImage")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldValueToken {
    /// <summary>A directed impression query, with its facet's declared numeric kind.</summary>
    public sealed record Social(WorldSocialQuery Query) : WorldValueToken;
    /// <summary>The social bank's current engine tick, Int saturated at Int64.MaxValue.</summary>
    public sealed record SocialClock : WorldValueToken;
    /// <summary>The last social evidence result ordinal, Int; -1 before any attempt. Rule effects execute in document order.</summary>
    public sealed record SocialResult : WorldValueToken;
    /// <summary>An exact authored decimal, converted to the destination row's numeric kind at compile time.</summary>
    /// <param name="Value">The exact decimal literal.</param>
    public sealed record Constant(decimal Value) : WorldValueToken;
    /// <summary>A live state cell or reserved world-rule channel.</summary>
    /// <param name="Name">The state row or reserved-channel name.</param>
    /// <param name="Key">The optional keyed-row cell.</param>
    public sealed record State(
        string Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : WorldValueToken;
    /// <summary>Consumes two values and pushes their sum.</summary>
    public sealed record Add : WorldValueToken;
    /// <summary>Consumes two values and pushes left minus right.</summary>
    public sealed record Subtract : WorldValueToken;
    /// <summary>Consumes two values and pushes their product in the destination row's numeric domain.</summary>
    public sealed record Multiply : WorldValueToken;
    /// <summary>Consumes two values and pushes left divided by right; zero fails evaluation. The caller closes a gate,
    /// rejects an effect/decision candidate, or supplies zero affinity according to its own contract.</summary>
    public sealed record Divide : WorldValueToken;
    /// <summary>Consumes two values and pushes the lesser.</summary>
    public sealed record Min : WorldValueToken;
    /// <summary>Consumes two values and pushes the greater.</summary>
    public sealed record Max : WorldValueToken;
    /// <summary>Consumes value, minimum, maximum (in that authored order) and pushes the inclusive clamp.</summary>
    public sealed record Clamp : WorldValueToken;
    /// <summary>Consumes two values and pushes the remainder of left divided by right, truncating toward zero
    /// (Int: <c>37 % 40 = 37</c>; Fixed: the raw remainder, so <c>2.5 % 1 = 0.5</c>). A zero divisor fails
    /// evaluation; a divisor of -1 yields zero.</summary>
    public sealed record Modulo : WorldValueToken;
    /// <summary>Consumes two Int values and pushes their bitwise AND. Int expressions only.</summary>
    public sealed record BitAnd : WorldValueToken;
    /// <summary>Consumes two Int values and pushes their bitwise OR. Int expressions only.</summary>
    public sealed record BitOr : WorldValueToken;
    /// <summary>Consumes two Int values and pushes their bitwise XOR. Int expressions only.</summary>
    public sealed record BitXor : WorldValueToken;
    /// <summary>Consumes one Int value and pushes its bitwise complement. Int expressions only.</summary>
    public sealed record BitNot : WorldValueToken;
    /// <summary>Consumes value, count and pushes value shifted left by count bits; bits leave the top without
    /// refusal, so <c>1 shiftLeft 63</c> is the sign bit. A count outside 0..63 fails evaluation. Int only.</summary>
    public sealed record ShiftLeft : WorldValueToken;
    /// <summary>Consumes value, count and pushes the arithmetic (sign-propagating) right shift. A count outside 0..63
    /// fails evaluation. Int only.</summary>
    public sealed record ShiftRight : WorldValueToken;
    /// <summary>Consumes value, count and pushes the logical (zero-filling) right shift, the bitboard walk. A count
    /// outside 0..63 fails evaluation. Int only.</summary>
    public sealed record ShiftRightLogical : WorldValueToken;
    /// <summary>Consumes two same-kind values and pushes Int 1 when equal, else 0.</summary>
    public sealed record Equal : WorldValueToken;
    /// <summary>Consumes two same-kind values and pushes Int 1 when unequal, else 0.</summary>
    public sealed record NotEqual : WorldValueToken;
    /// <summary>Consumes two same-kind values and pushes Int 1 when left is less than right, else 0.</summary>
    public sealed record Less : WorldValueToken;
    /// <summary>Consumes two same-kind values and pushes Int 1 when left is at most right, else 0.</summary>
    public sealed record LessOrEqual : WorldValueToken;
    /// <summary>Consumes two same-kind values and pushes Int 1 when left is greater than right, else 0.</summary>
    public sealed record Greater : WorldValueToken;
    /// <summary>Consumes two same-kind values and pushes Int 1 when left is at least right, else 0.</summary>
    public sealed record GreaterOrEqual : WorldValueToken;
    /// <summary>Consumes condition, whenTrue, whenFalse (in that authored order) and pushes whenTrue when the Int
    /// condition is nonzero, else whenFalse. The two branches must share a kind; the result takes it.</summary>
    public sealed record Select : WorldValueToken;
    /// <summary>Consumes one Int value and pushes the number of set bits (0..64): a bitboard's piece count. Int
    /// only.</summary>
    public sealed record PopCount : WorldValueToken;
    /// <summary>Consumes one Int value and pushes the count of zero bits above the highest set bit (64 for zero):
    /// <c>63 - leadingZeroCount</c> is the highest set square, the integer log2. Int only.</summary>
    public sealed record LeadingZeroCount : WorldValueToken;
    /// <summary>Consumes one Int value and pushes the count of zero bits below the lowest set bit (64 for zero): the
    /// lowest occupied square's index. Int only.</summary>
    public sealed record TrailingZeroCount : WorldValueToken;
    /// <summary>Consumes one Int value and pushes its lowest set bit alone (<c>x &amp; -x</c>; zero for zero): the
    /// next piece to visit. Int only.</summary>
    public sealed record LowestSetBit : WorldValueToken;
    /// <summary>Consumes one Int value and pushes it with its lowest set bit cleared (<c>x &amp; (x - 1)</c>): the
    /// remaining pieces after a visit. Int only.</summary>
    public sealed record ClearLowestSetBit : WorldValueToken;
    /// <summary>Consumes value, count and pushes the 64-bit left rotation; a count outside 0..63 fails evaluation.
    /// Int only.</summary>
    public sealed record RotateLeft : WorldValueToken;
    /// <summary>Consumes value, count and pushes the 64-bit right rotation; a count outside 0..63 fails evaluation.
    /// Int only.</summary>
    public sealed record RotateRight : WorldValueToken;
    /// <summary>Consumes one Int value and pushes its eight bytes in reverse order: on an 8x8 bitboard, the board
    /// flipped rank for rank (a vertical mirror). Int only.</summary>
    public sealed record ByteSwap : WorldValueToken;
    /// <summary>Consumes one Int value and pushes its 64 bits in reverse order: on an 8x8 bitboard, the board rotated
    /// a half turn. Int only.</summary>
    public sealed record BitReverse : WorldValueToken;
    /// <summary>Consumes one value and pushes its negation in the same kind; the carrier's minimum fails evaluation.</summary>
    public sealed record Negate : WorldValueToken;
    /// <summary>Consumes one value and pushes its magnitude in the same kind; the carrier's minimum fails evaluation.</summary>
    public sealed record Abs : WorldValueToken;
    /// <summary>Consumes one value of either kind and pushes Int -1, 0, or 1 by its sign.</summary>
    public sealed record Sign : WorldValueToken;
    /// <summary>Consumes value, mask and pushes the bits of value at the mask's set positions, packed toward bit 0
    /// in order (pext): a bitboard's occupancy along a chosen set of squares as a dense index. Int only.</summary>
    public sealed record ParallelBitExtract : WorldValueToken;
    /// <summary>Consumes value, mask and pushes the low bits of value scattered to the mask's set positions in
    /// order (pdep): a dense index back onto its squares. Int only.</summary>
    public sealed record ParallelBitDeposit : WorldValueToken;
    /// <summary>Consumes value, offset, width and pushes the unsigned field of <c>width</c> bits starting at bit
    /// <c>offset</c>; width outside 1..64 or offset + width above 64 fails evaluation. Int only.</summary>
    public sealed record BitField : WorldValueToken;
    /// <summary>Consumes value, field, offset, width and pushes value with the <c>width</c> bits at <c>offset</c>
    /// replaced by the low bits of field; the same bounds as <see cref="BitField"/>. Int only.</summary>
    public sealed record BitInsert : WorldValueToken;
    /// <summary>Consumes one Int mask over the named topology's cells (bit c is cell ordinal c, at most 64 cells) and
    /// pushes it with every set bit moved to that cell's neighbour in the named direction; a cell with no neighbour
    /// that way drops its bit instead of wrapping, so an attack map never crosses an edge. Int only.</summary>
    /// <param name="Topology">A discrete topology of <c>state.lattices</c> with at most 64 cells.</param>
    /// <param name="Direction">A direction of that topology.</param>
    public sealed record BoardShift(string Topology, string Direction) : WorldValueToken;
    /// <summary>Consumes one Int mask over the named topology's cells and pushes it carried through a point-group
    /// element of that topology (a rotation or mirror of the board), so a rule authored from one side's view reads
    /// the other side's board through the half turn. Int only.</summary>
    /// <param name="Topology">A discrete topology of <c>state.lattices</c> with at most 64 cells.</param>
    /// <param name="Element">An element name <c>world.topology</c> lists for that topology.</param>
    public sealed record BoardImage(string Topology, string Element) : WorldValueToken;
}

/// <summary>How a rule-triggered body designation chooses its target.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldBodyDesignationKind>))]
public enum WorldBodyDesignationKind : byte {
    /// <summary>Designate another active body.</summary>
    Body,
    /// <summary>Clear the register.</summary>
    Clear,
}

/// <summary>One deterministic presentation-neutral cue emitted by an authored rule.</summary>
/// <param name="Name">The cue's stable authored identifier.</param>
/// <param name="Payload">An optional bounded payload interpreted by the consumer.</param>
/// <param name="Body">An optional active-body index associated with the cue.</param>
/// <param name="Tick">The simulation tick that emitted it.</param>
public readonly record struct WorldGameplayCue(string Name, string? Payload, int? Body, ulong Tick) {
    /// <summary>Determines whether a cue name is a bounded dot-separated token suitable for a document and log.</summary>
    /// <param name="candidate">The candidate cue name.</param>
    /// <returns><see langword="true"/> when the name is non-empty, bounded, begins and ends with an ASCII letter or
    /// digit, and otherwise contains only ASCII letters, digits, dots, hyphens, or underscores.</returns>
    public static bool IsValidName(string? candidate) {
        if (
            (candidate is not { Length: > 0 }) ||
            (candidate.Length > WorldRuleCapacity.MaxCueNameLength) ||
            !char.IsAsciiLetterOrDigit(c: candidate[0]) ||
            !char.IsAsciiLetterOrDigit(c: candidate[^1])
        ) {
            return false;
        }

        foreach (var character in candidate) {
            if (
                !char.IsAsciiLetterOrDigit(c: character) &&
                (character != '.') &&
                (character != '-') &&
                (character != '_')
            ) {
                return false;
            }
        }

        return true;
    }
}
/// <summary>The finite, non-recursive effect vocabulary admitted inside an atomic world-rule transaction. It mirrors
/// the rollback-safe world-scope effects explicitly; nested transactions and persistence I/O have no wire shape.</summary>
[JsonDerivedType(typeof(WorldTransactionStep.TransformStateStep), typeDiscriminator: "transformState")]
[JsonDerivedType(typeof(WorldTransactionStep.SetCell), typeDiscriminator: "setState")]
[JsonDerivedType(typeof(WorldTransactionStep.AddCell), typeDiscriminator: "addState")]
[JsonDerivedType(typeof(WorldTransactionStep.CountdownCell), typeDiscriminator: "countdownState")]
[JsonDerivedType(typeof(WorldTransactionStep.RemoveCell), typeDiscriminator: "removeStateCell")]
[JsonDerivedType(typeof(WorldTransactionStep.ScheduleCell), typeDiscriminator: "scheduleState")]
[JsonDerivedType(typeof(WorldTransactionStep.GenerateStep), typeDiscriminator: "generate")]
[JsonDerivedType(typeof(WorldTransactionStep.UpsertHudPanelStep), typeDiscriminator: "upsertHudPanel")]
[JsonDerivedType(typeof(WorldTransactionStep.RemoveHudPanelStep), typeDiscriminator: "removeHudPanel")]
[JsonDerivedType(typeof(WorldTransactionStep.UpsertPlacementStep), typeDiscriminator: "upsertPlacement")]
[JsonDerivedType(typeof(WorldTransactionStep.RemovePlacementStep), typeDiscriminator: "removePlacement")]
[JsonDerivedType(typeof(WorldTransactionStep.PoseStep), typeDiscriminator: "pose")]
[JsonDerivedType(typeof(WorldTransactionStep.EmitCueStep), typeDiscriminator: "emitCue")]
[JsonDerivedType(typeof(WorldTransactionStep.SetBodyVerticalVelocityStep), typeDiscriminator: "setBodyVerticalVelocity")]
[JsonDerivedType(typeof(WorldTransactionStep.ScaleBodyVerticalVelocityStep), typeDiscriminator: "scaleBodyVerticalVelocity")]
[JsonDerivedType(typeof(WorldTransactionStep.ApplyBodyImpulseStep), typeDiscriminator: "applyBodyImpulse")]
[JsonDerivedType(typeof(WorldTransactionStep.DesignateBodyStep), typeDiscriminator: "designateBody")]
[JsonDerivedType(typeof(WorldTransactionStep.PaintFieldStep), typeDiscriminator: "paintField")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldTransactionStep {
    /// <summary>One atomic state transform.</summary>
    /// <param name="Transform">The bounded operation.</param>
    public sealed record TransformStateStep(WorldStateTransform Transform) : WorldTransactionStep;
    /// <summary>Sets one state cell from exactly one numeric source spelling.</summary>
    /// <param name="State">The destination row.</param>
    /// <param name="Key">The optional destination cell key.</param>
    /// <param name="Value">The literal source.</param>
    /// <param name="FromState">The live source row or reserved channel.</param>
    /// <param name="FromKey">The optional source cell key.</param>
    /// <param name="ValueSeconds">The exact engine-tick duration source.</param>
    /// <param name="Expression">The bounded postfix numeric source.</param>
    public sealed record SetCell(
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldValueExpression? Expression = null
    ) : WorldTransactionStep;
    /// <summary>Adds exactly one numeric source to a state cell.</summary>
    /// <param name="State">The destination row.</param>
    /// <param name="Key">The optional destination cell key.</param>
    /// <param name="Value">The literal source.</param>
    /// <param name="FromState">The live source row or reserved channel.</param>
    /// <param name="FromKey">The optional source cell key.</param>
    /// <param name="ValueSeconds">The exact engine-tick duration source.</param>
    /// <param name="Expression">The bounded postfix numeric source.</param>
    public sealed record AddCell(
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldValueExpression? Expression = null
    ) : WorldTransactionStep;
    /// <summary>Consumes one non-negative integer countdown by the current engine-step width.</summary>
    /// <param name="State">The countdown row.</param>
    /// <param name="Key">The optional cell key.</param>
    public sealed record CountdownCell(string State, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null) : WorldTransactionStep;
    /// <summary>Removes one addressed state cell.</summary>
    /// <param name="State">The row to remove from.</param>
    /// <param name="Key">The optional cell key.</param>
    public sealed record RemoveCell(string State, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null) : WorldTransactionStep;
    /// <summary>Writes one absolute simulation-tick deadline.</summary>
    /// <param name="State">The integer destination row.</param>
    /// <param name="DelaySeconds">The non-negative delay, converted with the world's simulation rate and rounded up.</param>
    /// <param name="Key">The optional cell key.</param>
    public sealed record ScheduleCell(string State, decimal DelaySeconds, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null) : WorldTransactionStep;
    /// <summary>Redraws one declared state draw site.</summary>
    public sealed record GenerateStep(string Row) : WorldTransactionStep;
    /// <summary>Upserts one world HUD panel.</summary>
    public sealed record UpsertHudPanelStep(WorldHudPanel Panel) : WorldTransactionStep;
    /// <summary>Removes one world HUD panel.</summary>
    public sealed record RemoveHudPanelStep(string Id) : WorldTransactionStep;
    /// <summary>Upserts one placement row.</summary>
    public sealed record UpsertPlacementStep(WorldPlacement Placement) : WorldTransactionStep;
    /// <summary>Removes one placement row.</summary>
    public sealed record RemovePlacementStep(string Id) : WorldTransactionStep;
    /// <summary>Teleports an active body.</summary>
    public sealed record PoseStep(
        string Key,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SpawnPoint = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Position = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float YawDegrees = 0f,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float PitchDegrees = 0f,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float RollDegrees = 0f
    ) : WorldTransactionStep;
    /// <summary>Emits one deterministic gameplay cue.</summary>
    public sealed record EmitCueStep(string Name, string? Payload = null, string? Key = null) : WorldTransactionStep;
    /// <summary>Sets an active body's vertical velocity.</summary>
    public sealed record SetBodyVerticalVelocityStep(string Key, decimal Velocity) : WorldTransactionStep;
    /// <summary>Scales an active body's vertical velocity.</summary>
    public sealed record ScaleBodyVerticalVelocityStep(string Key, decimal Factor) : WorldTransactionStep;
    /// <summary>Applies a timed body-local planar impulse.</summary>
    public sealed record ApplyBodyImpulseStep(string Key, DocumentVector3 BodyDirection, decimal Speed, decimal DurationSeconds) : WorldTransactionStep;
    /// <summary>Sets or clears an active body's target register.</summary>
    public sealed record DesignateBodyStep(string Key, string Register, WorldBodyDesignationKind Kind, string? TargetKey = null) : WorldTransactionStep;
    /// <summary>Sets or adds a bounded live field neighborhood.</summary>
    public sealed record PaintFieldStep(string Field, int X, int Y, int Z, decimal Value, WorldFieldWriteOp Operation = WorldFieldWriteOp.Set, int Radius = 0) : WorldTransactionStep;
}
/// <summary>An authored operand row lowered to a <see cref="BodyMotionOp"/> and executed by the body instruction
/// interpreter when its trigger fires.</summary>
[JsonDerivedType(typeof(ActionEffect.SetVerticalVelocity), typeDiscriminator: "setVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.ScaleVerticalVelocity), typeDiscriminator: "scaleVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.PlanarImpulse), typeDiscriminator: "planarImpulse")]
[JsonDerivedType(typeof(ActionEffect.SetState), typeDiscriminator: "setState")]
[JsonDerivedType(typeof(ActionEffect.AddState), typeDiscriminator: "addState")]
[JsonDerivedType(typeof(ActionEffect.PushState), typeDiscriminator: "pushState")]
[JsonDerivedType(typeof(ActionEffect.TransformState), typeDiscriminator: "transformState")]
[JsonDerivedType(typeof(ActionEffect.CountdownState), typeDiscriminator: "countdownState")]
[JsonDerivedType(typeof(ActionEffect.StartTimer), typeDiscriminator: "startTimer")]
[JsonDerivedType(typeof(ActionEffect.Designate), typeDiscriminator: "designate")]
[JsonDerivedType(typeof(ActionEffect.Generate), typeDiscriminator: "generate")]
[JsonDerivedType(typeof(ActionEffect.UpsertHudPanel), typeDiscriminator: "upsertHudPanel")]
[JsonDerivedType(typeof(ActionEffect.RemoveHudPanel), typeDiscriminator: "removeHudPanel")]
[JsonDerivedType(typeof(ActionEffect.UpsertPlacement), typeDiscriminator: "upsertPlacement")]
[JsonDerivedType(typeof(ActionEffect.RemovePlacement), typeDiscriminator: "removePlacement")]
[JsonDerivedType(typeof(ActionEffect.Save), typeDiscriminator: "save")]
[JsonDerivedType(typeof(ActionEffect.Pose), typeDiscriminator: "pose")]
[JsonDerivedType(typeof(ActionEffect.RemoveStateCell), typeDiscriminator: "removeStateCell")]
[JsonDerivedType(typeof(ActionEffect.ScheduleState), typeDiscriminator: "scheduleState")]
[JsonDerivedType(typeof(ActionEffect.Transaction), typeDiscriminator: "transaction")]
[JsonDerivedType(typeof(ActionEffect.EmitCue), typeDiscriminator: "emitCue")]
[JsonDerivedType(typeof(ActionEffect.SetBodyVerticalVelocity), typeDiscriminator: "setBodyVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.ScaleBodyVerticalVelocity), typeDiscriminator: "scaleBodyVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.ApplyBodyImpulse), typeDiscriminator: "applyBodyImpulse")]
[JsonDerivedType(typeof(ActionEffect.DesignateBody), typeDiscriminator: "designateBody")]
[JsonDerivedType(typeof(ActionEffect.PaintField), typeDiscriminator: "paintField")]
[JsonDerivedType(typeof(ActionEffect.ObserveSocial), typeDiscriminator: "observeSocial")]
[JsonDerivedType(typeof(ActionEffect.ForgetSocial), typeDiscriminator: "forgetSocial")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionEffect {
    /// <summary>Delivers explicitly perceived evidence through the world's bounded social-memory policy. World-scope only.</summary>
    public sealed record ObserveSocial(WorldSocialObservation Evidence) : ActionEffect;
    /// <summary>Forgets one impression without clearing its unexpired evidence receipts. World-scope only.</summary>
    public sealed record ForgetSocial(WorldSocialRelationship Relationship) : ActionEffect;
    /// <summary>Applies a bounded state transform through the ordinary mutation pipeline.</summary>
    /// <param name="Transform">The typed operation.</param>
    public sealed record TransformState(WorldStateTransform Transform) : ActionEffect;
    /// <summary>Writes the body's vertical-velocity channel (the jump launch / the surge). Under the grounded program
    /// gravity owns its decay; under the free program it bleeds to zero at the tuning's rise gravity (no fall phase).</summary>
    public sealed record SetVerticalVelocity(float Velocity, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>Multiplies the body's vertical velocity (the jump cut; gate on <see cref="ActionFact.Rising"/>).</summary>
    public sealed record ScaleVerticalVelocity(float Factor, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>A timed planar velocity overlay (the dash): <paramref name="BodyDirection"/> is rotated by the body's
    /// attitude at fire time and ridden at <paramref name="Speed"/> for <paramref name="DurationSeconds"/>, integrated
    /// through its own accumulator on top of the body's compiled motion — integration itself is untouched.</summary>
    public sealed record PlanarImpulse(DocumentVector3 BodyDirection, float Speed, float DurationSeconds, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>World scope only: pushes one numeric value into a history row's ring (see
    /// <see cref="WorldStateHistory"/>), the same source spellings as <see cref="SetState"/> minus text: exactly one
    /// of <paramref name="Value"/>, <paramref name="FromState"/>, or <paramref name="Expression"/>.</summary>
    /// <param name="State">The history row.</param>
    /// <param name="Value">An exact decimal literal in the row's kind.</param>
    /// <param name="FromState">A state row or reserved channel read live at every firing.</param>
    /// <param name="FromKey">The cell of <paramref name="FromState"/>, or null for its slot.</param>
    /// <param name="Expression">A bounded numeric expression evaluated in the row's kind.</param>
    public sealed record PushState(
        string State,
        decimal? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldValueExpression? Expression = null
    ) : ActionEffect;
    /// <summary>Writes a named state cell — a kit counter slot at body scope, a <c>state</c>-section row's cell at
    /// world scope (see <see cref="WorldRule"/>).</summary>
    /// <param name="State">The counter slot (body scope) or state row name (world scope).</param>
    /// <param name="Value">The literal value to write, or <see langword="null"/> when <paramref name="FromState"/>
    /// spells a live operand to copy instead — world scope only, exactly one of the two is authored (refused by name
    /// when both or neither are present, the same duality <see cref="ActionPredicate.CompareState"/>'s own comparand
    /// carries). Required (non-null) at body scope, where a live copy source is refused.</param>
    /// <param name="Target">The addressed entity — body scope only; a non-<see cref="ActionTarget.Self"/> target is
    /// refused at world scope, where there is no entity to select.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> at world scope — <see langword="null"/> writes the
    /// row's slot cell, which a keyed row does not have (refused by name). Refused at body scope.</param>
    /// <param name="FromState">world scope only (refused at body scope, on the same terms as <paramref name="Value"/>):
    /// another declared <c>state</c>-section row name, or one of <see cref="WorldRuleFacts"/>'s reserved channels,
    /// read live at fire time and copied in place of an authored <paramref name="Value"/> — the row that resets to
    /// another row's own current value (a shadow row mirroring a counter someone else advances), never only a
    /// standing literal. Resolved through the same operand walk <see cref="ActionPredicate.CompareState"/>'s own
    /// <c>ComparandState</c> uses; mixing a <c>fixed</c> row into an <c>int</c> destination (or the reverse) is
    /// refused by name rather than coerced.</param>
    /// <param name="FromKey">The cell inside <paramref name="FromState"/>, on the same (row, key) terms as
    /// <paramref name="Key"/>. Refused when <paramref name="FromState"/> names a reserved channel or is absent.</param>
    /// <param name="ValueSeconds">world scope only (refused at body scope, on the same terms as <paramref name="Value"/>
    /// and <paramref name="FromState"/> — exactly one of the three is authored): an alternative to
    /// <paramref name="Value"/> for a <c>kind=int</c> state row a companion <see cref="CountdownState"/> effect
    /// decrements once per simulation tick (a countdown/cooldown). Authored in seconds — a physical unit, not a tick count,
    /// so a world's rate can change without silently retuning every cooldown — and converted once at rule compile
    /// time to an exact whole engine-tick count via <see cref="Puck.Maths.FixedTickConversion.TryDurationEngineTicksExact"/>,
    /// never re-derived at runtime and never rounded: a duration that is not an exact whole engine-tick count is
    /// refused rather than silently rounded away (<see cref="WorldRuleRefusal.DurationNotExactEngineTicks"/>). Typed
    /// <see cref="decimal"/> rather than <see langword="float"/> because JSON deserializes a number token to
    /// <see cref="decimal"/> exactly (base-10, no binary-float intermediate), and most terminating decimals — the
    /// only ones an author can spell — have no exact binary float or fixed-point spelling either. See
    /// <see cref="WorldRuleCompiler"/>.</param>
    /// <param name="Text">world scope only: the literal a <c>kind=text</c> state row's cell takes — the fourth
    /// spelling beside <paramref name="Value"/>/<paramref name="FromState"/>/<paramref name="ValueSeconds"/>, exactly
    /// one authored. Because every state-bound document value (a creation palette colour, a look-assignment row
    /// name) re-resolves on the write, this is how a rule restyles what a body wears.</param>
    /// <param name="Expression">World scope only: a bounded numeric expression evaluated in the destination row's
    /// integer or fixed-point domain. Exactly one source spelling is authored.</param>
    public sealed record SetState(
        string State,
        decimal? Value = null,
        ActionTarget Target = ActionTarget.Self,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldValueExpression? Expression = null
    ) : ActionEffect;
    /// <summary>Adds to a named state cell — a kit counter slot at body scope, a <c>state</c>-section row's cell at
    /// world scope (see <see cref="WorldRule"/>).</summary>
    /// <param name="State">The counter slot (body scope) or state row name (world scope).</param>
    /// <param name="Value">The literal addend, or <see langword="null"/> when <paramref name="FromState"/> spells a
    /// live addend instead — see <see cref="SetState.Value"/>'s remarks; the same value/from duality, required
    /// (non-null) at body scope.</param>
    /// <param name="Target">The addressed entity — body scope only.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> at world scope; refused at body scope.</param>
    /// <param name="FromState">world scope only — see <see cref="SetState.FromState"/>'s remarks; here the addend is
    /// read live rather than the replacement.</param>
    /// <param name="FromKey">The cell inside <paramref name="FromState"/> — see <see cref="SetState.FromKey"/>.</param>
    /// <param name="ValueSeconds">world scope only — see <see cref="SetState.ValueSeconds"/>'s remarks; here the
    /// converted tick count is the addend rather than the replacement.</param>
    /// <param name="Expression">World scope only — see <see cref="SetState.Expression"/>.</param>
    public sealed record AddState(
        string State,
        decimal? Value = null,
        ActionTarget Target = ActionTarget.Self,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldValueExpression? Expression = null
    ) : ActionEffect;
    /// <summary>Decrements a world-state countdown by the current simulation step's engine-tick width, saturating at
    /// zero. world scope only: the destination must be a <c>kind=int nonNegative=true</c> row. Unlike an authored
    /// <see cref="AddState"/> constant, this effect consumes the runtime step width, so changing the world's authored
    /// tick rate never retunes the duration. When the remaining duration is shorter than one step, the computed
    /// decrement is exactly the remaining value; it reaches zero without asking the explicit-write door to admit a
    /// negative candidate.</summary>
    /// <param name="State">The countdown state-row name.</param>
    /// <param name="Key">The cell inside <paramref name="State"/>; <see langword="null"/> addresses its slot.</param>
    public sealed record CountdownState(
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Removes one addressed cell from a declared world-state row. World scope only.</summary>
    /// <param name="State">The row to remove from.</param>
    /// <param name="Key">The optional cell key.</param>
    public sealed record RemoveStateCell(
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Writes an absolute simulation due tick into an integer state cell. The delay is converted against
    /// the world's authored simulation rate and rounded up, so it never fires early. A companion rule compares
    /// <c>$tick</c> against the cell and removes it after handling, forming a bounded, document-backed scheduler.</summary>
    /// <param name="State">The integer destination row.</param>
    /// <param name="DelaySeconds">The non-negative delay, rounded up to simulation ticks.</param>
    /// <param name="Key">The optional cell key.</param>
    public sealed record ScheduleState(
        string State,
        decimal DelaySeconds,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Applies a bounded list of state, document, body, cue, and field effects atomically after preflight.
    /// When any effect refuses, none apply and <paramref name="OnFailure"/> runs instead. Nested transactions and
    /// save effects are structurally unavailable because persistence I/O cannot be rolled back.</summary>
    /// <param name="Effects">The main transaction branch.</param>
    /// <param name="OnFailure">The optional branch run after a main-branch refusal.</param>
    public sealed record Transaction(
        IReadOnlyList<WorldTransactionStep> Effects,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldTransactionStep>? OnFailure = null
    ) : ActionEffect;
    /// <summary>Emits a deterministic, presentation-neutral gameplay cue. World scope only.</summary>
    /// <param name="Name">The stable authored cue identifier.</param>
    /// <param name="Payload">The optional bounded consumer payload.</param>
    /// <param name="Key">The optional body key used for spatial presentation.</param>
    public sealed record EmitCue(
        string Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payload = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Sets an active body's vertical velocity. <paramref name="Key"/> accepts the ordinary body-key
    /// indirection and rule bindings.</summary>
    /// <param name="Key">The body index or dynamic body-key spelling.</param>
    /// <param name="Velocity">The fixed-point vertical velocity.</param>
    public sealed record SetBodyVerticalVelocity(string Key, decimal Velocity) : ActionEffect;
    /// <summary>Scales an active body's vertical velocity.</summary>
    /// <param name="Key">The body index or dynamic body-key spelling.</param>
    /// <param name="Factor">The fixed-point scale factor.</param>
    public sealed record ScaleBodyVerticalVelocity(string Key, decimal Factor) : ActionEffect;
    /// <summary>Applies a timed body-local planar impulse to an active body.</summary>
    /// <param name="Key">The body index or dynamic body-key spelling.</param>
    /// <param name="BodyDirection">The finite unit direction in body-local space.</param>
    /// <param name="Speed">The fixed-point impulse speed.</param>
    /// <param name="DurationSeconds">The non-negative exact engine-tick duration.</param>
    public sealed record ApplyBodyImpulse(string Key, DocumentVector3 BodyDirection, decimal Speed, decimal DurationSeconds) : ActionEffect;
    /// <summary>Sets or clears one active body's declared target register.</summary>
    /// <param name="Key">The body whose register changes.</param>
    /// <param name="Register">The declared target-register name.</param>
    /// <param name="Kind">Whether to set a body target or clear the register.</param>
    /// <param name="TargetKey">The required target body for <see cref="WorldBodyDesignationKind.Body"/>.</param>
    public sealed record DesignateBody(
        string Key,
        string Register,
        WorldBodyDesignationKind Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetKey = null
    ) : ActionEffect;
    /// <summary>Sets or adds a value over a bounded spherical neighborhood in a live lattice field.</summary>
    /// <param name="Field">The declared lattice field.</param>
    /// <param name="X">The center X coordinate, in lattice cells.</param>
    /// <param name="Y">The center Y coordinate, in lattice cells.</param>
    /// <param name="Z">The center Z coordinate, in lattice cells.</param>
    /// <param name="Value">The fixed-point value to set or add.</param>
    /// <param name="Operation">The set or add operation.</param>
    /// <param name="Radius">The sphere radius, in lattice cells.</param>
    public sealed record PaintField(
        string Field,
        int X,
        int Y,
        int Z,
        decimal Value,
        WorldFieldWriteOp Operation = WorldFieldWriteOp.Set,
        int Radius = 0
    ) : ActionEffect;
    /// <summary>Starts a named timer slot with an authored duration.</summary>
    public sealed record StartTimer(string State, float Seconds, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>Submits the selected subject into a named target register.</summary>
    /// <param name="Register">The authored target-register name.</param>
    /// <param name="Target">The subject source.</param>
    public sealed record Designate(string Register, ActionTarget Target = ActionTarget.AffectingSubject) : ActionEffect;
    /// <summary>Redraws a draw site (a <c>state</c> row declaring a <see cref="WorldDraw"/>) — the one effect
    /// admissible at both scopes, and the join that makes authored randomness and world rules one arc rather than
    /// two: a kit action, a world rule, and the <c>world.generate</c> console verb all reduce to composing the same
    /// <c>WorldMutation.Generate</c> and letting it drain through the ordinary tick boundary, so journal/undo cover a
    /// draw for free wherever it was fired from. This is also how a draw's moment is authored: a
    /// <see cref="WorldDrawTiming.TickPeriod"/> site redraws on an ordinary <c>$tick</c>-scheduled rule and an
    /// <see cref="WorldDrawTiming.Event"/> site on an event-gated one, so timing costs no mutation ordinal. At body
    /// scope the firing is staged during the body's advance and enqueued for the next tick's drain (an honestly-
    /// reported one-tick latency: this is the first <see cref="ActionEffect"/> to write the document rather than
    /// per-body state, so it is the first to pay the pipeline's own round trip).</summary>
    /// <param name="Row">The draw site's row name. One name, not a (source, destination) pair: a site's source is its
    /// own facet and a site is a scalar slot, so there is nothing else to address.</param>
    public sealed record Generate(string Row) : ActionEffect;
    /// <summary>Upserts a whole HUD panel row — world scope only (refused at body scope: a per-body action has no HUD
    /// panel of its own to author). Admits <c>WorldMutation.UpsertHudPanel</c> into the world-rule effect set
    /// through the same seam <see cref="Generate"/> uses: the compiled effect submits the mutation stamped
    /// <see cref="WorldPrincipal.World"/>, which <c>WorldServer.TryAdmitMutation</c> admits structurally, so the
    /// panel's own validation (capacity, unknown binding) is the ordinary whole-document revalidation every
    /// <see cref="UpsertHudPanel"/> submission — console, addon, or rule — already passes through.</summary>
    /// <param name="Panel">The whole panel row, elements included.</param>
    public sealed record UpsertHudPanel(WorldHudPanel Panel) : ActionEffect;
    /// <summary>Removes a HUD panel row by id — world scope only. See <see cref="UpsertHudPanel"/>'s remarks.</summary>
    /// <param name="Id">The panel id to remove.</param>
    public sealed record RemoveHudPanel(string Id) : ActionEffect;
    /// <summary>Upserts a whole placement row — world scope only (refused at body scope: a per-body action has no
    /// placement of its own to author). Admits <c>WorldMutation.UpsertPlacement</c> into the world-rule effect
    /// set through the same seam <see cref="Generate"/> uses.</summary>
    /// <param name="Placement">The whole placement row.</param>
    public sealed record UpsertPlacement(WorldPlacement Placement) : ActionEffect;
    /// <summary>Removes a placement row by id — world scope only. See <see cref="UpsertPlacement"/>'s remarks.</summary>
    /// <param name="Id">The placement id to remove.</param>
    public sealed record RemovePlacement(string Id) : ActionEffect;
    /// <summary>Writes a session snapshot of the world to its own loaded file — world scope only (refused at body
    /// scope: a per-body action has no world file of its own to save). A rule gate now decides when a save happens (an
    /// every-N-ticks cadence, a boss-defeated edge), closing the one gap the mutation substrate could not: a rule
    /// could already express any cadence over <c>$tick</c> or a state fact, but had nothing to fire that composed a
    /// save — every prior save was a human typing <c>world.save</c>, so a crashed server rewound to the last manual
    /// one.</summary>
    /// <remarks>
    /// <para><b>Not a door — the one effect with no <c>WorldMutation</c> kind.</b> Every other admitted effect
    /// (<see cref="SetState"/>, <see cref="Generate"/>, <see cref="UpsertHudPanel"/>, <see cref="UpsertPlacement"/>, …)
    /// composes an ordinary mutation and rides <c>WorldServer.TryApplyMutation</c>: compose, whole-document validate,
    /// install, journal. <c>Save</c> does none of that — it writes no sim state, composes no candidate document, and
    /// journals nothing. It is deterministic in when it fires (an ordinary rule gate over tick/state facts, evaluated
    /// the same way on every run) and projection-only in what it does: the same settle-at-save capture
    /// <c>world.save</c> itself runs (<c>WorldSessionCapture.Capture</c>), which folds live session state into a
    /// snapshot it serializes — it never mutates the in-memory definition. The sim state after a tick carrying a fired
    /// save effect is bit-identical to a tick without one; a replay hash cannot see it, because there is nothing for a
    /// hash to see. That is why this effect needed no <c>KindMask</c> ordinal at all: it is not a mutation. It rides
    /// <c>WorldServer.FireWorldRuleEffect</c> directly instead — the one effect that does.</para>
    /// <para><b>No authored path — the world's own canonical home only.</b> A document that could point a rule's save
    /// at an arbitrary filesystem path is a hazard for no authoring benefit a fixed target does not already cover, so
    /// this effect carries no path field: it always writes to <c>WorldDefinitionSource.SourcePath</c>, the same
    /// resolution the console's own no-argument <c>world.save</c> uses (the file the world was loaded from — an
    /// explicit <c>--world</c> path or the shipped default file, both always file-backed at boot; there is no
    /// "homeless world" boot shape in this engine, so this effect has no compile-time path refusal to author).</para>
    /// <para><b>Throttle honesty — no hidden guard.</b> A <see cref="ActionTriggerMode.Level"/> rule gating this
    /// effect fires it every tick the gate holds — 240 saves/second of disk I/O at the fixed step. This effect adds no
    /// throttle beyond the ordinary <see cref="ActionTriggerMode"/> vocabulary every other effect already uses: that
    /// is the author's own footgun, the same one <see cref="WorldRule.Mode"/>'s own remarks document for a
    /// level-triggered <c>addState</c> ("wrote 503 journal entries across 500 ticks before this mode existed, which is
    /// a measurement, not a style preference") — <see cref="ActionTriggerMode.Edge"/> is what an autosave cadence
    /// wants, for the identical reason. A hidden per-effect guard would be exactly the config surface this repository
    /// does not have.</para>
    /// <para><b>Failure is narrated, never fatal.</b> A write that fails (disk full, the target's directory gone, a
    /// read-only file) is caught at the composition-root seam that performs it and printed on stderr by name; the tick
    /// that fired it continues normally, and nothing about the sim is rolled back — there was nothing to roll back.
    /// </para>
    /// </remarks>
    public sealed record Save : ActionEffect;
    /// <summary>Teleports one body to a pose — the rule-side spelling of the <c>body.pose</c> verb, world scope
    /// only (refused at body scope). Like <see cref="Save"/> it submits no <c>WorldMutation</c>: a pose is body state,
    /// not document state, so nothing composes, validates, or journals, and a replay reproduces it by re-firing the
    /// same rule. Applied as the world's own act through <c>WorldBody.Pose</c>, deliberately outside the
    /// drive-admission gate: a body whose <c>gatesDrive</c> row reads nonzero is a body a rule still needs to move
    /// (a dead body back to its spawn). The client sees the same teleport continuity a <c>body.pose</c> produces.
    /// </summary>
    /// <param name="Key">The target body's 0-based entity index, spelled as a plain integer — the same literal
    /// addressing every other world-scope effect uses for a per-body cell.</param>
    /// <param name="SpawnPoint">A declared <c>spawnPoints</c> id (position and yaw from the point; pitch/roll zero).
    /// Exactly one of this and <paramref name="Position"/> is authored.</param>
    /// <param name="Position">A literal world position. Exactly one of this and <paramref name="SpawnPoint"/> is
    /// authored.</param>
    /// <param name="YawDegrees">The yaw about +Y, degrees; legal only with <paramref name="Position"/> and required
    /// to remain zero when <paramref name="SpawnPoint"/> supplies the pose.</param>
    /// <param name="PitchDegrees">The pitch about the body right, degrees; legal only with
    /// <paramref name="Position"/> and required to remain zero with <paramref name="SpawnPoint"/>.</param>
    /// <param name="RollDegrees">The roll about the body forward, degrees; legal only with
    /// <paramref name="Position"/> and required to remain zero with <paramref name="SpawnPoint"/>.</param>
    public sealed record Pose(
        string Key,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SpawnPoint = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Position = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float YawDegrees = 0f,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float PitchDegrees = 0f,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float RollDegrees = 0f
    ) : ActionEffect;
}

internal static class WorldStateNumericLiteral {
    public static FixedQ4816 ToFixed(decimal value) => TryToFixed(value: value, result: out var result)
        ? result
        : throw new OverflowException(message: $"The exact decimal literal '{value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}' is outside the Q48.16 state range.");

    public static bool TryToFixed(decimal value, out FixedQ4816 result) => FixedQ4816.TryParse(
        s: value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        provider: System.Globalization.CultureInfo.InvariantCulture,
        result: out result
    );
}
/// <summary>One trigger channel of a lane binding: a gate, a press latch (the buffer — a press stays pending until the
/// gate opens or the latch expires; the release channel latches nothing), and the effects a fire applies in order.</summary>
/// <param name="Gate">The predicate that must hold to fire, or <see langword="null"/> for always.</param>
/// <param name="LatchSeconds">How long a press stays pending waiting for the gate. <c>0</c> means this tick only —
/// the press fires if the gate is open on its own edge tick and is dropped otherwise. Legitimate only on
/// <see cref="ActionSpec.OnPress"/>: the release channel latches nothing, so a non-zero value on
/// <see cref="ActionSpec.OnRelease"/> is refused by name at validation rather than parsed and discarded.</param>
/// <param name="Effects">The effects applied on fire, in order.</param>
public sealed record ActionTrigger(IReadOnlyList<ActionEffect> Effects, ActionPredicate? Gate = null, float LatchSeconds = 0f);
/// <summary>A lane's full binding: the press trigger and the release trigger. What a channel does is this data — the
/// engine implements only the facts, predicates, and effects.</summary>
/// <param name="OnPress">The rising-edge trigger, or <see langword="null"/>.</param>
/// <param name="OnRelease">The falling-edge trigger (evaluated immediately, never latched), or <see langword="null"/>.</param>
/// <param name="OnFact">Engine-fact-triggered effect lists evaluated independently of channel edges.</param>
public sealed record ActionSpec(ActionTrigger? OnPress = null, ActionTrigger? OnRelease = null, IReadOnlyList<ActionFactTrigger>? OnFact = null);
/// <summary>An authored effect list fired by one engine fact pulse — gated and edged by the same
/// <see cref="ActionTriggerMode"/> vocabulary a world rule uses.</summary>
/// <param name="Fact">The fact that fires the rule.</param>
/// <param name="Effects">The effects applied in order.</param>
/// <param name="Gate">An additional predicate that must hold beside <paramref name="Fact"/>, or
/// <see langword="null"/> for none.</param>
/// <param name="Mode">Whether the trigger fires every tick the condition holds (<see cref="ActionTriggerMode.Level"/>,
/// the default) or once per crossing (<see cref="ActionTriggerMode.Edge"/>). The
/// condition is <paramref name="Fact"/> and <paramref name="Gate"/> together — an edge trigger re-arms only when
/// that conjunction stops holding.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionFactTrigger(
    ActionFact Fact,
    IReadOnlyList<ActionEffect> Effects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate = null,
    ActionTriggerMode Mode = ActionTriggerMode.Level
);
