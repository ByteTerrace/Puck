using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// An immutable, self-contained capture of a machine's entire mutable state at one instant. It owns its bytes and
/// aliases nothing in the live machine, so it can be held indefinitely, restored into the same machine to rewind, or
/// loaded into a fresh machine to fork a divergent run. The captured instant is carried alongside so a restore
/// repositions the clock exactly.
/// </summary>
public sealed class MachineSnapshot {
    private readonly byte[] m_data;

    internal MachineSnapshot(Tick takenAt, byte[] data) {
        TakenAt = takenAt;
        m_data = data;
    }

    /// <summary>Gets the instant on the master timeline at which this snapshot was taken.</summary>
    public Tick TakenAt { get; }
    /// <summary>Gets the size of the captured state, in bytes.</summary>
    public int Size =>
        m_data.Length;

    /// <summary>Indicates whether another snapshot captures byte-identical state. Two machines driven from the same
    /// start by any mix of pacing — single ticks or run budgets — produce equal snapshots; a difference is exactly a
    /// divergence.</summary>
    /// <param name="other">The snapshot to compare with.</param>
    /// <returns><see langword="true"/> when both snapshots hold identical bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool ContentEquals(MachineSnapshot other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        return m_data.AsSpan().SequenceEqual(other: other.m_data);
    }

    internal StateReader OpenReader() =>
        new(buffer: m_data);
}
