using System.IO.Hashing;

namespace Puck.Assets;

// The PNG chunk CRC, shared by the encoder that writes it and the decoder that checks it: CRC-32 over the chunk's
// four type bytes followed by its data, never over the length field (PNG specification, section 5.3).
internal static class PngChunk {
    public static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data) {
        var crc = new Crc32();

        crc.Append(source: type);
        crc.Append(source: data);
        return crc.GetCurrentHashAsUInt32();
    }
}
