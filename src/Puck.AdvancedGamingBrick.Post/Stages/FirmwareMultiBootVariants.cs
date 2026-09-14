using System.Buffers.Binary;
using Puck.GamingBricks;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Checks both normal clock rates and every multiplayer child slot against a public-protocol wire model.</summary>
internal static class FirmwareMultiBootVariants {
    private const int ChunkCycles = 64;

    private readonly record struct Exchange(uint Sent, uint First, ushort Second, ushort Third);

    internal static void Check(byte[] download) {
        var failures = new List<string>();

        foreach (var (mode, clients) in new[] { (0, 1), (2, 1), (1, 3) }) {
            try {
                CheckVariant(
                    clients: clients,
                    download: download,
                    mode: mode
                );
            } catch (InvalidOperationException exception) {
                failures.Add(item: $"mode {mode}, {clients} child(ren): {exception.Message}");
            }
        }
        Require(
            condition: (failures.Count == 0),
            detail: string.Join(
                separator: "; ",
                values: failures
            )
        );
    }

    private static void CheckVariant(byte[] download, int mode, int clients) {
        var negotiation = FirmwareMultiBootCartridge.Negotiation(
            clients: clients,
            download: download
        );
        var expected = Expected(
            clients: clients,
            download: download,
            mode: mode,
            negotiation: negotiation
        );
        using var sender = new AdvancedGamingBrickCore(
            cartridgeRom: FirmwareMultiBootCartridge.Sender(
                clients: clients,
                download: download,
                mode: mode,
                negotiation: negotiation
            ),
            bootMode: MachineBootMode.Fast
        );
        var receivers = new List<AdvancedGamingBrickCore>();

        try {
            for (var client = 0; (client < clients); ++client) {
                receivers.Add(item: FirmwareMultiBootStage.PrepareReceiver(
                    emptyCartridge: false,
                    transferMode: mode
                ));
            }
            using var session = new AgbLinkSession([sender.Instance, .. receivers.Select(selector: receiver => receiver.Instance)]);
            var serial = sender.Instance.GetRequiredService<IAgbSerialController>();
            var wasActive = serial.IsTransferActive;
            var sent = 0u;
            var completed = 0;
            var budget = ((60 * AdvancedGamingBrickMachine.CyclesPerFrame) / ChunkCycles);

            for (var chunk = 0; ((chunk < budget) && (completed < expected.Length)); ++chunk) {
                session.Run(cycles: ChunkCycles);
                var active = serial.IsTransferActive;

                if (
                    !wasActive &&
                    active
                ) {
                    sent = ((mode == 1)
                        ? serial.ReadRegister(offset: 0x12A)
                        : ReadNormal(serial: serial)
                    );
                }
                if (
                    wasActive &&
                    !active
                ) {
                    var actual = ((mode == 1)
                        ? new Exchange(
                            Sent: serial.ReadRegister(offset: 0x120),
                            First: serial.ReadRegister(offset: 0x122),
                            Second: serial.ReadRegister(offset: 0x124),
                            Third: serial.ReadRegister(offset: 0x126)
                        )
                        : new Exchange(
                            Sent: sent,
                            First: ReadNormal(serial: serial),
                            Second: 0xFFFF,
                            Third: 0xFFFF
                        )
                    );
                    var wanted = expected[completed];

                    Require(
                        condition: (actual == wanted),
                        detail: $"transfer {completed}: sent/replies {actual.Sent:X8}/{actual.First:X8}/{actual.Second:X4}/{actual.Third:X4}, expected {wanted.Sent:X8}/{wanted.First:X8}/{wanted.Second:X4}/{wanted.Third:X4}"
                    );
                    ++completed;
                }
                wasActive = active;
            }
            Require(
                condition: (completed == expected.Length),
                detail: $"stalled after {completed}/{expected.Length} expected exchanges within a 60-frame wire budget"
            );
            session.Run(cycles: (12L * AdvancedGamingBrickMachine.CyclesPerFrame));
            FirmwareMultiBootStage.CheckSender(sender: sender);
            for (var client = 0; (client < clients); ++client) {
                FirmwareMultiBootStage.CheckDownloadedProgram(
                    receiver: receivers[client],
                    download: download,
                    mode: mode,
                    client: (client + 1)
                );
            }
        } finally {
            foreach (var receiver in receivers) { receiver.Dispose(); }
        }
    }
    private static Exchange[] Expected(byte[] download, ushort[] negotiation, int mode, int clients) {
        // GBATEK specifies distinct initial CRCs, polynomials and cipher keys for normal and multiplayer
        // transfers. The model also checks normal's previous-master-word echo, not only the response tag.
        var wire = new List<Exchange>();
        var normal = (mode != 1);
        var previous = 0u;

        void Add(uint sent, ushort first, ushort second, ushort third) {
            wire.Add(item: new(
                First: (normal
                ? (((uint)first) << 16) | (previous & 0xFFFF)
                : first),
                Second: ((clients >= 2)
                ? second
                : (ushort)0xFFFF),
                Sent: sent,
                Third: ((clients >= 3)
                ? third
                : (ushort)0xFFFF)
            ));
            previous = sent;
        }
        void Address(uint sent, ushort address) => Add(
            first: address,
            second: address,
            sent: sent,
            third: address
        );
        for (var index = 0; (index < 16); ++index) {
            Add(
                first: ((index == 0)
                ? (ushort)0
                : (ushort)0x7202),
                second: ((index == 0)
                ? (ushort)0
                : (ushort)0x7204),
                sent: 0x6200,
                third: ((index == 0)
                ? (ushort)0
                : (ushort)0x7208)
            );
        }
        var mask = ((uint)((1 << (clients + 1)) - 2));

        Add(
            first: 0x7202,
            second: 0x7204,
            sent: 0x6100 | mask,
            third: 0x7208
        );
        for (var index = 0; (index < 96); ++index) {
            var word = BinaryPrimitives.ReadUInt16LittleEndian(source: download.AsSpan(
                length: 2,
                start: (index * 2)
            ));
            var countdown = ((96 - index) << 8);

            Add(
                first: ((ushort)(countdown | 2)),
                second: ((ushort)(countdown | 4)),
                sent: word,
                third: ((ushort)(countdown | 8))
            );
        }
        Add(
            first: 2,
            second: 4,
            sent: 0x6200,
            third: 8
        );
        Add(
            first: 0x7202,
            second: 0x7204,
            sent: 0x6200 | mask,
            third: 0x7208
        );
        Add(
            first: 0x7202,
            second: 0x7204,
            sent: 0x6381,
            third: 0x7208
        );
        Add(
            first: 0x7311,
            second: 0x7312,
            sent: 0x6381,
            third: 0x7313
        );
        var handshake = ((byte)(((0x11 + 0x11) + ((clients >= 2)
            ? 0x12
            : 0xFF)) + ((clients >= 3)
            ? 0x13
            : 0xFF)));

        Add(
            first: 0x7311,
            second: 0x7312,
            sent: ((uint)(0x6400 | handshake)),
            third: 0x7313
        );
        Require(
            condition: (wire.Count == negotiation.Length),
            detail: "negotiation fixture length disagrees with the independent protocol model"
        );
        for (var index = 0; (index < negotiation.Length); ++index) {
            Require(
                condition: (wire[index].Sent == negotiation[index]),
                detail: $"negotiation fixture word {index} disagrees with the independent protocol model"
            );
        }
        var payload = download.AsSpan(start: 0xC0);

        Add(
            sent: ((uint)((payload.Length / 4) - 4)),
            first: 0x73A1,
            second: 0x73A2,
            third: 0x73A3
        );
        var seed = 0x1181u | (((clients >= 2)
            ? 0x12u
            : 0xFFu) << 16) | (((clients >= 3)
            ? 0x13u
            : 0xFFu) << 24);
        var polynomial = (normal
            ? (ushort)0xC37B
            : (ushort)0xA517
        );
        var checksum = FirmwareMultiBootProtocol.Checksum(
            bytes: payload,
            initial: (normal
            ? (ushort)0xC387
            : (ushort)0xFFF8),
            polynomial: polynomial
        );

        checksum = FirmwareMultiBootProtocol.Checksum(
            bytes: [handshake, 0xA1, ((clients >= 2)
            ? (byte)0xA2
            : (byte)0xFF), ((clients >= 3)
            ? (byte)0xA3
            : (byte)0xFF)],
            initial: checksum,
            polynomial: polynomial
        );
        for (var offset = 0; (offset < payload.Length); offset += 4) {
            var plain = BinaryPrimitives.ReadUInt32LittleEndian(source: payload.Slice(
                length: 4,
                start: offset
            ));
            var encoded = FirmwareMultiBootProtocol.Encrypt(
                address: (0x020000C0u + ((uint)offset)),
                normalMode: normal,
                plain: plain,
                seed: ref seed
            );

            Address(
                address: ((ushort)(offset + 0xC0)),
                sent: (normal
                ? encoded
                : (ushort)encoded)
            );
            if (!normal) {
                Address(
                address: ((ushort)(offset + 0xC2)),
                sent: ((ushort)(encoded >> 16))
            );
            }
        }
        Address(
            sent: 0x65,
            address: ((ushort)download.Length)
        );
        Address(
            address: 0x75,
            sent: 0x65
        );
        Address(
            address: 0x75,
            sent: 0x66
        );
        Address(
            address: checksum,
            sent: checksum
        );
        return wire.ToArray();
    }
    private static uint ReadNormal(IAgbSerialController serial) => serial.ReadRegister(offset: 0x120) | (((uint)serial.ReadRegister(offset: 0x122)) << 16);
    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(
        condition: condition,
        detail: detail
    );
}
