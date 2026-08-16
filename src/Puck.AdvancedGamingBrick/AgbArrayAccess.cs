namespace Puck.AdvancedGamingBrick;

internal static class AgbArrayAccess {
    internal static uint Read(byte[] array, uint index, int width) {
        return width switch {
            1 => array[index],
            2 => ((uint)(array[index] | (array[(index + 1u)] << 8))),
            _ => ((uint)(array[index]
                | (array[(index + 1u)] << 8)
                | (array[(index + 2u)] << 16)
                | (array[(index + 3u)] << 24))),
        };
    }
    internal static void Write(byte[] array, uint index, int width, uint value) {
        array[index] = ((byte)value);

        if (width >= 2) {
            array[(index + 1u)] = ((byte)(value >> 8));
        }

        if (width == 4) {
            array[(index + 2u)] = ((byte)(value >> 16));
            array[(index + 3u)] = ((byte)(value >> 24));
        }
    }
}
