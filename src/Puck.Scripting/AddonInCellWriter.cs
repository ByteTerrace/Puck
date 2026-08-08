using System.Buffers.Binary;

namespace Puck.Scripting;

/// <summary>Serializes an <see cref="AddonInCell"/> into the 32-byte little-endian input cell layout, matching
/// the field offsets frozen in <see cref="AddonAbi.InCellOffsets"/>. Both reserved fields are always written as
/// explicit zeros.</summary>
public static class AddonInCellWriter {
    /// <summary>Writes <paramref name="cell"/> as 32 little-endian bytes into <paramref name="destination"/>.</summary>
    /// <param name="destination">The 32-byte destination span (one slot of the guest's input ring).</param>
    /// <param name="cell">The cell to serialize.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="AddonAbi.InCellBytes"/>.</exception>
    public static void Write(Span<byte> destination, in AddonInCell cell) {
        if (destination.Length < AddonAbi.InCellBytes) {
            throw new ArgumentException(
                message: $"The input cell destination must be at least {AddonAbi.InCellBytes} bytes.",
                paramName: nameof(destination)
            );
        }

        destination[AddonAbi.InCellOffsets.Kind] = (byte)cell.Kind;
        destination[AddonAbi.InCellOffsets.Channel] = cell.Channel;
        BinaryPrimitives.WriteUInt16LittleEndian(destination: destination[AddonAbi.InCellOffsets.Ordinal..], value: cell.Ordinal);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: destination[AddonAbi.InCellOffsets.HandleIndex..], value: cell.HandleIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: destination[AddonAbi.InCellOffsets.HandleGeneration..], value: cell.HandleGeneration);
        destination[AddonAbi.InCellOffsets.Verdict] = (byte)cell.Verdict;
        destination[AddonAbi.InCellOffsets.Verb] = cell.Verb;
        BinaryPrimitives.WriteUInt16LittleEndian(destination: destination[AddonAbi.InCellOffsets.Reserved0..], value: 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: destination[AddonAbi.InCellOffsets.Reserved1..], value: 0u);
        BinaryPrimitives.WriteInt64LittleEndian(destination: destination[AddonAbi.InCellOffsets.A..], value: cell.A);
        BinaryPrimitives.WriteInt64LittleEndian(destination: destination[AddonAbi.InCellOffsets.B..], value: cell.B);
    }
}
