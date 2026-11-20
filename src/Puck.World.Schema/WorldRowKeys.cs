using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The identity a keyed document section's rows are replaced and removed by: the key composition matches a
/// submitted row or a removal against, and the state reference that key reads when an author bound it.</summary>
/// <remarks>Composition (<c>WorldDocument.TryComposeCore</c>) keys every upsert and removal through one of these, and
/// the write door (<c>WorldRecipientMutations</c>) finds the installed row a write replaces through the same one, so
/// the two cannot key a section differently. A key bound to a state row (a creation's id, a look's name) is its live
/// value, which composition compares and the door never reads: it asks <see cref="ReferenceOf"/> instead, and treats
/// a row with a bound key as one any write to the section may target.
/// <para>A list of at least <see cref="IndexedRowCount"/> rows finds a key through its <see cref="WorldRowIndex{T, TKey}"/>,
/// derived once per list instance (<see cref="IndexOf"/>), rather than a scan: every placement a scene emits or a
/// collider set compiles resolves its creation, so a scan is a row count times a lookup count per pass. The index holds
/// only the rows before the list's first bound key, because a bound key reads a live value that the list's identity
/// cannot vouch for; a miss in a list holding one scans, reading (or throwing for) the bound keys exactly as the scan
/// always has. A key function is therefore a pure function of its row unless <paramref name="referenceOf"/> names the
/// binding it reads.</para></remarks>
/// <typeparam name="T">The row type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
/// <param name="keyOf">Reads a row's key: its resolved or live value.</param>
/// <param name="referenceOf">Reads the state reference a bound key reads, or <see langword="null"/> for a literal
/// key; omitted for a section whose keys are always literal.</param>
public sealed class WorldRowKey<T, TKey>(Func<T, TKey> keyOf, Func<T, string?>? referenceOf = null) where TKey : notnull {
    /// <summary>The row count from which <see cref="Find"/> resolves a key through the list's index: below it,
    /// comparing every row costs less than the weak-table lookup that finds the index.</summary>
    public const int IndexedRowCount = 16;

    private readonly Puck.Abstractions.CodeDerivedTable<IReadOnlyList<T>, WorldRowIndex<T, TKey>> m_indices = new();

    /// <summary>Reads a row's key as composition compares it.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The key.</returns>
    /// <exception cref="InvalidOperationException">The key is bound to a state cell that holds no value, or is an
    /// unresolved reference.</exception>
    public TKey Of(T row) => keyOf(row);
    /// <summary>Returns the state reference a row's key reads, or <see langword="null"/> when the key is literal.</summary>
    /// <param name="row">The row.</param>
    public string? ReferenceOf(T row) => referenceOf?.Invoke(row);
    /// <summary>Returns whether two keys are the same key, as composition compares them.</summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other.</param>
    public static bool Same(TKey left, TKey right) => EqualityComparer<TKey>.Default.Equals(left, right);
    /// <summary>Finds the first row under a key, as composition's upsert and removal match it.</summary>
    /// <param name="rows">The section's rows, or <see langword="null"/>.</param>
    /// <param name="key">The key.</param>
    /// <returns>The row, or the default value (<see langword="null"/> for a reference row) when none matches.</returns>
    /// <exception cref="InvalidOperationException">A row's key is bound to a state cell that holds no value, or is an
    /// unresolved reference.</exception>
    public T? Find(IReadOnlyList<T>? rows, TKey key) {
        if (rows is null) { return default; }
        if (rows.Count >= IndexedRowCount) {
            var index = IndexOf(rows: rows);

            if (index.TryGetPosition(key: key, position: out var position)) { return rows[position]; }
            if (!index.Bound) { return default; }
        }
        for (var position = 0; position < rows.Count; position++) {
            var row = rows[position];
            if ((row is not null) && Same(keyOf(row), key)) { return row; }
        }
        return default;
    }
    /// <summary>Gets a list's index, derived once per list instance, and again should the list have grown since it
    /// was indexed (composition reads lists it is still appending to).</summary>
    /// <param name="rows">The section's rows.</param>
    /// <returns>The index, the first row of a key winning as in <see cref="Find"/>'s scan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is <see langword="null"/>.</exception>
    /// <remarks>The invariant is that an indexed list is never changed in place: a document's rows are replaced
    /// wholesale, a new list per edit. Growth is detected; a row replaced in place is not, and nothing enforces that
    /// an arbitrary <see cref="IReadOnlyList{T}"/> stays unchanged, so a caller that edits a list it has already
    /// passed here reads a stale index.</remarks>
    public WorldRowIndex<T, TKey> IndexOf(IReadOnlyList<T> rows) {
        ArgumentNullException.ThrowIfNull(argument: rows);

        if (m_indices.TryGetValue(key: rows, value: out var held) && (held.RowCount == rows.Count)) {
            return held;
        }

        var built = new WorldRowIndex<T, TKey>(keyOf: keyOf, referenceOf: referenceOf, rows: rows);

        m_indices.AddOrUpdate(key: rows, value: built);

        return built;
    }
}

/// <summary>A keyed section's rows by key, derived from one list instance by <see cref="WorldRowKey{T, TKey}.IndexOf"/>:
/// each key's first position over the rows before the list's first bound key, read as a dictionary of those rows. A
/// null row and a null key are skipped.</summary>
/// <typeparam name="T">The row type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class WorldRowIndex<T, TKey> : IReadOnlyDictionary<TKey, T> where TKey : notnull {
    private readonly IReadOnlyList<T> m_rows;
    private readonly Dictionary<TKey, int> m_positions;

    internal WorldRowIndex(IReadOnlyList<T> rows, Func<T, TKey> keyOf, Func<T, string?>? referenceOf) {
        m_rows = rows;
        RowCount = rows.Count;
        m_positions = new Dictionary<TKey, int>(capacity: rows.Count);

        for (var position = 0; (position < rows.Count); position++) {
            var row = rows[position];
            if (row is null) { continue; }
            if (referenceOf?.Invoke(arg: row) is not null) {
                Bound = true;

                break;
            }
            if (keyOf(arg: row) is { } key) { _ = m_positions.TryAdd(key: key, value: position); }
        }
    }

    /// <summary>Gets whether the list holds a bound key, at and past which nothing is indexed.</summary>
    public bool Bound { get; }
    /// <summary>Gets the number of rows the list held when it was indexed.</summary>
    public int RowCount { get; }
    /// <summary>Gets the number of keys indexed.</summary>
    public int Count => m_positions.Count;
    /// <summary>Gets the keys indexed, in no particular order.</summary>
    public IEnumerable<TKey> Keys => m_positions.Keys;
    /// <summary>Gets each key's first row, in no particular order.</summary>
    public IEnumerable<T> Values {
        get {
            foreach (var position in m_positions.Values) { yield return m_rows[position]; }
        }
    }

    /// <summary>Gets a key's first row.</summary>
    /// <param name="key">The key.</param>
    /// <exception cref="KeyNotFoundException">No indexed row has the key.</exception>
    public T this[TKey key] => m_rows[m_positions[key]];

    /// <summary>Returns whether an indexed row has the key.</summary>
    /// <param name="key">The key.</param>
    public bool ContainsKey(TKey key) => m_positions.ContainsKey(key: key);
    /// <summary>Enumerates each key with its first row.</summary>
    public IEnumerator<KeyValuePair<TKey, T>> GetEnumerator() {
        foreach (var (key, position) in m_positions) { yield return new KeyValuePair<TKey, T>(key: key, value: m_rows[position]); }
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    /// <summary>Finds a key's first position in the indexed list.</summary>
    /// <param name="key">The key.</param>
    /// <param name="position">The position, or -1.</param>
    /// <returns><see langword="true"/> when an indexed row has the key.</returns>
    public bool TryGetPosition(TKey key, out int position) {
        if (m_positions.TryGetValue(key: key, value: out position)) { return true; }
        position = -1;
        return false;
    }
    /// <summary>Gets a key's first row.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The row, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when an indexed row has the key.</returns>
    public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(returnValue: false)] out T value) {
        if (m_positions.TryGetValue(key: key, value: out var position)) {
            value = m_rows[position];
            return true;
        }
        value = default;
        return false;
    }
}

/// <summary>The one table of keyed document sections: each section's <see cref="WorldRowKey{T, TKey}"/>, which
/// composition and the write door both read.</summary>
public static class WorldRowKeys {
    /// <summary>Kits, by name.</summary>
    public static WorldRowKey<WorldKit, string> Kits { get; } = new(static kit => kit.Name);
    /// <summary>Machines, by name.</summary>
    public static WorldRowKey<WorldMachine, string> Machines { get; } = new(static machine => machine.Name);
    /// <summary>Screens, by their engine index: a screen has no name, and its index is not its list position.</summary>
    public static WorldRowKey<WorldScreen, int> Screens { get; } = new(static screen => screen.Index);
    /// <summary>Cameras, by name.</summary>
    public static WorldRowKey<WorldCamera, string> Cameras { get; } = new(static camera => camera.Name);
    /// <summary>Mounted addons, by name.</summary>
    public static WorldRowKey<WorldAddonRow, string> Addons { get; } = new(static addon => addon.Name);
    /// <summary>Creations, by the id's value, which a bound id reads from its state cell.</summary>
    public static WorldRowKey<WorldPrototype, string> Creations { get; } = new(static creation => creation.Id?.Value!, static creation => creation.Id?.Reference);
    /// <summary>Placements, by id.</summary>
    public static WorldRowKey<WorldPlacement, string> Placements { get; } = new(static placement => placement.Id);
    /// <summary>Speakers, by name.</summary>
    public static WorldRowKey<WorldSpeaker, string> Speakers { get; } = new(static speaker => speaker.Name);
    /// <summary>Tunes, by name.</summary>
    public static WorldRowKey<WorldTune, string> Tunes { get; } = new(static tune => tune.Name);
    /// <summary>Synth patches, by name.</summary>
    public static WorldRowKey<WorldPatch, string> Patches { get; } = new(static patch => patch.Name);
    /// <summary>Binding overlays, by id.</summary>
    public static WorldRowKey<WorldBindingOverlay, string> BindingOverlays { get; } = new(static overlay => overlay.Id);
    /// <summary>Looks, by the name's value, which a bound name reads from its state cell.</summary>
    public static WorldRowKey<WorldLook, string> Looks { get; } = new(static look => look.Name?.Value!, static look => look.Name?.Reference);
    /// <summary>Dynamics rows, by name.</summary>
    public static WorldRowKey<DynamicsRow, string> Dynamics { get; } = new(static row => row.Name);
    /// <summary>Curve rows, by name.</summary>
    public static WorldRowKey<WorldCurveRow, string> Curves { get; } = new(static row => row.Name);
    /// <summary>View layouts, by name.</summary>
    public static WorldRowKey<WorldViewLayout, string> ViewLayouts { get; } = new(static layout => layout.Name);
    /// <summary>View graph instances, by name.</summary>
    public static WorldRowKey<WorldViewGraph, string> ViewGraphs { get; } = new(static graph => graph.Name);
    /// <summary>HUD panels, by id.</summary>
    public static WorldRowKey<WorldHudPanel, string> HudPanels { get; } = new(static panel => panel.Id);
    /// <summary>HUD elements within their panel, by id.</summary>
    public static WorldRowKey<WorldHudElement, string> HudElements { get; } = new(static element => element.Id);
    /// <summary>State rows, by name.</summary>
    public static WorldRowKey<WorldStateRow, string> StateRows { get; } = new(static row => row.Name.Value);
    /// <summary>World rules, by name.</summary>
    public static WorldRowKey<WorldRule, string> Rules { get; } = new(static rule => rule.Name);
    /// <summary>Interactions, by name.</summary>
    public static WorldRowKey<WorldInteraction, string> Interactions { get; } = new(static row => row.Name);
    /// <summary>Grants, by grantee, capability and subject.</summary>
    public static WorldRowKey<WorldGrant, (Grantee Grantee, WorldCapability Capability, GrantSubject Subject)> Grants { get; } =
        new(static grant => (grant.Grantee, grant.Capability, grant.Subject));
    /// <summary>Group kinds, by name.</summary>
    public static WorldRowKey<WorldGroupKind, string> GroupKinds { get; } = new(static kind => kind.Name);
    /// <summary>Groups, by id.</summary>
    public static WorldRowKey<WorldGroup, string> Groups { get; } = new(static group => group.Id);
    /// <summary>Ownership rows, by subject.</summary>
    public static WorldRowKey<WorldOwnership, OwnershipSubject> Ownership { get; } = new(static row => row.Subject);
    /// <summary>Registered property names, by themselves.</summary>
    public static WorldRowKey<string, string> PropertyNames { get; } = new(static name => name);
}
