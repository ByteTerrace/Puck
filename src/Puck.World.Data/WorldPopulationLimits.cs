namespace Puck.World;

/// <summary>
/// Engine representation bounds for authored population tables.
/// </summary>
public static class WorldPopulationLimits {
    /// <summary>The largest authored population table the engine will allocate — single-sourced against
    /// <c>Puck.World.Client.WorldClient.EntityCapacity</c>, the client's own fixed per-entity view arrays
    /// (<c>Puck.World.Data</c> cannot name that type, so the reference runs the other way: the client's constant
    /// reads this one). <c>population.capacity</c> is bounded to the client's real array size, so an over-capacity
    /// document refuses at load (<c>WorldDefinitionValidator</c>) rather than booting into a latent throw. 128
    /// already covers the shipped worlds' authored capacity and the owner's ~40-player scale intent with headroom to
    /// spare; raising it again means growing the client's arrays first, never widening this ceiling alone.</summary>
    public const int CapacityCeiling = 128;

    /// <summary>The reserved local-seat count — indices <c>0..LocalSeatCount-1</c> are the up-to-four local players;
    /// later indices host simulated stand-ins and network peers.</summary>
    public const int LocalSeatCount = 4;

    /// <summary>Determines whether <paramref name="index"/> is in the population slice reserved for peer entities.</summary>
    /// <param name="index">The 0-based population entity index.</param>
    /// <returns><see langword="true"/> for indices <see cref="LocalSeatCount"/> through the engine ceiling.</returns>
    public static bool IsPeerIndex(int index) => ((uint)(index - LocalSeatCount) < (CapacityCeiling - LocalSeatCount));
}
