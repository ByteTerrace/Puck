namespace Puck.Machines;

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

    /// <summary>Gets the raw captured state bytes.</summary>
    public ReadOnlySpan<byte> Data => m_image.Data;
    /// <summary>Gets the machine identity.</summary>
    public TIdentity Identity { get; }
    /// <summary>Gets the component byte ranges covering the captured state.</summary>
    public IReadOnlyList<SnapshotSection> Sections => m_image.Sections;
    /// <summary>Gets the size of the captured state, in bytes.</summary>
    public int Size => m_image.Size;
    /// <summary>Gets the captured clock instant.</summary>
    public TClock TakenAt { get; }

    /// <summary>Creates the concrete snapshot over a byte image.</summary>
    /// <param name="identity">The machine identity.</param>
    /// <param name="takenAt">The captured clock instant.</param>
    /// <param name="image">The immutable state image.</param>
    /// <returns>The concrete snapshot.</returns>
    protected abstract TSnapshot Create(TIdentity identity, TClock takenAt, SnapshotImage image);

    /// <summary>Indicates whether another snapshot has the same identity, instant, and state bytes.</summary>
    /// <param name="other">The snapshot to compare with.</param>
    /// <returns><see langword="true"/> when every carried value is equal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public bool ContentEquals(TSnapshot other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        return (
            Identity.Equals(other: other.Identity) &&
            TakenAt.Equals(other: other.TakenAt) &&
            m_image.BytesEqual(other: other.m_image)
        );
    }
    /// <summary>Opens a forward-only reader over the captured state.</summary>
    /// <returns>The state reader.</returns>
    public StateReader OpenReader() => m_image.OpenReader();
    /// <summary>Replaces a machine's entire state with this snapshot's after checking that the snapshot was taken from
    /// the same machine and that the restore consumed exactly the image. A shortfall or overrun means a save/load
    /// field-order drift that would otherwise load as silently wrong state, so it faults deterministically and a byte
    /// difference stays a genuine divergence, never a misread.</summary>
    /// <typeparam name="TMachine">The machine type.</typeparam>
    /// <param name="machine">The machine to restore into.</param>
    /// <param name="expected">The identity the machine holds; the snapshot's must equal it.</param>
    /// <param name="identityFields">The fields the identity covers, named for the refusal sentence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The snapshot's identity does not match, or the restore consumed a
    /// different number of bytes than the image holds.</exception>
    public void RestoreInto<TMachine>(TMachine machine, TIdentity expected, string identityFields) where TMachine : ISnapshotableMachine {
        ArgumentNullException.ThrowIfNull(argument: machine);

        if (!Identity.Equals(other: expected)) {
            throw new InvalidOperationException(message: $"Snapshot identity ({identityFields}) does not match this machine; refusing to restore a mismatched image.");
        }

        var reader = m_image.OpenReader();

        machine.RestoreState(reader: reader);

        if (!reader.AtEnd) {
            throw new InvalidOperationException(message: "Snapshot restore consumed a different number of bytes than the snapshot holds; the save/load field order has drifted.");
        }
    }
    /// <summary>Returns a copy with one captured state byte overwritten.</summary>
    /// <param name="offset">The byte offset to overwrite.</param>
    /// <param name="value">The replacement byte.</param>
    /// <returns>A new snapshot carrying the modified byte image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside <see cref="Data"/>.</exception>
    public TSnapshot WithPokedByte(int offset, byte value) =>
        Create(
            identity: Identity,
            takenAt: TakenAt,
            image: m_image.WithPokedByte(
                offset: offset,
                value: value
            )
        );
}
