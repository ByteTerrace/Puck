using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Original song, instrument and sample inputs for native sequencer calls; never reads firmware instructions.</summary>
internal sealed class FirmwareSequenceFixture : IDisposable {
    internal const uint Player = 0x02008000;
    internal const uint Track = 0x02008100;
    internal const uint Program = 0x02009000;
    internal const uint Wave = 0x0200A000;
    internal const uint Area = 0x02001000;
    internal const uint Channel = Area + 0x50;
    private const uint Song = 0x02008800;
    private const uint Voice = 0x02008A00;

    internal FirmwareSequenceFixture(byte[]? bios, byte[] program) {
        Core = FirmwareSwiProbe.Run(number: 0x1A, thumb: true, r0: Area, bios: bios);
        try {
            FirmwareSwiProbe.Call(core: Core, number: 0x1B, thumb: true, r0: 0x0001F100);
            FirmwareSwiProbe.Call(core: Core, number: 0x20, thumb: true, r0: Player, r1: Track, r2: 1);
            Put(address: Song, bytes: [1, 0, 7, 0]);
            Word(address: Song + 4, value: Voice);
            Word(address: Song + 8, value: Program);
            // Fixed-rate direct sound, immediate attack, slow decay and a half-volume release per tick.
            Put(address: Voice, bytes: [8, 60, 0, 0, 0, 0xA0, 0, 2, 255, 255, 128, 128]);
            var wave = new byte[4096 + 16];
            BinaryPrimitives.WriteUInt16LittleEndian(destination: wave.AsSpan(start: 2), value: 0x4000);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: wave.AsSpan(start: 4), value: 5734 * 1024);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: wave.AsSpan(start: 12), value: 4096);
            wave.AsSpan(start: 16).Fill(value: 64);
            Put(address: Wave, bytes: wave);
            Put(address: Program, bytes: program);
            FirmwareSwiProbe.Call(core: Core, number: 0x21, thumb: true, r0: Player, r1: Song);
        } catch {
            Core.Dispose();
            throw;
        }
    }

    internal AdvancedGamingBrickCore Core { get; }
    internal byte Byte(uint address) => Core.Instance.Machine.Bus.Read8(address: address, access: BusAccessType.NonSequential);
    internal uint Word(uint address) => Core.Instance.Machine.Bus.Read32(address: address, access: BusAccessType.NonSequential);
    internal void Word(uint address, uint value) => Core.Instance.Machine.Bus.Write32(address: address, value: value, access: BusAccessType.NonSequential);
    internal void Put(uint address, ReadOnlySpan<byte> bytes) => FirmwareSwiProbe.WriteBytes(bus: Core.Instance.Machine.Bus, address: address, bytes: bytes);
    internal void Tick() => FirmwareSwiProbe.Call(core: Core, number: 0x1C, thumb: true);

    internal byte[] Pcm() {
        var result = new byte[192];
        for (var index = 0; index < 96; ++index) {
            result[index] = Byte(address: Area + 0x350 + (uint)index);
            result[index + 96] = Byte(address: Area + 0x980 + (uint)index);
        }
        return result;
    }

    internal uint[] Records() => [
        Byte(address: Track), Byte(address: Track + 1), Byte(address: Track + 2), Byte(address: Track + 3),
        Byte(address: Track + 4), Byte(address: Track + 5), Byte(address: Track + 6), Byte(address: Track + 0x12),
        Word(address: Track + 0x20), Word(address: Track + 0x40),
        Byte(address: Channel), Byte(address: Channel + 9), Byte(address: Channel + 0x10),
        Byte(address: Channel + 0x11), Word(address: Channel + 0x18), Word(address: Channel + 0x1C),
        Word(address: Channel + 0x28), Word(address: Channel + 0x2C),
    ];

    public void Dispose() => Core.Dispose();
}
