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
        var lowerWins = ((PairHash(lower: in lower, higher: in higher, interaction: interaction) & 1UL) == 0UL);
        return lowerWins ? (order < 0) : (order > 0);
    }

    private static int Compare(in WorldEntityAddress left, in WorldEntityAddress right) {
        var authority = string.Compare(strA: left.Authority, strB: right.Authority, comparisonType: StringComparison.Ordinal);
        if (authority != 0) { return authority; }
        var index = left.Index.CompareTo(value: right.Index);
        return ((index != 0) ? index : left.Generation.CompareTo(value: right.Generation));
    }

    private static ulong PairHash(in WorldEntityAddress lower, in WorldEntityAddress higher, string interaction) {
        var hash = Puck.Maths.Fnv1aHash.Create();
        WorldDeterministicHash.AddUtf8(hash: ref hash, value: interaction);
        hash.Add(value: (byte)0xfe);
        WorldDeterministicHash.AddUtf8(hash: ref hash, value: lower.Authority);
        hash.Add(value: unchecked((uint)lower.Index));
        hash.Add(value: unchecked((uint)lower.Generation));
        hash.Add(value: (byte)0xff);
        WorldDeterministicHash.AddUtf8(hash: ref hash, value: higher.Authority);
        hash.Add(value: unchecked((uint)higher.Index));
        hash.Add(value: unchecked((uint)higher.Generation));
        return hash.Value;
    }
}
