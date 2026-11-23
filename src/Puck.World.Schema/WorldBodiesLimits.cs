namespace Puck.World;

/// <summary>
/// Engine representation bounds for authored population tables.
/// </summary>
/// <remarks>A world's body storage is sized by its own authored <c>bodies.capacity</c> on the authority and grows on
/// demand to the highest body a delivery names on every client, mirror and presentation table; no structure is sized
/// by <see cref="IndexLimit"/>. A body count is a capacity that grows with the world, never a fixed slot count.</remarks>
public static class WorldBodiesLimits {
    /// <summary>The body-index format bound: every body index a world, a wire record, a checkpoint or a replay carries
    /// is below it, so <c>bodies.capacity</c> above it refuses at load by name (<c>WorldDefinitionValidator</c>), and a
    /// decoder reading untrusted bytes refuses a count or an index past it before it allocates. It is a format width,
    /// one world's population (a zone grows past one world through layers and shards), not an allocation: nothing is
    /// sized by it. 2^20 keeps any decoded body array within tens of mebibytes while leaving a single world room for
    /// hundreds of thousands of bodies.</summary>
    public const int IndexLimit = (1 << 20);
    /// <summary>The most distinct censuses a drawn <see cref="WorldBodiesDefaults.CapacityRow"/> source may span.
    /// Each one is settled into a candidate and put through the whole document validator, so this bound is what keeps
    /// proving every outcome a bounded admission cost; a wider source refuses by name rather than being admitted on
    /// the strength of the value one boot happened to roll.</summary>
    public const int MaxDrawnCensusOutcomes = 16;
    /// <summary>The capacity a population with a drawn <see cref="WorldBodiesDefaults.CapacityRow"/> census and no
    /// literal capacity validates at before the draw settles it: large enough that no permissive check refuses a body
    /// the settled census could hold. The settled census is validated again exactly (<c>ValidateCore</c>), and the
    /// authority allocates only the settled value.</summary>
    public const int UnsettledCapacity = 4096;
    /// <summary>The maximum local-seat count. Each document reserves only its authored
    /// <see cref="WorldBodiesDefaults.LocalSeats"/>; remaining slots may host inhabitants or peers.</summary>
    public const int LocalSeatCount = 4;

    /// <summary>Determines whether an address fits the engine's population representation, independently of
    /// a document's seat reservation or live occupant. A codec has no world census and cannot decide occupancy.</summary>
    /// <param name="index">The 0-based population entity index.</param>
    /// <returns><see langword="true"/> for nonnegative indices below <see cref="IndexLimit"/>.</returns>
    public static bool IsBodyIndex(int index) => (((uint)index) < IndexLimit);
}
