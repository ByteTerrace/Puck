namespace Puck.World;

/// <summary>
/// Engine representation bounds for authored population tables.
/// </summary>
public static class WorldBodiesLimits {
    /// <summary>The largest authored population table the engine will allocate — single-sourced against
    /// <c>Puck.World.Client.WorldClient.EntityCapacity</c>, the client's own fixed per-entity view arrays
    /// (<c>Puck.World.Schema</c> cannot name that type, so the reference runs the other way: the client's constant
    /// reads this one). <c>population.capacity</c> is bounded to the client's real array size, so an over-capacity
    /// document refuses at load (<c>WorldDefinitionValidator</c>) rather than booting into a latent throw. Render
    /// detail is independently bounded: bodies beyond the detailed-avatar lane retain an individual coarse instance
    /// instead of multiplying this simulation limit by a full humanoid rig.</summary>
    public const int CapacityCeiling = 4096;
    /// <summary>The maximum local-seat count. Each document reserves only its authored
    /// <see cref="WorldBodiesDefaults.LocalSeats"/>; remaining slots may host inhabitants or peers.</summary>
    public const int LocalSeatCount = 4;
    /// <summary>The lowest-index band of bodies the client renders at full detail — a complete catalog rig, or the
    /// creation look the body wears. Bodies past this band render as one coarse instance each. This ONE number sizes
    /// every per-body presentation reservation that scales with detail: the avatar catalog's transform ranges
    /// (<c>Puck.World.Client.WorldRigCatalog.DetailedAvatarCapacity</c>) and the body-rooted creation-stamp pool
    /// (<see cref="WorldPlacementPolicy.MaxStampRegistrations"/>), so a creature in the detailed band can always be
    /// drawn as its creature — there is no separate, smaller ceiling on how many bodies may wear a creation.</summary>
    public const int DetailedRenderBand = 128;

    /// <summary>Determines whether an address fits the engine's population representation, independently of
    /// a document's seat reservation or live occupant. A codec has no world census and cannot decide occupancy.</summary>
    /// <param name="index">The 0-based population entity index.</param>
    /// <returns><see langword="true"/> for nonnegative indices below <see cref="CapacityCeiling"/>.</returns>
    public static bool IsBodyIndex(int index) => ((uint)index < CapacityCeiling);
}
