using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Independent multiplayer wire expectations derived from the public GBATEK protocol, not native firmware tables.</summary>
internal static class FirmwareMultiBootProtocol {
    // https://www.akkit.org/info/gbatek.htm#biosmultibootsinglegamepak
    internal readonly record struct Exchange(ushort Sent, ushort Reply);

    internal static Exchange[] Expected(byte[] download, ushort[] negotiation) {
        CheckArithmeticVectors();
        var wire = new List<Exchange>();
        for (var index = 0; index < 16; ++index) { wire.Add(item: new(Sent: 0x6200, Reply: index == 0 ? (ushort)0 : (ushort)0x7202)); }
        wire.Add(item: new(Sent: 0x6102, Reply: 0x7202));
        for (var index = 0; index < 96; ++index) {
            var word = BinaryPrimitives.ReadUInt16LittleEndian(source: download.AsSpan(start: index * 2, length: 2));
            wire.Add(item: new(Sent: word, Reply: (ushort)(((96 - index) << 8) | 2)));
        }
        wire.AddRange(collection: new Exchange[] { new(0x6200, 2), new(0x6202, 0x7202), new(0x6381, 0x7202), new(0x6381, 0x7311), new(0x6420, 0x7311) });
        Require(condition: wire.Count == negotiation.Length, detail: "original negotiation fixture has the wrong number of transfers");
        for (var index = 0; index < negotiation.Length; ++index) {
            Require(condition: wire[index].Sent == negotiation[index], detail: "original negotiation fixture disagrees with the public protocol");
        }
        var payload = download.AsSpan(start: 0xC0);
        wire.Add(item: new(Sent: (ushort)(payload.Length / 4 - 4), Reply: 0x73A1));
        var seed = 0xFFFF1181u;
        var checksum = Checksum(bytes: payload, initial: 0xFFF8, polynomial: 0xA517);
        checksum = Checksum(bytes: [0x20, 0xA1, 0xFF, 0xFF], initial: checksum, polynomial: 0xA517);
        for (var offset = 0; offset < payload.Length; offset += 4) {
            var plain = BinaryPrimitives.ReadUInt32LittleEndian(source: payload.Slice(start: offset, length: 4));
            var encoded = Encrypt(plain: plain, address: 0x020000C0u + (uint)offset, seed: ref seed);
            wire.Add(item: new(Sent: (ushort)encoded, Reply: (ushort)(offset + 0xC0)));
            wire.Add(item: new(Sent: (ushort)(encoded >> 16), Reply: (ushort)(offset + 0xC2)));
        }
        wire.AddRange(collection: new Exchange[] { new(0x65, (ushort)download.Length), new(0x65, 0x75), new(0x66, 0x75), new(checksum, checksum) });
        return wire.ToArray();
    }

    // Byte-at-a-time polynomial division is deliberately different from the native word-at-a-time calculation.
    // These are fixed-width hardware bit streams, not an interchangeable hash or RNG abstraction.
    internal static ushort Checksum(ReadOnlySpan<byte> bytes, ushort initial, ushort polynomial) {
        var remainder = initial;
        foreach (var value in bytes) {
            for (var bit = 0; bit < 8; ++bit) {
                var feedback = (remainder ^ (value >> bit)) & 1;
                remainder = (ushort)(remainder >> 1);
                if (feedback != 0) { remainder ^= polynomial; }
            }
        }
        return remainder;
    }

    internal static uint Encrypt(uint plain, uint address, ref uint seed, bool normalMode = false) {
        seed = unchecked(seed * 0x6F646573u + 1);
        return plain ^ unchecked(~address + 1) ^ seed ^ (normalMode ? 0x43202F2Fu : 0x6465646Fu);
    }

    private static void CheckArithmeticVectors() {
        Require(condition: Checksum(bytes: [0, 0, 0, 0], initial: 0xFFF8, polynomial: 0xA517) == 0x0749, detail: "public-protocol zero-word CRC vector changed");
        Require(condition: Checksum(bytes: [0x78, 0x56, 0x34, 0x12], initial: 0xFFF8, polynomial: 0xA517) == 0x31B7, detail: "public-protocol multiplayer CRC vector changed");
        Require(condition: Checksum(bytes: [0x78, 0x56, 0x34, 0x12], initial: 0xC387, polynomial: 0xC37B) == 0x4DD0, detail: "public-protocol normal-mode CRC vector changed");
        var seed = 0xFFFF1181u;
        Require(condition: Encrypt(plain: 0, address: 0x020000C0, seed: ref seed) == 0xFF7A5ADB, detail: "public-protocol first encrypted zero-word vector changed");
        Require(condition: Encrypt(plain: 0, address: 0x020000C4, seed: ref seed) == 0xEB56FFCE, detail: "public-protocol second encrypted zero-word vector changed");
        seed = 0xFFFF1181u;
        Require(condition: Encrypt(plain: 0, address: 0x020000C0, seed: ref seed, normalMode: true) == 0xD83F119B, detail: "public-protocol normal-mode encrypted zero-word vector changed");
    }

    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(condition: condition, detail: detail);
}
