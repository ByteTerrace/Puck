namespace Puck.Commands;

/// <summary>
/// The runtime form of a validated <see cref="BindingProfileDocument"/>: the compiled chord rows (page tables,
/// precomputed <see cref="BindingPageView"/>s, and command-chord edge payloads), the group table, and the
/// per-group resolution helpers. Immutable — a profile edit produces a new compiled instance (via
/// <see cref="BindingProfile.Compile"/>) that <see cref="PagedInputBindings.Reload"/> swaps in atomically.
/// </summary>
/// <remarks>
/// Resolution is group-scoped and prefix-deep: within a slot's active group, the page row with the LONGEST chord
/// that is a press-order prefix of the held modifiers answers the slot's sources (the resting page's empty chord
/// is a prefix of everything, so it is the fallback), and a command row fires when the held order equals its
/// chord exactly. Switching the active group is a pointer-level operation on this compiled instance — no
/// recompose, no recompilation.
/// </remarks>
public sealed class CompiledBindingProfile {
    private readonly int[][] m_commandRowsByGroup;
    private readonly Dictionary<string, int> m_groupIndexByName;
    private readonly string[] m_groups;
    private readonly IReadOnlyList<BindingModifierDefinition> m_modifiers;
    private readonly Dictionary<string, int> m_modifierIndexBySource;
    private readonly int[] m_restingRowByGroup;
    private readonly CompiledChordRow[] m_rows;

    // One compiled chord row: exactly one of (Table, View) — the page meaning — or Command is present.
    internal sealed record CompiledChordRow(
        int GroupIndex,
        int[] Chord,
        IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>>? Table,
        BindingPageView? View,
        CompiledChordCommand? Command
    );

    // A command row's precomputed edge payloads: the press fires the command with PressValue, the release clears
    // it with ReleaseValue (an inactive value of the same kind); DispatchRelease mirrors HoldRelease.
    internal sealed record CompiledChordCommand(
        string Command,
        bool DispatchRelease,
        CommandValue PressValue,
        CommandValue ReleaseValue
    );

    internal CompiledBindingProfile(
        IReadOnlyList<BindingModifierDefinition> modifiers,
        Dictionary<string, int> modifierIndexBySource,
        string[] groups,
        Dictionary<string, int> groupIndexByName,
        CompiledChordRow[] rows,
        int[] restingRowByGroup,
        int[][] commandRowsByGroup
    ) {
        m_commandRowsByGroup = commandRowsByGroup;
        m_groupIndexByName = groupIndexByName;
        m_groups = groups;
        m_modifierIndexBySource = modifierIndexBySource;
        m_modifiers = modifiers;
        m_restingRowByGroup = restingRowByGroup;
        m_rows = rows;
    }

    /// <summary>Gets the index of the DEFAULT group — the first chord row's group, the group a fresh slot resolves in.</summary>
    public int DefaultGroupIndex => 0;

    /// <summary>Gets the group names, in first-declared order (index 0 is the default group).</summary>
    public IReadOnlyList<string> Groups => m_groups;

    /// <summary>Gets the modifier declarations, in document order (a chord references them by index).</summary>
    public IReadOnlyList<BindingModifierDefinition> Modifiers => m_modifiers;

    /// <summary>Gets the number of compiled chord rows.</summary>
    public int RowCount => m_rows.Length;

    /// <summary>Attempts to resolve a group name to its index.</summary>
    /// <param name="group">The group name (ordinal comparison).</param>
    /// <param name="groupIndex">The group's index into <see cref="Groups"/>, when found.</param>
    /// <returns><see langword="true"/> when the profile declares the group.</returns>
    public bool TryGetGroup(string group, out int groupIndex) {
        return m_groupIndexByName.TryGetValue(
            key: group,
            value: out groupIndex
        );
    }

    /// <summary>Gets a group's resting (empty-chord) page row index.</summary>
    /// <param name="groupIndex">The group index, from <c>0</c> to (<see cref="Groups"/>.Count - 1).</param>
    internal int RestingRowOf(int groupIndex) {
        return m_restingRowByGroup[groupIndex];
    }

    /// <summary>Resolves the ACTIVE PAGE row for a group and held-modifier order: the page row with the longest
    /// chord that is a press-order prefix of <paramref name="heldOrder"/>, falling back to the group's resting
    /// page. Command rows never answer this — they fire edges, they do not table sources.</summary>
    /// <param name="groupIndex">The active group index.</param>
    /// <param name="heldOrder">The held modifier indices, in press order.</param>
    /// <returns>The resolved page row index.</returns>
    internal int PageRowOf(int groupIndex, ReadOnlySpan<int> heldOrder) {
        var best = m_restingRowByGroup[groupIndex];
        var bestLength = 0;

        for (var rowIndex = 0; (rowIndex < m_rows.Length); rowIndex++) {
            var row = m_rows[rowIndex];

            if ((row.GroupIndex != groupIndex) || (row.Table is null) || (row.Chord.Length <= bestLength)) {
                continue;
            }

            if (IsPrefix(chord: row.Chord, heldOrder: heldOrder)) {
                best = rowIndex;
                bestLength = row.Chord.Length;
            }
        }

        return best;
    }

    /// <summary>Gets a group's command-meaning row indices, in profile order.</summary>
    /// <param name="groupIndex">The group index.</param>
    internal ReadOnlySpan<int> CommandRowsOf(int groupIndex) {
        return m_commandRowsByGroup[groupIndex];
    }

    /// <summary>Gets a compiled chord row.</summary>
    /// <param name="rowIndex">The row index, from <c>0</c> to (<see cref="RowCount"/> - 1).</param>
    internal CompiledChordRow RowAt(int rowIndex) {
        return m_rows[rowIndex];
    }

    /// <summary>Gets a page row's binding table.</summary>
    /// <param name="rowIndex">A page-meaning row index.</param>
    internal IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> TableOf(int rowIndex) {
        return m_rows[rowIndex].Table!;
    }

    /// <summary>Gets a page row's precomputed UI view.</summary>
    /// <param name="rowIndex">A page-meaning row index.</param>
    internal BindingPageView ViewOf(int rowIndex) {
        return m_rows[rowIndex].View!;
    }

    /// <summary>Attempts to resolve a source to the modifier it drives.</summary>
    /// <param name="source">The provider-neutral input source id.</param>
    /// <param name="modifierIndex">The modifier's index into <see cref="Modifiers"/>, when found.</param>
    /// <returns><see langword="true"/> when the source drives a declared modifier.</returns>
    internal bool TryGetModifier(string source, out int modifierIndex) {
        return m_modifierIndexBySource.TryGetValue(
            key: source,
            value: out modifierIndex
        );
    }

    /// <summary>Whether a chord is a press-order prefix of a held-modifier order.</summary>
    /// <param name="chord">The chord's modifier indices.</param>
    /// <param name="heldOrder">The held modifier indices, in press order.</param>
    internal static bool IsPrefix(ReadOnlySpan<int> chord, ReadOnlySpan<int> heldOrder) {
        return ((chord.Length <= heldOrder.Length) && heldOrder[..chord.Length].SequenceEqual(other: chord));
    }
}
