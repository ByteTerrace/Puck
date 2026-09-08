using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>One symbol of a pattern's alphabet: the cell values in <paramref name="Min"/>..<paramref name="Max"/>
/// (inclusive, in the pattern's kind) read as this letter. Symbols may overlap; the refined alphabet splits them.</summary>
/// <param name="Name">The symbol name a pattern node references.</param>
/// <param name="Min">The least value the symbol accepts.</param>
/// <param name="Max">The greatest value the symbol accepts.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PatternSymbol(CellName Name, decimal Min, decimal Max);

/// <summary>The closed pattern vocabulary over a row's cell values, matched against the whole word. Complement and
/// intersection are first-class, so "no two adjacent kings" and "holds a 2 and a 5" are single patterns rather than
/// rule arithmetic.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PatternNode.Symbol), "symbol")]
[JsonDerivedType(typeof(PatternNode.AnySymbol), "any")]
[JsonDerivedType(typeof(PatternNode.Except), "except")]
[JsonDerivedType(typeof(PatternNode.Nothing), "empty")]
[JsonDerivedType(typeof(PatternNode.None), "none")]
[JsonDerivedType(typeof(PatternNode.Sequence), "sequence")]
[JsonDerivedType(typeof(PatternNode.Choice), "choice")]
[JsonDerivedType(typeof(PatternNode.Both), "all")]
[JsonDerivedType(typeof(PatternNode.Complement), "not")]
[JsonDerivedType(typeof(PatternNode.Optional), "optional")]
[JsonDerivedType(typeof(PatternNode.Star), "star")]
[JsonDerivedType(typeof(PatternNode.Plus), "plus")]
[JsonDerivedType(typeof(PatternNode.Repeat), "repeat")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record PatternNode {
    /// <summary>One token whose value falls in the named symbol.</summary>
    public sealed record Symbol(string Name) : PatternNode;
    /// <summary>One token of any value, named symbols and the unnamed remainder alike.</summary>
    public sealed record AnySymbol : PatternNode;
    /// <summary>One token whose value falls outside the named symbol.</summary>
    public sealed record Except(string Name) : PatternNode;
    /// <summary>The empty word.</summary>
    public sealed record Nothing : PatternNode;
    /// <summary>The empty language: no word at all, the zero of choice and the annihilator of sequence.</summary>
    public sealed record None : PatternNode;
    /// <summary>The items matched one after another.</summary>
    public sealed record Sequence(IReadOnlyList<PatternNode> Items) : PatternNode;
    /// <summary>Any one of the items.</summary>
    public sealed record Choice(IReadOnlyList<PatternNode> Items) : PatternNode;
    /// <summary>Every item at once: the word is in each item's language.</summary>
    public sealed record Both(IReadOnlyList<PatternNode> Items) : PatternNode;
    /// <summary>Every word the item does not match.</summary>
    public sealed record Complement(PatternNode Item) : PatternNode;
    /// <summary>The item or nothing.</summary>
    public sealed record Optional(PatternNode Item) : PatternNode;
    /// <summary>The item zero or more times.</summary>
    public sealed record Star(PatternNode Item) : PatternNode;
    /// <summary>The item one or more times.</summary>
    public sealed record Plus(PatternNode Item) : PatternNode;
    /// <summary>The item between <paramref name="Min"/> and <paramref name="Max"/> times.</summary>
    public sealed record Repeat(PatternNode Item, int Min, int Max) : PatternNode;
}

/// <summary>One row of the <c>patterns</c> section: a regular language over cell values, compiled once to a
/// deterministic table the <c>$match:</c> operand runs allocation-free, one indexed step per token.</summary>
/// <param name="Name">The pattern name a rule references.</param>
/// <param name="Kind">The numeric kind of the values the word is read from: Int or Fixed.</param>
/// <param name="Symbols">The alphabet, 1..32 named value ranges.</param>
/// <param name="Pattern">The language.</param>
/// <param name="Attribute">For a zone source, the keyed row (over the zone's token domain) whose cell values form the
/// word, in pile order; null reads the source row's own cell values.</param>
/// <param name="Value">For a zone source, an expression in the pattern's kind evaluated once per token in pile order,
/// where a state token keyed <c>$token</c> reads that token's cell of a row keyed over the zone's token domain: the
/// word over a tuple of attributes (<c>suit * 16 + rank</c>) rather than one. Exclusive with <paramref name="Attribute"/>.</param>
/// <param name="MaxStates">The machine-state budget the compile refuses past, 1..256.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PatternRow(
    CellName Name,
    CellKind Kind,
    IReadOnlyList<PatternSymbol> Symbols,
    PatternNode Pattern,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Attribute = null,
    int MaxStates = PatternCapacity.DefaultStates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueExpression? Value = null
);

/// <summary>Representation ceilings for the pattern section.</summary>
public static class PatternCapacity {
    /// <summary>The most pattern rows a document declares.</summary>
    public const int MaxRows = 64;
    /// <summary>The longest word one read walks: every source row fits, so a read is always decided.</summary>
    public const int MaxWord = TopologyCompilation.MaxCells;
    /// <summary>The most named symbols in one alphabet.</summary>
    public const int MaxSymbols = 32;
    /// <summary>The most times a <c>repeat</c> node may unroll its item.</summary>
    public const int MaxRepeat = 64;
    /// <summary>The state ceiling any row may budget: 256 states over 64 letters is a 64 KiB table.</summary>
    public const int MaxStates = 256;
    /// <summary>The state budget a row that declares none gets.</summary>
    public const int DefaultStates = 64;
}
