namespace Puck.State;

/// <summary>The authored spelling of a state enum token, as a refusal message must render it.</summary>
/// <remarks>A refusal quotes the token the author wrote in the document, not the CLR name — one home so a validator
/// refusal and a runtime refusal about the same value never disagree on how it is spelled.</remarks>
public static class StateSpelling {
    /// <summary>Describes the authored spelling of a cell kind.</summary>
    /// <param name="kind">The cell kind.</param>
    /// <returns>The kind's authored token.</returns>
    public static string Kind(CellKind kind) => kind.ToString().ToLowerInvariant();
    /// <summary>Describes the authored spelling of a generator source shape.</summary>
    /// <param name="source">The source shape.</param>
    /// <returns>The source's authored token.</returns>
    public static string GeneratorSource(GeneratorSource source) =>
        (char.ToLowerInvariant(c: source.ToString()[0]) + source.ToString()[1..]);
    /// <summary>The document spelling of a <see cref="Puck.State.CycleOutput"/> — the enum's own name, as the strict
    /// enum converter reads and writes it.</summary>
    /// <param name="output">The cycle output.</param>
    /// <returns>The output's authored token.</returns>
    public static string CycleOutput(CycleOutput output) => output.ToString();
}
