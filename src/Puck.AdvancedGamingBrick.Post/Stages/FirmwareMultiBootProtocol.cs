using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Independent multiplayer wire expectations derived from the public GBATEK protocol, not native firmware tables.</summary>
internal static class FirmwareMultiBootProtocol {
    // https://www.akkit.org/info/gbatek.htm#biosmultibootsinglegamepak
    internal readonly record struct Exchange(ushort Sent, ushort Reply);

    // Byte-at-a-time polynomial division is deliberately different from the native word-at-a-time calculation.
    // These are fixed-width hardware bit streams, not an interchangeable hash or RNG abstraction.
    internal static ushort Checksum(ReadOnlySpan<byte> bytes, ushort initial, ushort polynomial) {
        var remainder = initial;

        foreach (var value in bytes) {
            for (var bit = 0; (bit < 8); ++bit) {
                var feedback = (remainder ^ (value >> bit)) & 1;

                remainder = ((ushort)(remainder >> 1));
                if (feedback != 0) { remainder ^= polynomial; }
            }
        }
        return remainder;
    }
    internal static uint Encrypt(uint plain, uint address, ref uint seed, bool normalMode = false) {
        seed = unchecked(((seed * 0x6F646573u) + 1));
        return plain ^ unchecked((~address + 1)) ^ seed ^ (normalMode
            ? 0x43202F2Fu
            : 0x6465646Fu
        );
    }
    internal static Exchange[] Expected(byte[] download, ushort[] negotiation) {
        CheckArithmeticVectors();
        var wire = new List<Exchange>();

        for (var index = 0; (index < 16); ++index) {
            wire.Add(item: new(
            Reply: ((index == 0)
            ? (ushort)0
            : (ushort)0x7202),
            Sent: 0x6200
        ));
        }
        wire.Add(item: new(
            Reply: 0x7202,
            Sent: 0x6102
        ));
        for (var index = 0; (index < 96); ++index) {
            var word = BinaryPrimitives.ReadUInt16LittleEndian(source: download.AsSpan(
                length: 2,
                start: (index * 2)
            ));

            wire.Add(item: new(
                Reply: ((ushort)(((96 - index) << 8) | 2)),
                Sent: word
            ));
        }
        wire.AddRange(collection: new Exchange[] { new(
            Reply: 2,
            Sent: 0x6200
        ), new(
            Reply: 0x7202,
            Sent: 0x6202
        ), new(
            Reply: 0x7202,
            Sent: 0x6381
        ), new(
            Reply: 0x7311,
            Sent: 0x6381
        ), new(
            Reply: 0x7311,
            Sent: 0x6420
        ) });
        Require(
            condition: (wire.Count == negotiation.Length),
            detail: "original negotiation fixture has the wrong number of transfers"
        );
        for (var index = 0; (index < negotiation.Length); ++index) {
            Require(
                condition: (wire[index].Sent == negotiation[index]),
                detail: "original negotiation fixture disagrees with the public protocol"
            );
        }
        var payload = download.AsSpan(start: 0xC0);

        wire.Add(item: new(
            Sent: ((ushort)((payload.Length / 4) - 4)),
            Reply: 0x73A1
        ));
        var seed = 0xFFFF1181u;
        var checksum = Checksum(
            bytes: payload,
            initial: 0xFFF8,
            polynomial: 0xA517
        );

        checksum = Checksum(
            bytes: [0x20, 0xA1, 0xFF, 0xFF],
            initial: checksum,
            polynomial: 0xA517
        );
        for (var offset = 0; (offset < payload.Length); offset += 4) {
            var plain = BinaryPrimitives.ReadUInt32LittleEndian(source: payload.Slice(
                length: 4,
                start: offset
            ));
            var encoded = Encrypt(
                plain: plain,
                address: (0x020000C0u + ((uint)offset)),
                seed: ref seed
            );

            wire.Add(item: new(
                Reply: ((ushort)(offset + 0xC0)),
                Sent: ((ushort)encoded)
            ));
            wire.Add(item: new(
                Reply: ((ushort)(offset + 0xC2)),
                Sent: ((ushort)(encoded >> 16))
            ));
        }
        wire.AddRange(collection: new Exchange[] { new(
            0x65,
            ((ushort)download.Length)
        ), new(
            Reply: 0x75,
            Sent: 0x65
        ), new(
            Reply: 0x75,
            Sent: 0x66
        ), new(
            Reply: checksum,
            Sent: checksum
        ) });
        return wire.ToArray();
    }

    private static void CheckArithmeticVectors() {
        Require(
            condition: (Checksum(
                bytes: [0, 0, 0, 0],
                initial: 0xFFF8,
                polynomial: 0xA517
            ) == 0x0749),
            detail: "public-protocol zero-word CRC vector changed"
        );
        Require(
            condition: (Checksum(
                bytes: [0x78, 0x56, 0x34, 0x12],
                initial: 0xFFF8,
                polynomial: 0xA517
            ) == 0x31B7),
            detail: "public-protocol multiplayer CRC vector changed"
        );
        Require(
            condition: (Checksum(
                bytes: [0x78, 0x56, 0x34, 0x12],
                initial: 0xC387,
                polynomial: 0xC37B
            ) == 0x4DD0),
            detail: "public-protocol normal-mode CRC vector changed"
        );
        var seed = 0xFFFF1181u;

        Require(
            condition: (Encrypt(
                plain: 0,
                address: 0x020000C0,
                seed: ref seed
            ) == 0xFF7A5ADB),
            detail: "public-protocol first encrypted zero-word vector changed"
        );
        Require(
            condition: (Encrypt(
                plain: 0,
                address: 0x020000C4,
                seed: ref seed
            ) == 0xEB56FFCE),
            detail: "public-protocol second encrypted zero-word vector changed"
        );
        seed = 0xFFFF1181u;
        Require(
            condition: (Encrypt(
                address: 0x020000C0,
                normalMode: true,
                plain: 0,
                seed: ref seed
            ) == 0xD83F119B),
            detail: "public-protocol normal-mode encrypted zero-word vector changed"
        );
    }
    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(
        condition: condition,
        detail: detail
    );
}
