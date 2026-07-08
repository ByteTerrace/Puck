using Puck.Scene;

namespace Puck.Demo.Overworld;

/// <summary>
/// The room-level COOPERATIVE win: cabinets whose top-16 SRAM regions must XOR to a shared 128-bit target (a meta
/// <see cref="BrickVictoryCondition"/> group). Each frame it reads every member cabinet's region and fires the first time
/// a whole group's XOR reaches its target — the "no cabinet wins alone" gate, enforced at read time (a group only counts
/// when ALL its cabinets are present, so a partial subset can never trip it). Built once from the console list; single-
/// shot per group. Extracted from the overworld node so that node stays under its class-coupling budget.
/// </summary>
internal sealed class MetaVictoryWatch {
    // One group: the cabinets whose regions must XOR to TargetBytes. Fired makes it single-shot; TargetText is the
    // authored GUID, kept for the win log line.
    private sealed class Group {
        public Group(int[] brickIndices, string label, byte[] targetBytes, string targetText) {
            BrickIndices = brickIndices;
            Label = label;
            TargetBytes = targetBytes;
            TargetText = targetText;
        }

        public int[] BrickIndices { get; }
        public string Label { get; }
        public byte[] TargetBytes { get; }
        public string TargetText { get; }
        public bool Fired { get; set; }
    }

    private readonly List<Group> m_groups;

    private MetaVictoryWatch(List<Group> groups) {
        m_groups = groups;
    }

    /// <summary>Builds the watch from a console list, or returns null when no console declares a meta victory. Cabinets
    /// sharing a group id form one XOR set (the validator has already proven a group agrees on its target and that its
    /// shares XOR to it); a single-cabinet group is skipped defensively (a validation error).</summary>
    /// <param name="consoles">The overworld's console sources.</param>
    /// <returns>The watch, or null when there is nothing to watch.</returns>
    public static MetaVictoryWatch? Build(IReadOnlyList<GamingBrickSource> consoles) {
        Dictionary<string, List<int>>? byGroup = null;

        for (var index = 0; (index < consoles.Count); index++) {
            if (consoles[index].Victory is { IsMeta: true } victory) {
                byGroup ??= new Dictionary<string, List<int>>(comparer: StringComparer.Ordinal);

                var key = (victory.Group ?? "");

                if (!byGroup.TryGetValue(key: key, value: out var indices)) {
                    indices = [];
                    byGroup[key] = indices;
                }

                indices.Add(item: index);
            }
        }

        if (byGroup is null) {
            return null;
        }

        var groups = new List<Group>(capacity: byGroup.Count);

        foreach (var (group, indices) in byGroup) {
            if (indices.Count < 2) {
                continue;
            }

            var victory = consoles[indices[0]].Victory!;
            var targetBytes = new byte[VictoryGate.RegionByteCount];

            if (!victory.TryParseTarget(destination: targetBytes)) {
                continue;
            }

            groups.Add(item: new Group(
                brickIndices: [.. indices],
                label: (victory.Label ?? $"meta victory '{((group.Length == 0) ? "(default)" : group)}'"),
                targetBytes: targetBytes,
                targetText: victory.Target
            ));
        }

        return ((groups.Count > 0) ? new MetaVictoryWatch(groups: groups) : null);
    }

    /// <summary>Copies a document console list into a fresh MUTABLE array (returned as an <see cref="IReadOnlyList{T}"/>)
    /// — the overworld node's editable console-source copy. The array type lives HERE (not the node) so the node stays
    /// under its analyzer coupling ceiling; <see cref="ApplyPendingEdit"/> mutates the same backing array in place.</summary>
    /// <param name="consoles">The document's (immutable) console sources.</param>
    /// <returns>A mutable copy, exposed read-only. The runtime type is a <c>GamingBrickSource[]</c> (an EXPLICIT array,
    /// not a collection-expression target that could synthesize a non-array read-only wrapper), so the in-place
    /// <c>consoles is GamingBrickSource[]</c> writes in <see cref="ApplyPendingEdit"/> succeed.</returns>
    public static IReadOnlyList<GamingBrickSource> CopyConsoles(IReadOnlyList<GamingBrickSource> consoles) {
        var array = new GamingBrickSource[consoles.Count];

        for (var index = 0; (index < array.Length); index++) {
            array[index] = consoles[index];
        }

        return array;
    }

    /// <summary>Applies the frame source's PENDING live condition edit (the <c>condition.set/clear</c> verbs — "the
    /// recursion") end to end, returning the (possibly rebuilt) meta watch the node stores. Drains the edit through the
    /// frame source's primitive out-params, mutates the target BRICK (its setters re-parse, clear the fired one-shot so a
    /// re-edited cabinet may win again, and re-seed a meta share into the running machine), syncs the console-source
    /// record (<c>record with { … }</c>) so the rebuild sees the new group/target/share, and on a VICTORY edit REBUILDS
    /// the watch + runs the transient-invalid <see cref="WarnOnInconsistentGroups"/> WARN. Owning the whole flow here
    /// keeps the Scene victory-condition type off the overworld node's coupling set (its CA1506 ceiling). No pending edit
    /// → returns <paramref name="current"/> unchanged.</summary>
    /// <param name="source">The overworld frame source (the control host holding the queued edit).</param>
    /// <param name="bricks">The cabinets' render children (indexed by console index).</param>
    /// <param name="consoles">The node's mutable console-source copy (from <see cref="CopyConsoles"/>).</param>
    /// <param name="current">The current meta watch.</param>
    /// <returns>The rebuilt watch on a victory edit, else <paramref name="current"/>.</returns>
    public static MetaVictoryWatch? ApplyPendingEdit(OverworldFrameSource source, GamingBrickChildNode[] bricks, IReadOnlyList<GamingBrickSource> consoles, MetaVictoryWatch? current) {
        if (!source.TryConsumeConditionEdit(index: out var index, exitSet: out var exitSet, exit: out var exit, victorySet: out var victorySet, victory: out var victory)) {
            return current;
        }

        if ((index < 0) || (index >= bricks.Length)) {
            return current;
        }

        if (exitSet) {
            bricks[index].SetExitCondition(condition: exit);

            if (consoles is GamingBrickSource[] exitArray) {
                exitArray[index] = (exitArray[index] with { Exit = exit });
            }
        }

        if (!victorySet) {
            return current;
        }

        bricks[index].SetVictoryCondition(condition: victory);

        if (consoles is GamingBrickSource[] victoryArray) {
            victoryArray[index] = (victoryArray[index] with { Victory = victory });
        }

        // The watch reads the console list ONCE into fixed Groups at Build time, so a live victory edit needs a full
        // rebuild over the updated console records — the console-source sync the recursion needs — then the WARN.
        var rebuilt = Build(consoles: consoles);

        WarnOnInconsistentGroups(consoles: consoles);

        return rebuilt;
    }

    /// <summary>Formats every cabinet's live EXIT condition for the <c>condition.show</c> echo (index = console index) —
    /// static here so the overworld node names no extra type for the snapshot.</summary>
    /// <param name="bricks">The cabinets' render children.</param>
    /// <returns>Per-cabinet exit descriptions.</returns>
    public static string[] DescribeExits(GamingBrickChildNode[] bricks) {
        var result = new string[bricks.Length];

        for (var index = 0; (index < bricks.Length); index++) {
            result[index] = DescribeExit(condition: bricks[index].ExitCondition);
        }

        return result;
    }

    /// <summary>Formats every cabinet's live VICTORY condition for the <c>condition.show</c> echo (index = console
    /// index).</summary>
    /// <param name="bricks">The cabinets' render children.</param>
    /// <returns>Per-cabinet victory descriptions.</returns>
    public static string[] DescribeVictories(GamingBrickChildNode[] bricks) {
        var result = new string[bricks.Length];

        for (var index = 0; (index < bricks.Length); index++) {
            result[index] = DescribeVictory(condition: bricks[index].VictoryCondition);
        }

        return result;
    }

    /// <summary>The re-validation WARN for a LIVE condition edit ("the recursion" — <c>condition.set</c>): re-runs the
    /// cross-cabinet XOR consistency the document validator only runs at LOAD, and warns to stderr for each meta group
    /// whose shares no longer XOR to the target (or has fewer than two cabinets). RE-VALIDATION POLICY: it never
    /// REFUSES — a transient-invalid group simply never fires; the dev/authoring path stays ungated, so a self-locking
    /// edit is always recoverable (edit the shares back, or clear the condition). Static so the overworld node gains no
    /// Dictionary/List coupling at its analyzer ceiling. Mirrors <c>NodeDocument.ValidateMetaVictoryGroups</c>, but warns
    /// instead of erroring.</summary>
    /// <param name="consoles">The (edited) console sources.</param>
    public static void WarnOnInconsistentGroups(IReadOnlyList<GamingBrickSource> consoles) {
        var byGroup = new Dictionary<string, List<int>>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < consoles.Count); index++) {
            if (consoles[index].Victory is { IsMeta: true } victory) {
                var key = (victory.Group ?? "");

                if (!byGroup.TryGetValue(key: key, value: out var indices)) {
                    indices = [];
                    byGroup[key] = indices;
                }

                indices.Add(item: index);
            }
        }

        foreach (var (group, indices) in byGroup) {
            var groupLabel = ((group.Length == 0) ? "(default)" : group);

            if (indices.Count < 2) {
                Console.Error.WriteLine(value: $"[condition] warning: meta group '{groupLabel}' has only {indices.Count} cabinet(s) — a meta victory needs at least two, so it can never fire until another cabinet joins the group.");

                continue;
            }

            var target = new byte[VictoryGate.RegionByteCount];
            var accumulator = new byte[VictoryGate.RegionByteCount];
            var allParsed = consoles[indices[0]].Victory!.TryParseTarget(destination: target);

            foreach (var index in indices) {
                var share = new byte[VictoryGate.RegionByteCount];

                if (consoles[index].Victory!.TryParseShare(destination: share)) {
                    VictoryGate.Xor(accumulator: accumulator, operand: share);
                }
                else {
                    allParsed = false;
                }
            }

            if (allParsed && !VictoryGate.RegionEquals(region: accumulator, target: target)) {
                Console.Error.WriteLine(value: $"[condition] warning: meta group '{groupLabel}' shares no longer XOR to the target — the cooperative win is currently UNREACHABLE (edit a share so the group XORs back to the target). The gate simply won't fire; the dev/authoring path is unaffected.");
            }
        }
    }

    // Formats a cabinet's fourth-wall EXIT condition for the condition.show echo, or "(none)".
    private static string DescribeExit(BrickExitCondition? condition) =>
        ((condition is { } exit) ? $"{exit.Address}{exit.Op}{exit.Value}" : "(none)");

    // Formats a cabinet's 128-bit VICTORY condition for the condition.show echo, or "(none)".
    private static string DescribeVictory(BrickVictoryCondition? condition) =>
        ((condition is { } victory)
            ? $"{victory.Mode}(target={victory.Target}{((victory.Share is { } s) ? $",share={s}" : "")}{((victory.Group is { } g) ? $",group={g}" : "")})"
            : "(none)");

    /// <summary>Polls every unfired group against the current cabinet regions and returns the console index to reveal the
    /// first time a group completes (its cabinets all present and their XOR equal to the target), or <c>-1</c> for no
    /// win this frame. A completed group is marked fired and never re-reports.</summary>
    /// <param name="bricks">The cabinets' render children (indexed by console index).</param>
    /// <returns>The triggering console index, or <c>-1</c>.</returns>
    public int Poll(GamingBrickChildNode[] bricks) {
        Span<byte> region = stackalloc byte[VictoryGate.RegionByteCount];
        Span<byte> accumulator = stackalloc byte[VictoryGate.RegionByteCount];

        foreach (var group in m_groups) {
            if (group.Fired) {
                continue;
            }

            accumulator.Clear();

            var allPresent = true;

            foreach (var brickIndex in group.BrickIndices) {
                if (!bricks[brickIndex].TryReadVictoryRegion(destination: region)) {
                    allPresent = false;

                    break;
                }

                VictoryGate.Xor(accumulator: accumulator, operand: region);
            }

            if (allPresent && VictoryGate.RegionEquals(region: accumulator, target: group.TargetBytes)) {
                group.Fired = true;

                Console.Error.WriteLine(value: $"[win] {group.Label}: XOR of {group.BrickIndices.Length} cabinets == {group.TargetText} — breaking the wall.");

                return group.BrickIndices[0];
            }
        }

        return -1;
    }
}
