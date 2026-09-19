using System.Text.Json.Serialization;

namespace Puck.State;

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
/// <summary>Representation ceilings for the pattern section.</summary>
public static class PatternCapacity {
    /// <summary>The state budget a row that declares none gets.</summary>
    public const int DefaultStates = 64;
    /// <summary>The most times a <c>repeat</c> node may unroll its item.</summary>
    public const int MaxRepeat = 64;
    /// <summary>The most pattern rows a document declares.</summary>
    public const int MaxRows = 64;
    /// <summary>The state ceiling any row may budget: 256 states over 64 letters is a 64 KiB table.</summary>
    public const int MaxStates = 256;
    /// <summary>The most named symbols in one alphabet.</summary>
    public const int MaxSymbols = 32;
    /// <summary>The longest word one read walks: every source row fits, so a read is always decided.</summary>
    public const int MaxWord = TopologyCompilation.MaxCells;
}
