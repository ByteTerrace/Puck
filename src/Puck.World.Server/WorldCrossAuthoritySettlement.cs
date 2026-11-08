namespace Puck.World.Server;

/// <summary>Deterministically selects one authority to settle a cross-authority interaction. Both authorities
/// evaluate the same unordered pair of authority identities and the interaction name, so there is exactly one
/// responder without a host leader, packet-order race, or topology preference.</summary>
/// <remarks>The choice reads only the two authority identities and the interaction, never an entity's index or
/// generation: a neighbour mints those for its own bodies and could respawn one until the hash favoured it. The local
/// identity is this authority's own; the remote one is the identity its link settled on
/// (<see cref="IWorldAdjacencyNeighbour.Authority"/>), so for one link and interaction the responder is fixed. A
/// neighbour that chooses its identity before linking can still choose which side of a pair it lands on until
/// authority identities are attested.</remarks>
public static class WorldCrossAuthoritySettlement {
    private static ulong PairHash(string lower, string higher, string interaction) {
        var hash = Puck.Maths.Fnv1aHash.Create();

        WorldDeterministicHash.AddUtf8(hash: ref hash, value: interaction);
        hash.Add(value: ((byte)0xfe));
        WorldDeterministicHash.AddUtf8(hash: ref hash, value: lower);
        hash.Add(value: ((byte)0xff));
        WorldDeterministicHash.AddUtf8(hash: ref hash, value: higher);
        return hash.Value;
    }

    /// <summary>Returns whether <paramref name="localAuthority"/> is the selected responder for
    /// <paramref name="interaction"/> against <paramref name="remoteAuthority"/>. The remote side, evaluating the
    /// same pair from its end, gets the opposite answer; a pair naming one identity twice selects no responder.</summary>
    /// <param name="localAuthority">The evaluating authority's own identity.</param>
    /// <param name="remoteAuthority">The other authority's identity.</param>
    /// <param name="interaction">The interaction's stable name.</param>
    /// <returns><see langword="true"/> when the local authority responds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="localAuthority"/> or <paramref name="remoteAuthority"/>
    /// is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="interaction"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public static bool LocalResponds(string localAuthority, string remoteAuthority, string interaction) {
        ArgumentNullException.ThrowIfNull(argument: localAuthority);
        ArgumentNullException.ThrowIfNull(argument: remoteAuthority);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: interaction);
        var order = string.CompareOrdinal(strA: localAuthority, strB: remoteAuthority);

        if (order == 0) {
            return false;
        }

        var localIsLower = (order < 0);
        var lowerWins = ((PairHash(
            higher: (localIsLower ? remoteAuthority : localAuthority),
            interaction: interaction,
            lower: (localIsLower ? localAuthority : remoteAuthority)
        ) & 1UL) == 0UL);

        return (lowerWins == localIsLower);
    }
}
