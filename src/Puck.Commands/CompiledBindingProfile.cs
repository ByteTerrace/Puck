using System.Collections.Frozen;
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
    // Every lookup here is built once, at compile time, and then read for the whole life of the profile — a group
    // switch, a modifier resolution per raw input signal, a wheel probe per page flip. That is FrozenDictionary's
    // exact shape: pay the construction cost once to make every subsequent read faster than Dictionary's. The
    // comparers are the compiler's own and are load-bearing document semantics, so each is carried across
    // explicitly rather than left to the default (ToFrozenDictionary's no-comparer overload would silently
    // substitute one).
    private readonly int[][] m_commandRowsByGroup;
    private readonly FrozenDictionary<string, int> m_groupIndexByName;
    private readonly ImmutableArray<string> m_groups;
    private readonly FrozenDictionary<string, int> m_modifierIndexBySource;
    private readonly ImmutableArray<BindingModifierDefinition> m_modifiers;
    private readonly int[][] m_pageRowsByGroup;
    private readonly int[] m_restingRowByGroup;
    private readonly CompiledChordRow[] m_rows;
    private readonly FrozenDictionary<int, BindingWheelView> m_wheelViewByRow;

    // One compiled chord row: exactly one of (Table, View) — the page meaning — or Command is present. A PAGE
    // meaning row may additionally carry row-scoped Activators — entries whose trigger is an ordered sequence
    // (BindingActivatorDefinition) rather than a plain Source, so they are excluded from Table and evaluated
    // out-of-band by PagedInputBindings' own RowActivatorTracker per (activator, slot).
    // Held: modifier indices that must be down in any order. Chord: modifier indices pressed in this order, tested
    // on the held order with the Held members removed. Depth = Held.Length + Chord.Length.
    internal sealed record CompiledChordRow(
        int GroupIndex,
        int[] Chord,
        int[] Held,
        FrozenDictionary<string, IReadOnlyList<CommandBinding>>? Table,
        BindingPageView? View,
        CompiledCommandEdge? Command,
        IReadOnlyList<CompiledActivatorEntry>? Activators = null
    );
    // The one precomputed command-edge payload shape shared by a command chord row and a row activator: the press
    // fires Command with PressValue, the release clears it with ReleaseValue (an inactive value of the same kind);
    // DispatchRelease mirrors HoldRelease for a held command and is always true for a toggled channel, whose next
    // completion synthesizes its release. Source is the command destination's precomputed synthetic ownership id:
    // parallel synthesized holds release independently, while all togglers of one destination share its latch owner.
    // Reassertable marks a held channel destination: it may recover continuous state from a digital Active sample
    // without turning that sample into a command edge. Mode remains input-side.
    internal sealed record CompiledCommandEdge(
        string Command,
        bool DispatchRelease,
        CommandValue PressValue,
        CommandValue ReleaseValue,
        bool Reassertable,
        BindingEntryMode Mode,
        string Source
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
    /// <summary>Determines whether a held-modifier order COMPLETES a row: <see cref="Matches"/> holds and the down
    /// set is exactly the row's members — the command-row firing condition.</summary>
    /// <param name="row">The row.</param>
    /// <param name="heldOrder">The held modifier indices, in press order.</param>
    internal static bool Completes(CompiledChordRow row, ReadOnlySpan<int> heldOrder) {
        return (
            (heldOrder.Length == (row.Held.Length + row.Chord.Length)) &&
            Matches(
            heldOrder: heldOrder,
            row: row
        )
        );
    }
    /// <summary>Determines whether a row's members are satisfied by a held-modifier order: every held member is
    /// down, and the chord is a press-order prefix of the order once the held members are removed from it.</summary>
    /// <param name="row">The row.</param>
    /// <param name="heldOrder">The held modifier indices, in press order.</param>
    internal static bool Matches(CompiledChordRow row, ReadOnlySpan<int> heldOrder) {
        foreach (var member in row.Held) {
            if (!heldOrder.Contains(value: member)) {
                return false;
            }
        }

        var chord = row.Chord;
        var matched = 0;

        foreach (var held in heldOrder) {
            if (row.Held.AsSpan().Contains(value: held)) {
                continue;
            }

            if (matched == chord.Length) {
                break;
            }

            if (held != chord[matched]) {
                return false;
            }

            matched++;
        }

        return (matched == chord.Length);
    }
    /// <summary>Resolves the active page row for a group and held-modifier order: the deepest page row
    /// <see cref="Matches"/> accepts (most members; the first declared on a tie), falling back to the group's resting
    /// page. Command rows never answer this — they fire edges, they do not table sources.</summary>
    /// <param name="groupIndex">The active group index.</param>
    /// <param name="heldOrder">The held modifier indices, in press order.</param>
    /// <returns>The resolved page row index.</returns>
    internal int PageRowOf(int groupIndex, ReadOnlySpan<int> heldOrder) {
        var best = m_restingRowByGroup[groupIndex];
        var bestDepth = 0;

        foreach (var rowIndex in m_pageRowsByGroup[groupIndex]) {
            var row = m_rows[rowIndex];
            var depth = (row.Held.Length + row.Chord.Length);

            if (
                (depth <= bestDepth) ||
                !Matches(
                heldOrder: heldOrder,
                row: row
            )
            ) {
                continue;
            }

            best = rowIndex;
            bestDepth = depth;
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
    /// <summary>Gets a page row's binding table, keyed by base source (<c>OrdinalIgnoreCase</c>). Returned as the
    /// concrete <see cref="FrozenDictionary{TKey, TValue}"/> so the per-signal lookup binds directly instead of
    /// dispatching through <see cref="IReadOnlyDictionary{TKey, TValue}"/>.</summary>
    /// <param name="rowIndex">A page-meaning row index.</param>
    internal FrozenDictionary<string, IReadOnlyList<CommandBinding>> TableOf(int rowIndex) {
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

    /// <summary>Gets a group's resting (empty-chord) page id, or <see langword="null"/> when the profile declares no
    /// such group.</summary>
    /// <param name="group">The group name (ordinal comparison).</param>
    public string? RestingPageIdOf(string group) {
        return (TryGetGroup(
            group: group,
            groupIndex: out var groupIndex
        )
            ? m_rows[m_restingRowByGroup[groupIndex]].View?.PageId
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
        int[][] pageRowsByGroup,
        int activatorCount,
        Dictionary<int, BindingWheelView> wheelViewByRow
    ) {
        m_commandRowsByGroup = commandRowsByGroup;
        m_pageRowsByGroup = pageRowsByGroup;
        m_groupIndexByName = groupIndexByName.ToFrozenDictionary(comparer: groupIndexByName.Comparer);
        m_groups = ImmutableArray.CreateRange(items: groups);
        m_modifierIndexBySource = modifierIndexBySource.ToFrozenDictionary(comparer: modifierIndexBySource.Comparer);
        m_modifiers = ImmutableArray.CreateRange(items: modifiers);
        m_restingRowByGroup = restingRowByGroup;
        m_rows = rows;
        m_wheelViewByRow = wheelViewByRow.ToFrozenDictionary();
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
