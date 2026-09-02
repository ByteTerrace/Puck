namespace Puck.World;

/// <summary>The authored spelling of an enum token, as a refusal message must render it.</summary>
/// <remarks>A refusal quotes the token the author WROTE in the document, not the CLR name — one home so a validator
/// refusal and a runtime refusal about the same value never disagree on how it is spelled.</remarks>
internal static class WorldRefusalSpelling {
    /// <summary>Describes the authored spelling of a cell kind.</summary>
    /// <param name="kind">The cell kind.</param>
    /// <returns>The kind's authored token.</returns>
    internal static string Kind(CellKind kind) => kind.ToString().ToLowerInvariant();
    /// <summary>Describes the authored spelling of a generator source shape.</summary>
    /// <param name="source">The source shape.</param>
    /// <returns>The source's authored token.</returns>
    internal static string GeneratorSource(WorldGeneratorSource source) =>
        (char.ToLowerInvariant(c: source.ToString()[0]) + source.ToString()[1..]);
    /// <summary>The document spelling of a <see cref="WorldCycleOutput"/> — the enum's own name, as the strict enum
    /// converter reads and writes it.</summary>
    internal static string CycleOutput(WorldCycleOutput output) => output.ToString();
}
