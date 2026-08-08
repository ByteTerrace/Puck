namespace Puck.Snapshots;

/// <summary>
/// Carries a typed machine identity and clock instant with an immutable snapshot byte image.
/// </summary>
/// <typeparam name="TSnapshot">The concrete snapshot type.</typeparam>
/// <typeparam name="TIdentity">The machine identity type.</typeparam>
/// <typeparam name="TClock">The captured clock type.</typeparam>
public abstract class MachineSnapshot<TSnapshot, TIdentity, TClock>
    where TSnapshot : MachineSnapshot<TSnapshot, TIdentity, TClock>
    where TIdentity : IEquatable<TIdentity>
    where TClock : IEquatable<TClock> {
    private readonly SnapshotImage m_image;

    /// <summary>Initializes a typed machine snapshot.</summary>
    /// <param name="identity">The machine identity.</param>
    /// <param name="takenAt">The captured clock instant.</param>
    /// <param name="image">The immutable state image.</param>
    protected MachineSnapshot(TIdentity identity, TClock takenAt, SnapshotImage image) {
        ArgumentNullException.ThrowIfNull(argument: image);

        Identity = identity;
        TakenAt = takenAt;
        m_image = image;
    }

    /// <summary>Gets the machine identity.</summary>
    public TIdentity Identity { get; }

    /// <summary>Gets the captured clock instant.</summary>
    public TClock TakenAt { get; }

    /// <summary>Gets the size of the captured state, in bytes.</summary>
    public int Size => m_image.Size;

    /// <summary>Gets the component byte ranges covering the captured state.</summary>
    public IReadOnlyList<SnapshotSection> Sections => m_image.Sections;

    /// <summary>Gets the raw captured state bytes.</summary>
    public ReadOnlySpan<byte> Data => m_image.Data;

    /// <summary>Indicates whether another snapshot has the same identity, instant, and state bytes.</summary>
    /// <param name="other">The snapshot to compare with.</param>
    /// <returns><see langword="true"/> when every carried value is equal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool ContentEquals(TSnapshot other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        return (Identity.Equals(other: other.Identity)
            && TakenAt.Equals(other: other.TakenAt)
            && m_image.BytesEqual(other: other.m_image));
    }

    /// <summary>Returns a copy with one captured state byte overwritten.</summary>
    /// <param name="offset">The byte offset to overwrite.</param>
    /// <param name="value">The replacement byte.</param>
    /// <returns>A new snapshot carrying the modified byte image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside <see cref="Data"/>.</exception>
    public TSnapshot WithPokedByte(int offset, byte value) =>
        Create(identity: Identity, takenAt: TakenAt, image: m_image.WithPokedByte(offset: offset, value: value));

    /// <summary>Opens a forward-only reader over the captured state.</summary>
    /// <returns>The state reader.</returns>
    public StateReader OpenReader() => m_image.OpenReader();

    /// <summary>Creates the concrete snapshot over a byte image.</summary>
    /// <param name="identity">The machine identity.</param>
    /// <param name="takenAt">The captured clock instant.</param>
    /// <param name="image">The immutable state image.</param>
    /// <returns>The concrete snapshot.</returns>
    protected abstract TSnapshot Create(TIdentity identity, TClock takenAt, SnapshotImage image);
}
