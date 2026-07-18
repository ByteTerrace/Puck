namespace Puck.Scripting;

/// <summary>The C#-side, pre-marshal snapshot the host writes into the guest's snapshot region each tick.
/// Every fixed-point field is <see cref="Puck.Maths.FixedQ4816"/> raw <c>i64</c> bits; no floating point
/// crosses the boundary.</summary>
/// <param name="Tick">The engine tick (50400 Hz timebase), carried as a <see cref="ulong"/> bit pattern.</param>
/// <param name="PosLocalX">The local-space X position (strafe axis), <see cref="Puck.Maths.FixedQ4816"/> raw bits.</param>
/// <param name="PosLocalY">The local-space Y position (up axis), <see cref="Puck.Maths.FixedQ4816"/> raw bits.</param>
/// <param name="PosLocalZ">The local-space Z position (forward axis), <see cref="Puck.Maths.FixedQ4816"/> raw bits.</param>
/// <param name="Buttons">The digital-button bitfield of the addon's own slot (see <see cref="AddonButtons"/>).</param>
public readonly record struct AddonSnapshot(ulong Tick, long PosLocalX, long PosLocalY, long PosLocalZ, uint Buttons) {
    /// <summary>Serializes this snapshot as the 40 little-endian bytes of the ABI snapshot region.</summary>
    /// <param name="destination">The 40-byte destination span (the guest's snapshot region).</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="AddonAbi.SnapshotBytes"/>.</exception>
    public void WriteTo(Span<byte> destination) {
        AddonSnapshotWriter.Write(
            destination: destination,
            snapshot: in this
        );
    }
}
