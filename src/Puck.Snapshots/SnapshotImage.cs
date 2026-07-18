namespace Puck.Snapshots;

/// <summary>
/// The flat, self-contained byte image of a whole-machine snapshot: the serialized state bytes plus the section table
/// that maps a raw offset back to the component that owns it. It aliases nothing in the live machine, so it can be held
/// indefinitely, and every per-core snapshot type composes one of these to carry its bytes — the shared plumbing beneath
/// each core's typed identity and captured-instant. The image knows nothing of model/ROM identity or the master clock;
/// those stay per-core.
/// </summary>
public sealed class SnapshotImage {
    private readonly byte[] m_data;

    /// <summary>Creates an image over a snapshot's bytes and its section table.</summary>
    /// <param name="data">The flat serialized state bytes. The image takes ownership; callers must not mutate it after.</param>
    /// <param name="sections">The component byte-range table covering every byte of <paramref name="data"/>, in save order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> or <paramref name="sections"/> is <see langword="null"/>.</exception>
    public SnapshotImage(byte[] data, IReadOnlyList<SnapshotSection> sections) {
        ArgumentNullException.ThrowIfNull(argument: data);
        ArgumentNullException.ThrowIfNull(argument: sections);

        m_data = data;
        Sections = sections;
    }

    /// <summary>Gets the size of the captured state, in bytes.</summary>
    public int Size => m_data.Length;

    /// <summary>Gets the component byte-range table this image was written with (the divergence localizer's map from a
    /// raw offset back to the component that owns it). Covers every byte in <see cref="Data"/>, in save order.</summary>
    public IReadOnlyList<SnapshotSection> Sections { get; }

    /// <summary>Gets the raw, flat snapshot bytes — the same bytes a restore reads back. Exposed read-only for
    /// diagnostics (hashing, byte-level diffing); never mutated in place.</summary>
    public ReadOnlySpan<byte> Data => m_data;

    /// <summary>Indicates whether another image holds byte-identical state.</summary>
    /// <param name="other">The image to compare with.</param>
    /// <returns><see langword="true"/> when both images hold the same bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool BytesEqual(SnapshotImage other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        return m_data.AsSpan().SequenceEqual(other: other.m_data);
    }

    /// <summary>Returns a copy of this image with a single data byte overwritten — a cycle-cost-free way to inject a
    /// controlled corruption for testing a divergence-detection tool against itself. The section table is unchanged.</summary>
    /// <param name="offset">The absolute byte offset within <see cref="Data"/> to overwrite.</param>
    /// <param name="value">The replacement byte value.</param>
    /// <returns>A new image, identical except for the one byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside <see cref="Data"/>.</exception>
    public SnapshotImage WithPokedByte(int offset, byte value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: offset, other: m_data.Length);

        var poked = (byte[])m_data.Clone();

        poked[offset] = value;

        return new SnapshotImage(data: poked, sections: Sections);
    }

    /// <summary>Opens a forward-only reader over the image's bytes — the source a restore reads component state from.</summary>
    /// <returns>The reader.</returns>
    public StateReader OpenReader() => new(buffer: m_data);
}
