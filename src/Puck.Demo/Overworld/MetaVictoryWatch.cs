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
