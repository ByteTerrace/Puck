using System.Globalization;
using Puck.Maths;

namespace Puck.State;

/// <summary>The authored spelling of a state enum token, as a refusal message must render it.</summary>
/// <remarks>A refusal quotes the token the author wrote in the document, not the CLR name — one home so a validator
/// refusal and a runtime refusal about the same value never disagree on how it is spelled.</remarks>
public static class StateSpelling {
    /// <summary>The document spelling of a <see cref="Puck.State.CycleOutput"/> — the enum's own name, as the strict
    /// enum converter reads and writes it.</summary>
    /// <param name="output">The cycle output.</param>
    /// <returns>The output's authored token.</returns>
    public static string CycleOutput(CycleOutput output) => output.ToString();
    /// <summary>Describes the authored spelling of a generator source shape.</summary>
    /// <param name="source">The source shape.</param>
    /// <returns>The source's authored token.</returns>
    public static string GeneratorSource(GeneratorSource source) =>
        (char.ToLowerInvariant(c: source.ToString()[0]) + source.ToString()[1..]);
    /// <summary>Describes the authored spelling of a cell kind — the declared member name, as the strict enum
    /// converter reads and writes it.</summary>
    /// <param name="kind">The cell kind.</param>
    /// <returns>The kind's authored token.</returns>
    public static string Kind(CellKind kind) => kind.ToString();
    /// <summary>Describes one cell value as a read-back renders it, with one spelling per case.</summary>
    /// <param name="value">The value to spell.</param>
    /// <returns>The value's read-back spelling.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> holds no case.</exception>
    /// <remarks>A vector is spelled by its width and digest rather than its components: a read-back line is for a
    /// person, and the components are read with the vector verbs.</remarks>
    public static string Value(CellValue value) => value.Kind switch {
        CellKind.Int => value.AsInt.ToString(provider: CultureInfo.InvariantCulture),
        CellKind.Fixed => FixedQ4816.FromRawBits(value: value.AsFixed).ToString(),
        CellKind.Bool => (value.AsBool
            ? "true"
            : "false"),
        CellKind.Text => $"'{value.AsText}'",
        CellKind.Vector => $"vector[{value.AsVector.Length.ToString(provider: CultureInfo.InvariantCulture)}] #{StateVector.ComputeDigest(components: value.AsVector.Span):x8}",
        _ => throw new InvalidOperationException(message: $"Unknown cell kind '{value.Kind}'."),
    };
}
