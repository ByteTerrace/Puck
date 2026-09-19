using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.State;

/// <summary>The one expression IR: a bounded postfix program a world rule, decision, cartridge step, or flock
/// affinity evaluates. Each instruction either pushes a value or consumes preceding values; the compiler proves
/// stack shape and numeric kind before simulation begins.</summary>
/// <remarks>
/// <para>The <c>.puck</c> grammar, the SQL dialect, and the infix spelling (<see cref="ExpressionSpelling"/>) are
/// front ends that parse to this shape; <c>puck.world.def.v1</c> holds it and nothing else.
/// <c>puck.cartridge.v1</c> spells a program as infix text instead, through
/// <see cref="ExpressionSpellingJsonConverter"/>.</para>
/// <para>A program's own <see cref="Instructions"/> and each of its <see cref="Subprograms"/> are separately bounded
/// by <c>RuleCapacity.MaxExpressionTokens</c>, so a shared subprogram spends the ceiling once rather than once per
/// call site.</para>
/// </remarks>
/// <param name="Instructions">The postfix instructions, in evaluation order.</param>
[JsonConverter(typeof(ExpressionProgramJsonConverter))]
public sealed record ExpressionProgram(IReadOnlyList<Instruction> Instructions) {
    /// <summary>Gets the shared subprograms this program's <see cref="ExpressionOp.Call"/> and fold instructions
    /// index into. The call graph is a directed acyclic graph over at most <c>RuleCapacity.MaxSubprograms</c>
    /// entries: the compiler refuses a cycle by name and compiles each subprogram once, so a call chain nests at
    /// most that many deep at evaluation.</summary>
    public IReadOnlyList<Subprogram> Subprograms { get; init; } = [];
    /// <summary>Parses an infix spelling.</summary>
    /// <param name="text">The spelling.</param>
    /// <returns>The program.</returns>
    /// <exception cref="FormatException">The spelling does not parse.</exception>
    public static ExpressionProgram Parse(string text) =>
        (ExpressionSpelling.TryParse(
            error: out var error,
            program: out var program,
            text: text
        )
            ? program
            : throw new FormatException(message: $"expression \"{text}\" {error}")
        );
}
/// <summary>One shared subprogram of an <see cref="ExpressionProgram"/>: the body a <c>derive</c> value, an authored
/// runtime function, or a fold's per-member expression compiles to.</summary>
/// <remarks>A subprogram reads its call's operands through <see cref="ExpressionOp.Argument"/> and, inside a fold,
/// the member in flight through <see cref="ExpressionOp.Member"/>. It reaches no other caller state, so one
/// subprogram serves every call site and is priced per call.</remarks>
/// <param name="Name">The authored name, for a refusal that quotes what the author wrote.</param>
/// <param name="Arity">How many operands a call consumes; zero for a fold body.</param>
/// <param name="Instructions">The postfix instructions, in evaluation order.</param>
public sealed record Subprogram(string Name, int Arity, IReadOnlyList<Instruction> Instructions);
/// <summary>Converts an exact authored decimal literal into the Q48.16 fixed-point carrier a state cell holds — the
/// one conversion every constant, table value, and authored fixed literal crosses, so the rounding is decided in
/// exactly one place.</summary>
public static class NumericLiteral {
    /// <summary>Converts a decimal literal, throwing when it lies outside the Q48.16 range.</summary>
    /// <param name="value">The exact decimal literal.</param>
    /// <returns>The fixed-point value.</returns>
    /// <exception cref="OverflowException">The literal is outside the Q48.16 state range.</exception>
    public static FixedQ4816 ToFixed(decimal value) => (TryToFixed(
        result: out var result,
        value: value
    )
        ? result
        : throw new OverflowException(message: $"The exact decimal literal '{value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}' is outside the Q48.16 state range.")
    );
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
