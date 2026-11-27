namespace Puck.State;

/// <summary>What the arena's shared ceilings answered while one reader watched: the least room any check left under
/// <see cref="ArenaCapacity.MaxBytes"/> and <see cref="StateCapacity.MaxCellKeys"/>, and whether any check refused.</summary>
/// <remarks>
/// <para>The arena's byte total and its retained key ledger belong to no row: every row's mints, reservations and cell
/// visibilities draw on them. An operation answers by them only when it reaches one of those checks (a mint of a name
/// the ledger does not hold, a row growing its reservation, a cell visibility stored), and every such check reports
/// here, the byte and name ceilings through one door (<c>StateArena.FitsCeilings</c>) and a refusal no count describes
/// (a vector or journal ceiling, the host's growth admission) through <c>StateArena.RefuseHeadroom</c>.</para>
/// <para>So work during which nothing reported cannot have depended on either total, and work whose checks all admitted
/// would admit again while the arena holds no more than <see cref="ByteRoom"/> more bytes and <see cref="KeyRoom"/> more
/// names over the same storage layout. A search job reads it to restart only when its answer could have changed.</para>
/// <para>One reader owns a watch and registers it through <see cref="StateArena.WatchHeadroom"/> for the span it
/// measures.</para>
/// </remarks>
public sealed class ArenaHeadroomWatch {
    /// <summary>Gets whether any check reported since the last <see cref="Clear"/>.</summary>
    public bool Consulted { get; private set; }
    /// <summary>Gets whether any check refused since the last <see cref="Clear"/>.</summary>
    public bool Refused { get; private set; }
    /// <summary>Gets the least room under <see cref="ArenaCapacity.MaxBytes"/> any check left, negative when one
    /// refused; <see cref="ArenaCapacity.MaxBytes"/> when none reported a byte total.</summary>
    public long ByteRoom { get; private set; } = ArenaCapacity.MaxBytes;
    /// <summary>Gets the least room under <see cref="StateCapacity.MaxCellKeys"/> any check left, negative when one
    /// refused; <see cref="StateCapacity.MaxCellKeys"/> when none reported a name count.</summary>
    public long KeyRoom { get; private set; } = StateCapacity.MaxCellKeys;

    /// <summary>Starts a new span: nothing consulted, and the whole of both ceilings as room.</summary>
    public void Clear() {
        Consulted = false;
        Refused = false;
        ByteRoom = ArenaCapacity.MaxBytes;
        KeyRoom = StateCapacity.MaxCellKeys;
    }

    internal void Note(long byteRoom, long keyRoom) {
        Consulted = true;
        Refused |= ((byteRoom < 0L) || (keyRoom < 0L));
        ByteRoom = Math.Min(val1: ByteRoom, val2: byteRoom);
        KeyRoom = Math.Min(val1: KeyRoom, val2: keyRoom);
    }
    internal void Refuse() {
        Consulted = true;
        Refused = true;
    }
}
public sealed partial class StateArena {
    private ArenaHeadroomWatch? m_headroom;

    /// <summary>Registers the watch every check against the arena's shared ceilings reports to, replacing any other;
    /// <see langword="null"/> registers none.</summary>
    /// <param name="watch">The reader's watch, or <see langword="null"/>.</param>
    public void WatchHeadroom(ArenaHeadroomWatch? watch) => m_headroom = watch;

    // The one door every check against the arena's shared ceilings passes: whether a proposed byte total and name count
    // fit, reported to the registered watch. A new check that compares against MaxBytes or MaxCellKeys without passing
    // here leaves a search job's walk standing across a change that could alter it.
    private bool FitsCeilings(long bytes, long keys) {
        var byteRoom = (ArenaCapacity.MaxBytes - bytes);
        var keyRoom = (StateCapacity.MaxCellKeys - keys);

        m_headroom?.Note(byteRoom: byteRoom, keyRoom: keyRoom);

        return ((byteRoom >= 0L) && (keyRoom >= 0L));
    }
    // A refusal by a ceiling that reads the arena's storage as a whole but is no byte or name count.
    private void RefuseHeadroom() => m_headroom?.Refuse();
    // The key table's admission of one more name: its retained bytes and name count against the arena's ceilings, the
    // rest of the arena's bytes being what the key byte budget leaves out.
    private bool AdmitKeys(long keyBytes, int keyCount) => FitsCeilings(
        bytes: ((ArenaCapacity.MaxBytes - KeyByteBudget()) + keyBytes),
        keys: keyCount
    );
}
