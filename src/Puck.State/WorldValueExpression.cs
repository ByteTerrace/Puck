using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.State;

/// <summary>A bounded postfix numeric expression evaluated by a world rule, decision, or flock affinity. Each token either pushes a value or
/// consumes preceding values; the compiler proves stack shape and numeric kind before simulation begins. Authored
/// either as an infix string (<see cref="WorldExpressionSyntax"/>, <c>"min(damage, hp) * 2"</c>) or as the postfix
/// <c>{ "tokens": [...] }</c> object; both parse to the same tokens and each writes back in its own spelling.</summary>
/// <param name="Tokens">The postfix tokens, in evaluation order.</param>
[JsonConverter(typeof(WorldValueExpressionJsonConverter))]
public sealed record WorldValueExpression(IReadOnlyList<WorldValueToken> Tokens) {
    /// <summary>Gets the infix spelling this expression was authored in, or <see langword="null"/> for one authored
    /// as tokens; the serializer writes whichever spelling is present.</summary>
    [JsonIgnore]
    public string? Text { get; init; }

    /// <summary>Parses an infix spelling.</summary>
    /// <param name="text">The spelling.</param>
    /// <returns>The expression, carrying <paramref name="text"/> as its <see cref="Text"/>.</returns>
    /// <exception cref="FormatException">The spelling does not parse.</exception>
    public static WorldValueExpression Parse(string text) =>
        (WorldExpressionSyntax.TryParse(text: text, tokens: out var tokens, error: out var error)
            ? new WorldValueExpression(Tokens: tokens) { Text = text }
            : throw new FormatException(message: $"expression \"{text}\" {error}"));
}
/// <summary>One authored token in a <see cref="WorldValueExpression"/>.</summary>
[JsonDerivedType(typeof(WorldValueToken.Constant), typeDiscriminator: "constant")]
[JsonDerivedType(typeof(WorldValueToken.State), typeDiscriminator: "state")]
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

/// <summary>Converts an exact authored decimal literal into the Q48.16 fixed-point carrier a state cell holds — the one
/// conversion every <see cref="WorldValueToken.Constant"/>, table value, and authored fixed literal crosses, so the
/// rounding is decided in exactly one place.</summary>
public static class WorldStateNumericLiteral {
    /// <summary>Converts a decimal literal, throwing when it lies outside the Q48.16 range.</summary>
    /// <param name="value">The exact decimal literal.</param>
    /// <returns>The fixed-point value.</returns>
    /// <exception cref="OverflowException">The literal is outside the Q48.16 state range.</exception>
    public static FixedQ4816 ToFixed(decimal value) => TryToFixed(value: value, result: out var result)
        ? result
        : throw new OverflowException(message: $"The exact decimal literal '{value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}' is outside the Q48.16 state range.");

    /// <summary>Converts a decimal literal, refusing rather than throwing when it lies outside the Q48.16 range.</summary>
    /// <param name="value">The exact decimal literal.</param>
    /// <param name="result">The fixed-point value, when this method returns <see langword="true"/>.</param>
    /// <returns>Whether the literal is representable.</returns>
    public static bool TryToFixed(decimal value, out FixedQ4816 result) => FixedQ4816.TryParse(
        s: value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        provider: System.Globalization.CultureInfo.InvariantCulture,
        result: out result
    );
}
