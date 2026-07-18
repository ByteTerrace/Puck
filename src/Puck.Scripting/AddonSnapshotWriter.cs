using System.Buffers.Binary;

namespace Puck.Scripting;

/// <summary>Serializes an <see cref="AddonSnapshot"/> into the 40-byte little-endian snapshot region, matching
/// the field offsets frozen in <see cref="AddonAbi.SnapshotOffsets"/>. The <c>reserved0</c> field is always
/// written as zero.</summary>
public static class AddonSnapshotWriter {
    /// <summary>Writes <paramref name="snapshot"/> as 40 little-endian bytes into <paramref name="destination"/>.</summary>
    /// <param name="destination">The 40-byte destination span (the guest's snapshot region).</param>
    /// <param name="snapshot">The snapshot to serialize.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="AddonAbi.SnapshotBytes"/>.</exception>
    public static void Write(Span<byte> destination, in AddonSnapshot snapshot) {
        if (destination.Length < AddonAbi.SnapshotBytes) {
            throw new ArgumentException(
                message: $"The snapshot destination must be at least {AddonAbi.SnapshotBytes} bytes.",
                paramName: nameof(destination)
            );
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination: destination[AddonAbi.SnapshotOffsets.Tick..], value: snapshot.Tick);
        BinaryPrimitives.WriteInt64LittleEndian(destination: destination[AddonAbi.SnapshotOffsets.PosLocalX..], value: snapshot.PosLocalX);
        BinaryPrimitives.WriteInt64LittleEndian(destination: destination[AddonAbi.SnapshotOffsets.PosLocalY..], value: snapshot.PosLocalY);
        BinaryPrimitives.WriteInt64LittleEndian(destination: destination[AddonAbi.SnapshotOffsets.PosLocalZ..], value: snapshot.PosLocalZ);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: destination[AddonAbi.SnapshotOffsets.Buttons..], value: snapshot.Buttons);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: destination[AddonAbi.SnapshotOffsets.Reserved0..], value: 0u);
    }
}
