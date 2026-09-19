namespace Puck.State;

/// <summary>The ceilings a <see cref="StateArena"/> sizes itself by — the ones the authored document does not
/// declare because they describe the runtime's own lanes and bookkeeping rather than a row's contents.</summary>
/// <remarks>Every ceiling here refuses by name at the door that would cross it, never growing silently: a store
/// whose size is a function of the document and the input sequence alone is what lets two runs of the same world
/// lay out identically.</remarks>
public static class ArenaCapacity {
    /// <summary>The most bytes one arena may occupy, as <see cref="ArenaLayout.Bytes"/> measures it: every column
    /// at its full width, the change stamps a settle keeps per position, the row and key indexes, and the vector
    /// components. It is a document's memory bound, the one figure every row's capacity, every board and every lane
    /// is counted against, and a section that lays out past it is refused at the row that crossed. The text a text
    /// or provenance cell refers to is bounded per cell by its own length ceiling and is not counted here.</summary>
    public const long MaxBytes = (64L * 1024L * 1024L);
    /// <summary>The bytes of undo record an arena's open scopes may hold, entries and snapshotted vector components
    /// together, before a firing is refused and rewound. It is checked between effects, so the record may pass it by
    /// the writes of the one effect that crossed it. The positions a settle visits are a subset of the record's, so
    /// this bounds that buffer too.</summary>
    public const int MaxJournalBytes = (16 * 1024 * 1024);
    /// <summary>How many identity ordinals an arena's identity lane admits when its builder states no width: eight
    /// bytes a lane row per ordinal.</summary>
    public const int DefaultIdentities = 256;
    /// <summary>How many participant ordinals an arena's participant lane admits when its builder states no width:
    /// eight bytes a lane row per ordinal.</summary>
    public const int DefaultParticipants = 256;
    /// <summary>How many drawn masks one draw site can carry, one per generator context. A mask is four eight-byte
    /// words, and a site over a named source reserves all of them: 8 KiB a site.</summary>
    public const int MaxDrawnMasks = 256;
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
