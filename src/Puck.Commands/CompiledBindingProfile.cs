using System.Collections.Immutable;

namespace Puck.Commands;

/// <summary>
/// The runtime form of a validated <see cref="BindingProfileDocument"/>: the compiled chord rows (page tables,
/// precomputed <see cref="BindingPageView"/>s, and command-chord edge payloads), the group table, and the
/// per-group resolution helpers. Immutable — a profile edit produces a new compiled instance (via
/// <see cref="BindingProfile.Compile"/>) that <see cref="PagedInputBindings.Reload"/> swaps in atomically.
/// </summary>
/// <remarks>
/// Resolution is group-scoped and prefix-deep: within a slot's active group, the page row with the longest chord
/// that is a press-order prefix of the held modifiers answers the slot's sources (the resting page's empty chord
/// is a prefix of everything, so it is the fallback), and a command row fires when the held order equals its
/// chord exactly. Switching the active group is a pointer-level operation on this compiled instance — no
/// recompose, no recompilation.
/// </remarks>
public sealed class CompiledBindingProfile {
    private readonly int[][] m_commandRowsByGroup;
    private readonly Dictionary<string, int> m_groupIndexByName;
    private readonly ImmutableArray<string> m_groups;
    private readonly Dictionary<string, int> m_modifierIndexBySource;
    private readonly ImmutableArray<BindingModifierDefinition> m_modifiers;
    private readonly int[] m_restingRowByGroup;
    private readonly CompiledChordRow[] m_rows;
    private readonly Dictionary<int, BindingWheelView> m_wheelViewByRow;

    // One compiled chord row: exactly one of (Table, View) — the page meaning — or Command is present. A PAGE
    // meaning row may additionally carry row-scoped Activators — entries whose trigger is an ordered sequence
    // (BindingActivatorDefinition) rather than a plain Source, so they are excluded from Table and evaluated
    // out-of-band by PagedInputBindings' own RowActivatorTracker per (activator, slot).
    internal sealed record CompiledChordRow(
        int GroupIndex,
        int[] Chord,
        IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>>? Table,
        BindingPageView? View,
        CompiledCommandEdge? Command,
        IReadOnlyList<CompiledActivatorEntry>? Activators = null
    );
    // The one precomputed command-edge payload shape shared by a command chord row and a row activator: the press
    // fires Command with PressValue, the release clears it with ReleaseValue (an inactive value of the same kind);
    // DispatchRelease mirrors HoldRelease. Reassertable marks a channel destination: it may recover continuous
    // state from a digital Active sample without turning that sample into a command edge.
    internal sealed record CompiledCommandEdge(
        string Command,
        bool DispatchRelease,
        CommandValue PressValue,
        CommandValue ReleaseValue,
        bool Reassertable
    );
    // A compiled row activator: the shared command edge plus the sequence/mode/timeout a RowActivatorTracker
    // resolves, plus a GLOBAL index (0..ActivatorCount-1, unique across the whole compiled profile) a slot's
    // per-activator tracker array is keyed by.
    internal sealed record CompiledActivatorEntry(
        int ActivatorIndex,
        BindingActivatorDefinition Activator,
        CompiledCommandEdge Edge
    );

    /// <summary>Gets a page row's row activators, or an empty list when it declares none.</summary>
    /// <param name="rowIndex">A page-meaning row index.</param>
    internal IReadOnlyList<CompiledActivatorEntry> ActivatorsOf(int rowIndex) {
        return (((IReadOnlyList<CompiledActivatorEntry>?)m_rows[rowIndex].Activators) ?? []);
    }
    /// <summary>Gets a group's command-meaning row indices, in profile order.</summary>
    /// <param name="groupIndex">The group index.</param>
    internal ReadOnlySpan<int> CommandRowsOf(int groupIndex) {
        return m_commandRowsByGroup[groupIndex];
    }
    /// <summary>Determines whether a chord is a press-order prefix of a held-modifier order.</summary>
    /// <param name="chord">The chord's modifier indices.</param>
    /// <param name="heldOrder">The held modifier indices, in press order.</param>
    internal static bool IsPrefix(ReadOnlySpan<int> chord, ReadOnlySpan<int> heldOrder) {
        return (
            (chord.Length <= heldOrder.Length) &&
            heldOrder[..chord.Length].SequenceEqual(other: chord)
        );
    }
    /// <summary>Resolves the active page row for a group and held-modifier order: the page row with the longest
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

            if (
                (row.GroupIndex != groupIndex) ||
                (row.Table is null) ||
                (row.Chord.Length <= bestLength)
            ) {
                continue;
            }

            if (IsPrefix(
                chord: row.Chord,
                heldOrder: heldOrder
            )) {
                best = rowIndex;
                bestLength = row.Chord.Length;
            }
        }

        return best;
    }
    // The press-edge value a command/channel destination dispatches when it declares no explicit Value: a channel
    // contributes its declared scale as an Axis, a plain command an active digital. BindingProfile.Compile builds
    // the real edge from this and BindingVocabularyCheck reads its Kind, so the value-less-press rule has one
    // definition site instead of a copy hand-synced across the two files.
    internal static CommandValue PressValue(CommandValue? explicitValue, float? channelScale) {
        return (explicitValue ?? ((channelScale is { } scale)
            ? CommandValue.Axis(value: scale)
            : CommandValue.Digital(active: true)));
    }
    /// <summary>Gets a group's resting (empty-chord) page row index.</summary>
    /// <param name="groupIndex">The group index, from <c>0</c> to (<see cref="Groups"/>.Count - 1).</param>
    internal int RestingRowOf(int groupIndex) {
        return m_restingRowByGroup[groupIndex];
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
    /// <summary>Gets a page row's precomputed UI view.</summary>
    /// <param name="rowIndex">A page-meaning row index.</param>
    internal BindingPageView ViewOf(int rowIndex) {
        return m_rows[rowIndex].View!;
    }
    /// <summary>Gets the wheel a page row presents while selected — the row is some wheel's hold page — or
    /// <see langword="null"/> for every other row.</summary>
    /// <param name="rowIndex">A page-meaning row index.</param>
    internal BindingWheelView? WheelOfRow(int rowIndex) {
        return (m_wheelViewByRow.TryGetValue(
            key: rowIndex,
            value: out var wheel
        )
            ? wheel
            : null
        );
    }

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

    internal CompiledBindingProfile(
        IReadOnlyList<BindingModifierDefinition> modifiers,
        Dictionary<string, int> modifierIndexBySource,
        string[] groups,
        Dictionary<string, int> groupIndexByName,
        CompiledChordRow[] rows,
        int[] restingRowByGroup,
        int[][] commandRowsByGroup,
        int activatorCount,
        Dictionary<int, BindingWheelView> wheelViewByRow
    ) {
        m_commandRowsByGroup = commandRowsByGroup;
        m_groupIndexByName = groupIndexByName;
        m_groups = ImmutableArray.CreateRange(items: groups);
        m_modifierIndexBySource = modifierIndexBySource;
        m_modifiers = ImmutableArray.CreateRange(items: modifiers);
        m_restingRowByGroup = restingRowByGroup;
        m_rows = rows;
        m_wheelViewByRow = wheelViewByRow;
        ActivatorCount = activatorCount;
    }

    /// <summary>Gets the total number of row activators declared across every page in this profile — the size a
    /// slot's lazily-populated <see cref="RowActivatorTracker"/> array is allocated to.</summary>
    public int ActivatorCount { get; }
    /// <summary>Gets the index of the default group — the first chord row's group, the group a fresh slot resolves in.</summary>
    public int DefaultGroupIndex => 0;
    /// <summary>Gets the group names, in first-declared order (index 0 is the default group).</summary>
    public IReadOnlyList<string> Groups => m_groups;
    /// <summary>Gets the modifier declarations, in document order (a chord references them by index).</summary>
    public IReadOnlyList<BindingModifierDefinition> Modifiers => m_modifiers;
    /// <summary>Gets the number of compiled chord rows.</summary>
    public int RowCount => m_rows.Length;
}
