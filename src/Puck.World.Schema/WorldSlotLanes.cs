namespace Puck.World;

/// <summary>How wide a world's arena slot lanes are built.</summary>
/// <remarks>Both lanes are per-body — a `state.body` declaration rides the participant lane and a `state.identity`
/// one the identity lane, each at the body's own entity index — so both are sized by the population's capacity and
/// every host that builds an arena over a world document sizes them here. Two hosts that size them differently
/// cannot compare arena hashes, because the lane widths are part of what <c>StateArena.ComputeHash</c> folds.
/// </remarks>
public static class WorldSlotLanes {
    /// <summary>Returns the lane widths an arena over <paramref name="definition"/> is built with.</summary>
    /// <param name="definition">The world document.</param>
    /// <returns>The lane widths.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static ArenaOptions Options(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        // A document authoring no population still admits one ordinal. The floor is this host's: the arena itself
        // lays out a zero-wide lane, which admits none.
        var ordinals = Math.Max(
            val1: 1,
            val2: definition.Population.Capacity
        );

        return new ArenaOptions(
            Identities: ordinals,
            Participants: ordinals
        );
    }
}
