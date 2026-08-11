using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Deterministically selects one entity owner to settle a cross-authority interaction. Both authorities
/// evaluate the same canonical address pair and interaction name, so there is exactly one responder without a host
/// leader, packet-order race, or topology preference. Entity generations make the choice sticky only for the
/// lifetime of those concrete occupants.</summary>
public static class WorldCrossAuthoritySettlement {
    /// <summary>Whether <paramref name="local"/>'s owner is the selected responder.</summary>
    public static bool LocalResponds(in WorldEntityAddress local, in WorldEntityAddress remote, string interaction) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: interaction);
        var order = Compare(left: in local, right: in remote);
        if (order == 0) {
            return false;
        }

        var lower = ((order < 0) ? local : remote);
        var higher = ((order < 0) ? remote : local);
        var lowerWins = ((PairHash(lower: in lower, higher: in higher, interaction: interaction) & 1U) == 0U);
        return lowerWins ? (order < 0) : (order > 0);
    }

    private static int Compare(in WorldEntityAddress left, in WorldEntityAddress right) {
        var authority = string.Compare(strA: left.Authority, strB: right.Authority, comparisonType: StringComparison.Ordinal);
        if (authority != 0) { return authority; }
        var index = left.Index.CompareTo(value: right.Index);
        return ((index != 0) ? index : left.Generation.CompareTo(value: right.Generation));
    }

    private static uint PairHash(in WorldEntityAddress lower, in WorldEntityAddress higher, string interaction) {
        const uint offset = 2166136261U;
        const uint prime = 16777619U;
        var hash = AddString(value: offset, text: interaction);
        hash = unchecked((hash ^ 0xfeU) * prime);
        hash = AddString(value: hash, text: lower.Authority);
        hash = AddInt(value: hash, number: lower.Index);
        hash = AddInt(value: hash, number: lower.Generation);
        hash = unchecked((hash ^ 0xffU) * prime);
        hash = AddString(value: hash, text: higher.Authority);
        hash = AddInt(value: hash, number: higher.Index);
        return AddInt(value: hash, number: higher.Generation);
    }

    private static uint AddString(uint value, string text) {
        const uint prime = 16777619U;
        foreach (var character in text) {
            value = unchecked((value ^ (byte)character) * prime);
            value = unchecked((value ^ (byte)(character >> 8)) * prime);
        }
        return value;
    }

    private static uint AddInt(uint value, int number) {
        const uint prime = 16777619U;
        var bits = unchecked((uint)number);
        for (var shift = 0; shift < 32; shift += 8) {
            value = unchecked((value ^ (byte)(bits >> shift)) * prime);
        }
        return value;
    }
}
