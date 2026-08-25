using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick;

internal static class AgbArrayAccess {
    internal static uint Read(byte[] array, uint index, int width) {
        return width switch {
            1 => array[index],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(array.AsSpan((int)index)),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan((int)index)),
        };
    }
    internal static void Write(byte[] array, uint index, int width, uint value) {
        if (width == 1) {
            array[index] = ((byte)value);
        } else if (width == 2) {
            BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan((int)index), ((ushort)value));
        } else {
            BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan((int)index), value);
        }
    }
}
