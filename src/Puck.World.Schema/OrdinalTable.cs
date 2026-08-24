namespace Puck.World;

/// <summary>The compiled name→ordinal table shape every declared-row vocabulary (channels, target registers, action
/// states, a binding profile's compiled rows, …) compiles once and resolves against: a dense ordinal per name in
/// authored order, plus the reverse name-by-ordinal lookup a read-back needs. Composed by value inside a richer
/// per-consumer table (e.g. <see cref="WorldChannelTable"/> also carries per-ordinal shape/frame/threshold), never
/// used as the whole compiled shape on its own.</summary>
public sealed class OrdinalTable {
    private readonly Dictionary<string, int> m_ordinalByName;
    private readonly string[] m_names;

    private OrdinalTable(Dictionary<string, int> ordinalByName, string[] names) {
        m_ordinalByName = ordinalByName;
        m_names = names;
    }

    /// <summary>Gets the empty table.</summary>
    public static OrdinalTable Empty { get; } = new(
        ordinalByName: new Dictionary<string, int>(comparer: StringComparer.Ordinal),
        names: []
    );

    /// <summary>Gets the declared row count.</summary>
    public int Count => m_names.Length;

    /// <summary>Builds a table assigning each name its position in <paramref name="names"/> as its ordinal.
    /// A name repeated in the sequence throws — <paramref name="names"/> must already be the validated, unique
    /// vocabulary; this is a compile step, not a second validation pass.</summary>
    /// <param name="names">The declared vocabulary in authored order.</param>
    /// <param name="comparer">The name comparer — <see cref="StringComparer.Ordinal"/> for a document-declared
    /// vocabulary, <see cref="StringComparer.OrdinalIgnoreCase"/> where the consumer's own grammar is
    /// case-insensitive (e.g. a binding profile's compiled command table).</param>
    public static OrdinalTable Build(IReadOnlyList<string> names, StringComparer comparer) {
        var ordinalByName = new Dictionary<string, int>(
            capacity: names.Count,
            comparer: comparer
        );
        var table = new string[names.Count];

        for (var ordinal = 0; (ordinal < names.Count); ordinal++) {
            table[ordinal] = names[ordinal];
            ordinalByName.Add(
                key: names[ordinal],
                value: ordinal
            );
        }

        return new OrdinalTable(
            ordinalByName: ordinalByName,
            names: table
        );
    }

    /// <summary>Gets the declared name at <paramref name="ordinal"/>.</summary>
    public string Name(int ordinal) => m_names[ordinal];
    /// <summary>Resolves a declared name to its ordinal.</summary>
    public bool TryGetOrdinal(string name, out int ordinal) => m_ordinalByName.TryGetValue(
        key: name,
        value: out ordinal
    );
}
