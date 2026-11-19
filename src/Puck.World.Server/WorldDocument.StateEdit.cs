namespace Puck.World.Server;

/// <summary>What a mutation's composed candidate changes of the state section: the candidate with every row the
/// mutation did not name restored to the installed document's own declaration, the live values those rows carry into a
/// replacement arena, and the names of the rows the replacement is born with from the candidate.</summary>
/// <param name="Candidate">The candidate whose unnamed rows are the installed declarations. When the mutation named no
/// state at all it holds the installed state section itself.</param>
/// <param name="Carried">The live arena's export of every unnamed row, in document order, or <see langword="null"/>
/// when the whole section is the candidate's (it declares pools, or the mutation changed a part of the section other
/// than its rows).</param>
/// <param name="Named">The rows the mutation added or changed, or <see langword="null"/> exactly when
/// <paramref name="Carried"/> is.</param>
internal readonly record struct WorldStateEdit(WorldDefinition Candidate, IReadOnlyList<StateRow>? Carried, IReadOnlySet<string>? Named);

public sealed partial class WorldDocument {
    /// <summary>Restores every state row a composed candidate did not name to the installed declaration, so an install
    /// changes only the rows the mutation names and the live arena carries the rest.</summary>
    /// <remarks>A mutation composes over <paramref name="current"/>, the installed document carrying the arena's values
    /// (or the installed document itself), and a row it did not write is the very instance <paramref name="current"/>
    /// holds. Such a row's declaration is the installed one, which keeps the per-cell traits of cells the arena no
    /// longer holds, and its values are the arena's. A section that declares pools, or a mutation that changed any part
    /// of the section beside its rows, is the candidate's whole.</remarks>
    /// <param name="candidate">The composed candidate.</param>
    /// <param name="current">The document it was composed over.</param>
    /// <returns>The edit.</returns>
    private WorldStateEdit NameStateEdit(WorldDefinition candidate, WorldDefinition current) {
        var installed = m_definition.StateRaw;
        var composed = candidate.StateRaw;
        var source = current.StateRaw;

        if ((installed is null) || (composed is null) || (source is null) ||
            (installed.Pools is { Count: > 0 }) || (installed.PairPools is { Count: > 0 }) ||
            (composed.Pools is { Count: > 0 }) || (composed.PairPools is { Count: > 0 }) ||
            !SameStateParts(left: composed, right: source) || !SameStateParts(left: source, right: installed)) {
            return new WorldStateEdit(Candidate: candidate, Carried: null, Named: null);
        }

        var before = new Dictionary<string, WorldStateRow>(capacity: (source.World?.Count ?? 0), comparer: StringComparer.Ordinal);
        var declared = new Dictionary<string, WorldStateRow>(capacity: (installed.World?.Count ?? 0), comparer: StringComparer.Ordinal);

        foreach (var row in (source.World ?? [])) { before[row.Name.Value] = row; }
        foreach (var row in (installed.World ?? [])) { declared[row.Name.Value] = row; }

        var after = (composed.World ?? []);
        var rows = new WorldStateRow[after.Count];
        var carried = new List<StateRow>(capacity: after.Count);
        var named = new HashSet<string>(comparer: StringComparer.Ordinal);
        var sameRows = (after.Count == (installed.World?.Count ?? 0));

        for (var index = 0; (index < after.Count); index++) {
            var row = after[index];
            var name = row.Name.Value;

            if (before.TryGetValue(key: name, value: out var exported) && ReferenceEquals(objA: row, objB: exported) &&
                declared.TryGetValue(key: name, value: out var declaration)) {
                // Composed over a document that is not the arena's export (the installed one itself), an unnamed row
                // holds seeds rather than the arena's values, so nothing can be carried.
                if (!declaration.HostOwned && ReferenceEquals(objA: exported, objB: declaration)) {
                    return new WorldStateEdit(Candidate: candidate, Carried: null, Named: null);
                }
                rows[index] = declaration;
                if (!declaration.HostOwned) { carried.Add(item: row); }
            } else {
                rows[index] = row;
                named.Add(item: name);
            }
            sameRows = (sameRows && ReferenceEquals(objA: rows[index], objB: installed.World![index]));
        }

        var state = (sameRows ? installed : (installed with { World = rows }));

        return new WorldStateEdit(Candidate: candidate.WithStateSection(state: state), Carried: carried, Named: named);
    }

    /// <summary>Names what a reload's candidate changes of the state section: every row whose declaration differs from
    /// the installed one is born from the candidate, and every other row carries its live values.</summary>
    /// <remarks>A row is unchanged when it is the installed document's own instance or serializes to the same bytes,
    /// so the rows a reload seeds are decided by content, on a live run and on its replay alike; the edit's candidate
    /// holds the installed declaration of each such row. A host-owned row is carried by its host, never by the arena's
    /// export.</remarks>
    /// <param name="candidate">The reload's candidate.</param>
    /// <returns>The edit, or <see langword="null"/> when the candidate's state installs whole: a section is absent,
    /// either declares pools, or the candidate changes a part of the section other than its rows.</returns>
    private WorldStateEdit? NameReloadEdit(WorldDefinition candidate) {
        var installed = m_definition.StateRaw;
        var composed = candidate.StateRaw;

        if ((installed is null) || (composed is null) ||
            (installed.Pools is { Count: > 0 }) || (installed.PairPools is { Count: > 0 }) ||
            (composed.Pools is { Count: > 0 }) || (composed.PairPools is { Count: > 0 }) ||
            !SameStateParts(left: composed, right: installed)) {
            return null;
        }

        var named = new HashSet<string>(comparer: StringComparer.Ordinal);
        var carried = new List<StateRow>();

        if (!ReferenceEquals(objA: composed, objB: installed)) {
            var declared = new Dictionary<string, WorldStateRow>(capacity: (installed.World?.Count ?? 0), comparer: StringComparer.Ordinal);

            foreach (var row in (installed.World ?? [])) { declared[row.Name.Value] = row; }

            var after = (composed.World ?? []);
            var rows = new WorldStateRow[after.Count];
            var kept = new HashSet<string>(comparer: StringComparer.Ordinal);
            var sameRows = (after.Count == (installed.World?.Count ?? 0));

            for (var index = 0; (index < after.Count); index++) {
                var row = after[index];
                var name = row.Name.Value;

                if (declared.TryGetValue(key: name, value: out var declaration) && (ReferenceEquals(objA: row, objB: declaration) || SameAuthoredRow(left: row, right: declaration))) {
                    rows[index] = declaration;
                    if (!declaration.HostOwned) { kept.Add(item: name); }
                } else {
                    rows[index] = row;
                    named.Add(item: name);
                }
                sameRows = (sameRows && ReferenceEquals(objA: rows[index], objB: installed.World![index]));
            }

            // A section that differs from the installed one in nothing is the installed one, and the install keeps the
            // live arena.
            var state = ((sameRows && ((composed with { World = installed.World }) == installed)) ? installed : (composed with { World = rows }));

            candidate = candidate.WithStateSection(state: state);
            if (!Host.KeepsArena(candidate: candidate) && (kept.Count > 0)) {
                foreach (var row in (Host.MaterializeArena().StateRaw?.World ?? [])) {
                    if (kept.Contains(item: row.Name.Value)) { carried.Add(item: row); }
                }
            }
        }

        return new WorldStateEdit(Candidate: candidate, Carried: carried, Named: named);
    }
    // Whether two state rows are the same row as a document writes it, declaration and seeds alike: their serialized bytes
    // agree. SameComposeDeclaration answers for the declaration alone, which a changed seed leaves standing.
    private static bool SameAuthoredRow(WorldStateRow left, WorldStateRow right) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value: left, jsonTypeInfo: WorldJsonContext.Default.WorldStateRow).AsSpan().SequenceEqual(
            other: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value: right, jsonTypeInfo: WorldJsonContext.Default.WorldStateRow));

    // Every part of a state section beside its rows and pools, by instance: an export and a row edit share them.
    private static bool SameStateParts(WorldStateSection left, WorldStateSection right) => (
        ReferenceEquals(objA: left.Body, objB: right.Body) && ReferenceEquals(objA: left.Identity, objB: right.Identity) &&
        ReferenceEquals(objA: left.Lattices, objB: right.Lattices) && ReferenceEquals(objA: left.Spaces, objB: right.Spaces) &&
        ReferenceEquals(objA: left.Enums, objB: right.Enums) && ReferenceEquals(objA: left.Families, objB: right.Families) &&
        ReferenceEquals(objA: left.Records, objB: right.Records)
    );
}
