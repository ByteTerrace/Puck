using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>One symbol of a pattern's alphabet: the cell values in <paramref name="Min"/>..<paramref name="Max"/>
/// (inclusive, in the pattern's kind) read as this letter. Symbols may overlap; the refined alphabet splits them.</summary>
/// <param name="Name">The symbol name a pattern node references.</param>
/// <param name="Min">The least value the symbol accepts.</param>
/// <param name="Max">The greatest value the symbol accepts.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PatternSymbol(CellName Name, decimal Min, decimal Max);
/// <summary>One row of the <c>patterns</c> section: a regular language over cell values, compiled once to a
/// deterministic table the <c>$match:</c> operand runs allocation-free, one indexed step per token.</summary>
/// <param name="Name">The pattern name a rule references.</param>
/// <param name="Kind">The numeric kind of the values the word is read from: Int or Fixed.</param>
/// <param name="Symbols">The alphabet, 1..<see cref="PatternCapacity.MaxSymbols"/> named value ranges.</param>
/// <param name="Pattern">The language.</param>
/// <param name="Attribute">For a zone source, the keyed row (over the zone's token domain) whose cell values form the
/// word, in pile order; null reads the source row's own cell values.</param>
/// <param name="Value">For a zone source, an expression in the pattern's kind evaluated once per token in pile order,
/// where a state token keyed <c>$token</c> reads that token's cell of a row keyed over the zone's token domain: the
/// word over a tuple of attributes (<c>suit * 16 + rank</c>) rather than one. Exclusive with <paramref name="Attribute"/>.</param>
/// <param name="MaxStates">The machine-state budget the compile refuses past,
/// 1..<see cref="PatternCapacity.MaxStates"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PatternRow(
    CellName Name,
    CellKind Kind,
    IReadOnlyList<PatternSymbol> Symbols,
    PatternNode Pattern,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Attribute = null,
    int MaxStates = PatternCapacity.DefaultStates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpressionProgram? Value = null
);
