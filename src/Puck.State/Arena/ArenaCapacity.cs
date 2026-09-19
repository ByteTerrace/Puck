namespace Puck.State;

/// <summary>The ceilings a <see cref="StateArena"/> sizes itself by — the ones the authored document does not
/// declare because they describe the runtime's own lanes and bookkeeping rather than a row's contents.</summary>
/// <remarks>Every ceiling here refuses by name at the door that would cross it, never growing silently: a store
/// whose size is a function of the document and the input sequence alone is what lets two runs of the same world
/// lay out identically.</remarks>
public static class ArenaCapacity {
    /// <summary>How many identity ordinals an arena's identity lane admits.</summary>
    public const int DefaultIdentities = 64;
    /// <summary>How many participant ordinals an arena's participant lane admits.</summary>
    public const int DefaultParticipants = 64;
    /// <summary>How many drawn masks one draw site can carry — one per generator context.</summary>
    public const int MaxDrawnMasks = 64;
}
/// <summary>How wide an arena's participant and identity lanes are built.</summary>
/// <param name="Participants">How many participant ordinals the participant lane admits.</param>
/// <param name="Identities">How many identity ordinals the identity lane admits.</param>
public readonly record struct ArenaOptions(int Participants = ArenaCapacity.DefaultParticipants, int Identities = ArenaCapacity.DefaultIdentities) {
    /// <summary>Gets the lane widths an arena built without stated options uses.</summary>
    public static ArenaOptions Default => new(
        Identities: ArenaCapacity.DefaultIdentities,
        Participants: ArenaCapacity.DefaultParticipants
    );

    /// <summary>Returns how many ordinals <paramref name="lane"/> admits.</summary>
    /// <param name="lane">The lane to size.</param>
    /// <returns>The lane's ordinal ceiling; zero for the document lane, which has no ordinals.</returns>
    public int Capacity(StateLane lane) => (lane switch {
        StateLane.Participant => Participants,
        StateLane.Identity => Identities,
        _ => 0,
    });
}
