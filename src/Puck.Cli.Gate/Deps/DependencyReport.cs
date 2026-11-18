using System.Text;

namespace Puck.Cli.Deps;

/// <summary>A dependency a run moved (or, under <c>--check</c>, would move).</summary>
/// <param name="Ecosystem">The ecosystem's name, as <see cref="DependencyEcosystems"/> spells it.</param>
/// <param name="Name">The package, tool, action or SDK.</param>
/// <param name="From">The version before; <c>-</c> when the dependency is new to the resolved set.</param>
/// <param name="To">The version after; <c>-</c> when the dependency left the resolved set.</param>
public sealed record DependencyMove(string Ecosystem, string Name, string From, string To);

/// <summary>A dependency a run left where it was although a newer stable version exists.</summary>
/// <param name="Ecosystem">The ecosystem's name.</param>
/// <param name="Name">The package, tool, action or SDK.</param>
/// <param name="Current">What the dependency is declared or pinned at.</param>
/// <param name="Available">The newest stable version.</param>
/// <param name="Reason">Why it stayed: the declared range's bound or an exact pin.</param>
public sealed record DependencyHold(string Ecosystem, string Name, string Current, string Available, string Reason);

/// <summary>What one <c>puck deps update</c> run moved and what it held back, in the order the ecosystems ran.
/// Renders the console lines and the Markdown a pull request carries as its body.</summary>
public sealed class DependencyReport {
    private readonly List<DependencyHold> m_holds = [];
    private readonly List<DependencyMove> m_moves = [];

    /// <summary>Gets the dependencies that moved.</summary>
    public IReadOnlyList<DependencyMove> Moves => m_moves;
    /// <summary>Gets the dependencies left behind a newer stable version.</summary>
    public IReadOnlyList<DependencyHold> Holds => m_holds;

    /// <summary>Records a held dependency.</summary>
    /// <param name="hold">The hold.</param>
    public void Add(DependencyHold hold) => m_holds.Add(item: hold);
    /// <summary>Records a moved dependency.</summary>
    /// <param name="move">The move.</param>
    public void Add(DependencyMove move) => m_moves.Add(item: move);
    /// <summary>Records a move for every package whose resolved versions differ between two lock readings, in package order.</summary>
    /// <param name="ecosystem">The ecosystem's name.</param>
    /// <param name="before">Each package with the versions it resolved to before.</param>
    /// <param name="after">Each package with the versions it resolves to now.</param>
    /// <param name="scope">What to append to each name, such as the project that holds the lock file; <see langword="null"/> for none.</param>
    public void AddDifferences(string ecosystem, IReadOnlyDictionary<string, SortedSet<string>> before, IReadOnlyDictionary<string, SortedSet<string>> after, string? scope = null) {
        foreach (var name in before.Keys.Union(second: after.Keys, comparer: StringComparer.OrdinalIgnoreCase).Order(comparer: StringComparer.OrdinalIgnoreCase)) {
            before.TryGetValue(key: name, value: out var from);
            after.TryGetValue(key: name, value: out var to);
            if ((from is not null) && (to is not null) && from.SetEquals(other: to)) { continue; }
            m_moves.Add(item: new DependencyMove(Ecosystem: ecosystem, From: ((from is null) ? "-" : string.Join(separator: ", ", values: from)), Name: ((scope is null) ? name : $"{name} ({scope})"), To: ((to is null) ? "-" : string.Join(separator: ", ", values: to))));
        }
    }
    /// <summary>Writes the report as Markdown: one table per ecosystem of what moved, then what was held.</summary>
    /// <param name="dryRun">Whether the run only checked, so every verb reads "would move".</param>
    /// <returns>The Markdown text, ending in a newline.</returns>
    public string ToMarkdown(bool dryRun) {
        var text = new StringBuilder();

        text.Append(value: (m_moves.Count == 0) ? "No dependency moved.\n" : $"{m_moves.Count} {(dryRun ? "would move" : "moved")}:\n");
        foreach (var group in m_moves.GroupBy(keySelector: static move => move.Ecosystem)) {
            text.Append(value: $"\n### {group.Key}\n\n| Name | From | To |\n|---|---|---|\n");
            foreach (var move in group.OrderBy(keySelector: static move => move.Name, comparer: StringComparer.OrdinalIgnoreCase)) {
                text.Append(value: $"| `{move.Name}` | {move.From} | {move.To} |\n");
            }
        }
        if (m_holds.Count > 0) {
            text.Append(value: "\n### Held\n\nNewer stable versions that no declared range admits; a deliberate move is `puck deps update --major`, or an edit of the pin.\n\n| Ecosystem | Name | Current | Available | Why |\n|---|---|---|---|---|\n");
            foreach (var hold in m_holds.OrderBy(keySelector: static hold => hold.Ecosystem, comparer: StringComparer.Ordinal).ThenBy(keySelector: static hold => hold.Name, comparer: StringComparer.OrdinalIgnoreCase)) {
                text.Append(value: $"| {hold.Ecosystem} | `{hold.Name}` | {hold.Current} | {hold.Available} | {hold.Reason} |\n");
            }
        }

        return text.ToString();
    }
    /// <summary>Writes the report as console lines, one per move and hold.</summary>
    /// <param name="writer">Where the lines go.</param>
    /// <param name="dryRun">Whether the run only checked.</param>
    public void Write(TextWriter writer, bool dryRun) {
        foreach (var move in m_moves) { writer.WriteLine(value: $"deps: {(dryRun ? "would move" : "moved")} {move.Ecosystem} {move.Name} {move.From} -> {move.To}"); }
        foreach (var hold in m_holds) { writer.WriteLine(value: $"deps: held {hold.Ecosystem} {hold.Name} {hold.Current} (newest {hold.Available}): {hold.Reason}"); }
        writer.WriteLine(value: $"deps: {m_moves.Count} {(dryRun ? "would move" : "moved")}, {m_holds.Count} held.");
    }
}
