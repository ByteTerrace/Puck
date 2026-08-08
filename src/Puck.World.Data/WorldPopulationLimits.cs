namespace Puck.World;

/// <summary>
/// Engine representation bounds for authored population tables.
/// </summary>
public static class WorldPopulationLimits {
    /// <summary>The largest authored population table the engine will allocate — single-sourced against
    /// <c>Puck.World.Client.WorldClient.EntityCapacity</c>, the client's own fixed per-entity view arrays
    /// (<c>Puck.World.Data</c> cannot name that type, so the reference runs the other way: the client's constant
    /// reads THIS one). Before the F3 reconciliation (2026-08-06) this stood at 4096 while the client's arrays were
    /// fixed at 128 — a document authoring 129..4096 validated and booted, and any client path indexing a body past
    /// 128 (a placement's Attach facet, a HUD binding, a render anchor) threw. The document's own vocabulary, not a
    /// client-side patch, is where an over-capacity document belongs refused: population.capacity is now bounded to
    /// the client's real bound, so an out-of-range document REFUSES AT LOAD (WorldDefinitionValidator) instead of
    /// booting into a latent throw. 128 already covers the shipped worlds' authored capacity and the owner's ~40-player
    /// scale intent with headroom to spare; raising it again means growing the client's arrays FIRST, never widening
    /// this ceiling alone.</summary>
    public const int CapacityCeiling = 128;

    /// <summary>The reserved local-seat count — indices <c>0..LocalSeatCount-1</c> are the up-to-four local players;
    /// later indices host simulated stand-ins and network peers.</summary>
    public const int LocalSeatCount = 4;

    /// <summary>Determines whether <paramref name="index"/> is in the population slice reserved for peer entities.</summary>
    /// <param name="index">The 0-based population entity index.</param>
    /// <returns><see langword="true"/> for indices <see cref="LocalSeatCount"/> through the engine ceiling.</returns>
    public static bool IsPeerIndex(int index) => ((uint)(index - LocalSeatCount) < (CapacityCeiling - LocalSeatCount));
}
