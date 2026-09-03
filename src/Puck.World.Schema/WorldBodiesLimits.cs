namespace Puck.World;

/// <summary>
/// Engine representation bounds for authored population tables.
/// </summary>
public static class WorldBodiesLimits {
    /// <summary>The largest authored population table the engine will allocate — single-sourced against
    /// <c>Puck.World.Client.WorldClient.EntityCapacity</c>, the client's own fixed per-entity view arrays
    /// (<c>Puck.World.Schema</c> cannot name that type, so the reference runs the other way: the client's constant
    /// reads this one). <c>population.capacity</c> is bounded to the client's real array size, so an over-capacity
    /// document refuses at load (<c>WorldDefinitionValidator</c>) rather than booting into a latent throw. This is
    /// the current representation limit, not the intended crowd scale. Raising it requires auditing protocol and
    /// state indices, population work, and the renderer's independent capacities, not just widening this constant.</summary>
    public const int CapacityCeiling = 128;
    /// <summary>The maximum local-seat count. Each document reserves only its authored
    /// <see cref="WorldBodiesDefaults.LocalSeats"/>; remaining slots may host inhabitants or peers.</summary>
    public const int LocalSeatCount = 4;

    /// <summary>Determines whether an address fits the engine's population representation, independently of
    /// a document's seat reservation or live occupant. A codec has no world census and cannot decide occupancy.</summary>
    /// <param name="index">The 0-based population entity index.</param>
    /// <returns><see langword="true"/> for nonnegative indices below <see cref="CapacityCeiling"/>.</returns>
    public static bool IsBodyIndex(int index) => ((uint)index < CapacityCeiling);
}
