using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Native legacy-player and PCM contracts, with optional retail execution as a black-box oracle.</summary>
internal sealed class FirmwareLegacySoundStage : IPostStage<PostContext> {
    private const uint Area = 0x02001000;
    private const uint Magic = 0x68736D53;
    private const uint Player = 0x02000000;
    private const uint Song = 0x02000600;
    private const uint Tracks = 0x02000100;

    public bool IsConcurrent => true;
    public string Name => "firmware-legacy-sound";
    public PostTier Tier => PostTier.A;

    private static void CheckCommands(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        var label = ((bios is null)
            ? "Puck"
            : "retail"
        );

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x20,
            thumb: true,
            r0: Player,
            r1: Tracks,
            r2: 1
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x2A,
            thumb: true,
            r0: Song
        );
        foreach (var (index, offset, flags, value) in new (uint, int, byte, byte)[] {
            (9, 0x1D, 0x80, 73), (11, 0x0A, 0x8C, 73), (13, 0x12, 0x83, 73),
            (14, 0x14, 0x83, 9), (15, 0x0E, 0x8C, 9), (16, 0x0F, 0x8C, 73),
            (17, 0x19, 0x80, 73), (18, 0x1B, 0x80, 73), (19, 0x17, 0x80, 73),
            (20, 0x18, 0x8F, 73), (23, 0x0C, 0x8C, 9),
        }) {
            FirmwareSwiProbe.WriteBytes(
                address: Tracks,
                bus: bus,
                bytes: new byte[80]
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: Tracks,
                value: 0x80
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0x40),
                value: (Song + 0x200)
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Song + 0x200),
                value: 73
            );
            var target = bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (index * 4))
            );

            FirmwareLegacySoundProbe.CallAddress(
                core: core,
                target: target,
                r0: Player,
                r1: Tracks
            );
            var expected = new byte[80];

            expected[0] = flags;
            expected[offset] = value;
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: expected.AsSpan(start: 0x40),
                value: (Song + 0x201)
            );
            FirmwareSwiProbe.ExpectBytes(
                address: Tracks,
                bus: bus,
                detail: $"{label} exported command {index}",
                expected: expected
            );
        }
        foreach (var index in new uint[] { 0, 5, 6, 7, 8, 21, 22, 24, 25, 26, 28 }) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: Tracks,
                value: 0x80
            );
            var target = bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (index * 4))
            );

            FirmwareLegacySoundProbe.CallAddress(
                core: core,
                target: target,
                r0: Player,
                r1: Tracks
            );
            FirmwareSwiProbe.Require(
                condition: (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: Tracks
                ) == 0),
                detail: $"{label} reserved/end command {index} must terminate track"
            );
        }
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Tracks + 0x40),
            value: (Song + 0x200)
        );
        bus.Write16(
            access: BusAccessType.NonSequential,
            address: (Player + 0x1E),
            value: 256
        );
        FirmwareLegacySoundProbe.CallAddress(
            core: core,
            target: bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (10 * 4))
            ),
            r0: Player,
            r1: Tracks
        );
        Word(
            address: (Player + 0x1C),
            bus: bus,
            detail: $"{label} tempo command",
            expected: 0x01000092
        );
        Word(
            address: (Player + 0x20),
            bus: bus,
            detail: $"{label} scaled tempo command",
            expected: 146
        );
        ReadOnlySpan<byte> instrument = [0, 60, 0, 0, 0x40, 0x09, 0, 2, 255, 128, 96, 64];

        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x300),
            bus: bus,
            bytes: instrument
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Player + 0x30),
            value: (Song + 0x300)
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Tracks + 0x40),
            value: (Song + 0x200)
        );
        bus.Write8(
            access: BusAccessType.NonSequential,
            address: (Song + 0x200),
            value: 0
        );
        FirmwareLegacySoundProbe.CallAddress(
            core: core,
            target: bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (12 * 4))
            ),
            r0: Player,
            r1: Tracks
        );
        FirmwareSwiProbe.ExpectBytes(
            address: (Tracks + 0x24),
            bus: bus,
            detail: $"{label} voice command copies instrument record",
            expected: instrument
        );
    }
    private static void CheckEdges(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        const uint Channel = (Area + 0x50), Wave = 0x02004000, Voices = 0x02008000;

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1B,
            thumb: true,
            r0: 0x0001F100
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x20,
            thumb: true,
            r0: Player,
            r1: Tracks,
            r2: 1
        );
        FirmwareSwiProbe.WriteBytes(
            address: Song,
            bus: bus,
            bytes: [1, 0, 7, 0]
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Song + 4),
            value: (Song + 0x100)
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Song + 8),
            value: (Song + 0x200)
        );
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x100),
            bus: bus,
            bytes: [128, 0, 0, 0, 0, 0x80, 0, 2, 0, 0, 0, 0]
        );
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x200),
            bus: bus,
            bytes: [0xBD, 0, 0xBE, 127, 0xBF, 64, 0xCF, 60, 127, 0xB0, 0xB1]
        );
        bus.Write16(
            access: BusAccessType.NonSequential,
            address: (Wave + 2),
            value: 0x4000
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Wave + 4),
            value: (5734 * 1024)
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Wave + 12),
            value: 4096
        );
        for (uint index = 0; (index < 4096); ++index) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: ((Wave + 16) + index),
                value: 64
            );
        }
        foreach (var (pan, gain, panX) in new (byte, uint, byte)[] { (0, 0x7D7E0812, 0), (0x80, 0xFB000812, 128), (0xA0, 0xBB3E0812, 192), (0xC0, 0x7D7E0812, 0), (0xFF, 0x00FA0812, 126) }) {
            FirmwareSwiProbe.WriteBytes(
                address: (Voices + (60 * 12)),
                bus: bus,
                bytes: [8, 72, 0, pan, 0, 0x40, 0, 2, 255, 255, 128, 128]
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x21,
                thumb: true,
                r0: Player,
                r1: Song
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1C,
                thumb: true
            );
            Word(
                address: Channel,
                bus: bus,
                detail: "drum-set stereo override",
                expected: gain
            );
            Word(
                address: (Channel + 0x20),
                bus: bus,
                detail: "drum-set forced octave",
                expected: 11468
            );
            FirmwareSwiProbe.Require(
                condition: ((bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Channel + 8)
                ) == 72) && (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Channel + 0x11)
                ) == 60)),
                detail: "drum pitch differs from tie-release MIDI key"
            );
            FirmwareSwiProbe.Require(
                condition: (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + 0x15)
                ) == panX),
                detail: "drum-set track pan extension"
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0x1E),
                value: 16
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0x1F),
                value: 4
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0x40),
                value: (Song + 0x300)
            );
            FirmwareSwiProbe.WriteBytes(
                address: (Song + 0x300),
                bus: bus,
                bytes: [60, 127, 0x81]
            );
            FirmwareLegacySoundProbe.CallAddress(
                core: core,
                target: bus.Read32(
                    access: BusAccessType.NonSequential,
                    address: (Area + 0x38)
                ),
                r0: 0,
                r1: Player,
                r2: Tracks
            );
            FirmwareSwiProbe.Require(
                condition: (bus.Read16(
                    access: BusAccessType.NonSequential,
                    address: (Channel + 0xC)
                ) == 0x0410),
                detail: "native note inherits pseudo-echo volume and duration"
            );
        }
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x22,
            thumb: true,
            r0: Player
        );
        foreach (var echo in new byte[] { 0, 32, 64, 128 }) {
            FirmwareSwiProbe.WriteBytes(
                address: Channel,
                bus: bus,
                bytes: new byte[64]
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: Channel,
                value: 0xFFFF0851
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Channel + 7),
                value: 128
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Channel + 9),
                value: 128
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Channel + 0xC),
                value: echo
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Channel + 0xD),
                value: 3
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 0x24),
                value: Wave
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 0x28),
                value: (Wave + 16)
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 0x18),
                value: 4096
            );
            for (var frame = 0; (frame < 10); ++frame) {
                FirmwareSwiProbe.Call(
                    core: core,
                    number: 0x1C,
                    thumb: true
                );
                var transition = ((echo == 0)
                    ? 7
                    : ((echo == 32)
                        ? 1
                        : 0
                ));
                var end = ((echo == 0)
                    ? 7
                    : (transition + 3)
                );
                var level = ((echo == 0)
                    ? (128 >> Math.Min(
                        val1: (frame + 1),
                        val2: 7
                    ))
                    : ((frame < transition)
                        ? 64
                        : echo
                ));
                var expectedStatus = ((frame >= end)
                    ? 0
                    : (((echo != 0) && (frame >= transition))
                        ? 0x55
                        : 0x51
                ));
                var remaining = (((echo == 0) || (frame <= transition))
                    ? 3
                    : Math.Max(
                        val1: 0,
                        val2: (end - frame)
                    )
                );

                FirmwareSwiProbe.Require(
                    condition: ((bus.Read8(
                        access: BusAccessType.NonSequential,
                        address: Channel
                    ) == expectedStatus) && (bus.Read8(
                        access: BusAccessType.NonSequential,
                        address: (Channel + 9)
                    ) == level) && (bus.Read8(
                        access: BusAccessType.NonSequential,
                        address: (Channel + 0xD)
                    ) == remaining)),
                    detail: $"pseudo-echo {echo} frame {frame} envelope/countdown"
                );
                var sample = ((frame >= end)
                    ? 0
                    : ((64 * ((level * 255) >> 8)) >> 8)
                );

                FirmwareSwiProbe.Require(
                    condition: (bus.Read8(
                        access: BusAccessType.NonSequential,
                        address: (Area + 0x350)
                    ) == sample),
                    detail: "pseudo-echo audible tail and stop"
                );
            }
        }
        foreach (var enabled in new byte[] { 0, 1 }) {
            foreach (var status in new uint[] { 0, 1, 0x80000000 }) {
                FirmwareSwiProbe.Call(
                    core: core,
                    number: 0x21,
                    thumb: true,
                    r0: Player,
                    r1: Song
                );
                bus.Write8(
                    access: BusAccessType.NonSequential,
                    address: (Player + 0xA),
                    value: enabled
                );
                bus.Write8(
                    access: BusAccessType.NonSequential,
                    address: (Player + 0xB),
                    value: enabled
                );
                bus.Write32(
                    access: BusAccessType.NonSequential,
                    address: (Player + 4),
                    value: status
                );
                bus.Write8(
                    access: BusAccessType.NonSequential,
                    address: (Player + 9),
                    value: 7
                );
                FirmwareSwiProbe.WriteBytes(
                    address: (Song + 0x40),
                    bus: bus,
                    bytes: [1, 0, 6, 0]
                );
                bus.Write32(
                    access: BusAccessType.NonSequential,
                    address: (Song + 0x48),
                    value: (Song + 0x200)
                );
                FirmwareSwiProbe.Call(
                    core: core,
                    number: 0x21,
                    thumb: true,
                    r0: Player,
                    r1: (Song + 0x40)
                );
                Word(
                    address: Player,
                    bus: bus,
                    detail: "native Start replaces lower-priority song for caller flag/status controls",
                    expected: (Song + 0x40)
                );
            }
        }
    }
    private static void CheckEnvelope(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        const uint Channel = (Area + 0x50), Wave = 0x02004000;

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1B,
            thumb: true,
            r0: 0x0001F100
        );
        bus.Write16(
            access: BusAccessType.NonSequential,
            address: (Wave + 2),
            value: 0x4000
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Wave + 12),
            value: 4096
        );
        for (uint index = 0; (index < 4096); ++index) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: ((Wave + 16) + index),
                value: 64
            );
        }
        foreach (var echo in new byte[] { 0, 32 }) {
            FirmwareSwiProbe.WriteBytes(
                address: Channel,
                bus: bus,
                bytes: new byte[64]
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: Channel,
                value: 0xFFFF0812
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Channel + 5),
                value: 128
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Channel + 9),
                value: 128
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Channel + 0xC),
                value: echo
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Channel + 0xD),
                value: 3
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 0x24),
                value: Wave
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 0x28),
                value: (Wave + 16)
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 0x18),
                value: 4096
            );
            for (var frame = 0; (frame < 12); ++frame) {
                FirmwareSwiProbe.Call(
                    core: core,
                    number: 0x1C,
                    thumb: true
                );
                var end = ((echo == 0)
                    ? 7
                    : 10
                );
                var expectedStatus = ((frame >= end)
                    ? 0
                    : ((frame < 7)
                        ? 0x12
                        : 0x16
                ));
                var envelope = ((frame < 7)
                    ? (128 >> (frame + 1))
                    : ((echo == 0)
                        ? 1
                        : 32
                ));
                var remaining = (((echo == 0) || (frame <= 7))
                    ? 3
                    : Math.Max(
                        val1: 0,
                        val2: (10 - frame)
                    )
                );

                FirmwareSwiProbe.Require(
                    condition: ((bus.Read8(
                        access: BusAccessType.NonSequential,
                        address: Channel
                    ) == expectedStatus) && (bus.Read8(
                        access: BusAccessType.NonSequential,
                        address: (Channel + 9)
                    ) == envelope) && (bus.Read8(
                        access: BusAccessType.NonSequential,
                        address: (Channel + 0xD)
                    ) == remaining)),
                    detail: "zero-sustain decay enters pseudo-echo or ends at silence"
                );
            }
        }
    }
    private static void CheckHelpers(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        var label = ((bios is null)
            ? "Puck"
            : "retail"
        );

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x2A,
            thumb: true,
            r0: Song
        );
        foreach (var (pan, right, left) in new (byte, byte, byte)[] { (0, 100, 99), (32, 150, 49), (64, 199, 0), (192, 0, 199), (224, 50, 149) }) {
            FirmwareSwiProbe.WriteBytes(
                address: Tracks,
                bus: bus,
                bytes: new byte[80]
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: Tracks,
                value: 0x8F
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0x12),
                value: 100
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0x13),
                value: 64
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0x14),
                value: pan
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0xA),
                value: 1
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0xC),
                value: 1
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + 0xF),
                value: 2
            );
            FirmwareLegacySoundProbe.CallAddress(
                core: core,
                target: bus.Read32(
                    access: BusAccessType.NonSequential,
                    address: (Song + (33 * 4))
                ),
                r0: Player,
                r1: Tracks
            );
            FirmwareSwiProbe.Require(
                condition: ((bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: Tracks
                ) == 0x8A) && (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + 8)
                ) == 1) && (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + 9)
                ) == 4)),
                detail: $"{label} volume/pitch helper dirty flags and fine pitch"
            );
            FirmwareSwiProbe.Require(
                condition: ((bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + 0x10)
                ) == right) && (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + 0x11)
                ) == left)),
                detail: $"{label} volume/pitch helper pan {pan}"
            );
        }
        FirmwareLegacySoundProbe.CallAddress(
            core: core,
            target: bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (30 * 4))
            ),
            r0: 0x20000
        );
        Word(
            address: (Area + 0x10),
            bus: bus,
            detail: $"{label} frequency helper sample count",
            expected: 132
        );
        Word(
            address: (Area + 0x14),
            bus: bus,
            detail: $"{label} frequency helper sample rate",
            expected: 7884
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Tracks + 0x40),
            value: (Song + 0x200)
        );
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x200),
            bus: bus,
            bytes: [0x21, 73]
        );
        FirmwareLegacySoundProbe.CallAddress(
            core: core,
            target: bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (27 * 4))
            ),
            r0: Player,
            r1: Tracks
        );
        FirmwareSwiProbe.Require(
            condition: (bus.Read8(
                access: BusAccessType.NonSequential,
                address: 0x04000081
            ) == 73),
            detail: $"{label} port command PSG routing"
        );
        Word(
            address: (Tracks + 0x40),
            bus: bus,
            detail: $"{label} port command advances two arguments",
            expected: (Song + 0x202)
        );
        const uint Channel = (Area + 0x50);
        const uint Next = (Channel + 64);

        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Tracks + 0x20),
            value: Channel
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Channel + 0x2C),
            value: Tracks
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Channel + 0x34),
            value: Next
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Next + 0x30),
            value: Channel
        );
        FirmwareLegacySoundProbe.CallAddress(
            core: core,
            target: bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (34 * 4))
            ),
            r0: Channel
        );
        Word(
            address: (Tracks + 0x20),
            bus: bus,
            detail: $"{label} channel unlink updates track head",
            expected: Next
        );
        Word(
            address: (Channel + 0x2C),
            bus: bus,
            detail: $"{label} channel unlink clears owner",
            expected: 0
        );
        Word(
            address: (Next + 0x30),
            bus: bus,
            detail: $"{label} channel unlink clears successor predecessor",
            expected: 0
        );
        var canary = new byte[65];

        canary.AsSpan().Fill(value: 0xA5);
        FirmwareSwiProbe.WriteBytes(
            address: Player,
            bus: bus,
            bytes: canary
        );
        FirmwareLegacySoundProbe.CallAddress(
            core: core,
            target: bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (35 * 4))
            ),
            r0: Player
        );
        canary.AsSpan(
            length: 64,
            start: 0
        ).Clear();
        FirmwareSwiProbe.ExpectBytes(
            address: Player,
            bus: bus,
            detail: $"{label} exported 64-byte initializer bounds",
            expected: canary
        );
    }
    private static void CheckInitialTick(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        var label = ((bios is null)
            ? "Puck"
            : "retail"
        );

        FirmwareSwiProbe.WriteBytes(
            address: Tracks,
            bus: bus,
            bytes: new byte[80]
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x20,
            thumb: true,
            r0: Player,
            r1: Tracks,
            r2: 1
        );
        FirmwareSwiProbe.WriteBytes(
            address: Song,
            bus: bus,
            bytes: [1, 0, 7, 0, 0, 0, 0, 0]
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Song + 8),
            value: (Song + 0x200)
        );
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x200),
            bus: bus,
            bytes: [0xB0, 0xB1]
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x21,
            thumb: true,
            r0: Player,
            r1: Song
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: true
        );
        var expected = new byte[80];

        expected[0] = 0x80;
        expected[1] = 95;
        expected[0xF] = 2;
        expected[0x13] = 64;
        expected[0x19] = 22;
        expected[0x24] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: expected.AsSpan(start: 0x40),
            value: (Song + 0x201)
        );
        FirmwareSwiProbe.ExpectBytes(
            address: Tracks,
            bus: bus,
            detail: $"{label} native first wait tick",
            expected: expected
        );
        Word(
            address: (Player + 4),
            bus: bus,
            detail: $"{label} native active-track mask",
            expected: 1
        );
        Word(
            address: (Player + 0xC),
            bus: bus,
            detail: $"{label} first tick preserves caller player clock",
            expected: 0
        );
    }
    private static void CheckManagement(bool thumb, byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: thumb,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        var label = $"{((bios is null)
            ? "Puck"
            : "retail")} {(thumb
            ? "Thumb"
            : "ARM")}";

        for (uint offset = 0; (offset < 64); ++offset) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Player + offset),
                value: 0xA5
            );
        }
        for (uint offset = 0; (offset < (4 * 80)); ++offset) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Tracks + offset),
                value: 0xA5
            );
        }
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x20,
            thumb: thumb,
            r0: Player,
            r1: Tracks,
            r2: 4
        );
        Word(
            address: (Player + 4),
            bus: bus,
            detail: $"{label} Open pause bit",
            expected: 0x80000000
        );
        Word(
            address: (Player + 8),
            bus: bus,
            detail: $"{label} Open track count",
            expected: 4
        );
        Word(
            address: (Player + 0x2C),
            bus: bus,
            detail: $"{label} Open tracks",
            expected: Tracks
        );
        Word(
            address: (Player + 0x34),
            bus: bus,
            detail: $"{label} Open identity",
            expected: Magic
        );
        Word(
            address: (Area + 0x24),
            bus: bus,
            detail: $"{label} Open registration",
            expected: Player
        );
        var callback = bus.Read32(
            access: BusAccessType.NonSequential,
            address: (Area + 0x20)
        );

        FirmwareSwiProbe.Require(
            condition: ((callback > 0) && (callback < 0x4000)),
            detail: $"{label} Open native callback missing"
        );
        for (uint index = 0; (index < 4); ++index) {
            FirmwareSwiProbe.Require(
                condition: ((bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + (index * 80))
                ) == 0) && (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: ((Tracks + (index * 80)) + 1)
                ) == 0xA5)),
                detail: $"{label} Open must clear track status only"
            );
        }
        // The caller supplies initialized track work areas before Start can unlink channels.
        FirmwareSwiProbe.WriteBytes(
            address: Tracks,
            bus: bus,
            bytes: new byte[(4 * 80)]
        );
        FirmwareSwiProbe.WriteBytes(
            address: Song,
            bus: bus,
            bytes: [2, 0, 7, 0]
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Song + 4),
            value: (Song + 0x100)
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Song + 8),
            value: (Song + 0x80)
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Song + 12),
            value: (Song + 0x90)
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x21,
            thumb: thumb,
            r0: Player,
            r1: Song
        );
        Word(
            address: Player,
            bus: bus,
            detail: $"{label} Start song",
            expected: Song
        );
        Word(
            address: (Player + 4),
            bus: bus,
            detail: $"{label} Start status",
            expected: 0
        );
        Word(
            address: (Player + 8),
            bus: bus,
            detail: $"{label} Start priority/count",
            expected: 0x704
        );
        Word(
            address: (Player + 0x1C),
            bus: bus,
            detail: $"{label} Start tempo/scaling",
            expected: 0x01000096
        );
        Word(
            address: (Player + 0x20),
            bus: bus,
            detail: $"{label} Start tempo accumulator",
            expected: 0x96
        );
        Word(
            address: (Player + 0x30),
            bus: bus,
            detail: $"{label} Start voicegroup",
            expected: (Song + 0x100)
        );
        for (uint index = 0; (index < 4); ++index) {
            FirmwareSwiProbe.Require(
                condition: (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + (index * 80))
                ) == ((index < 2)
                ? 0xC0
                : 0)),
                detail: $"{label} Start track status {index}"
            );
            if (index < 2) {
                Word(
                    address: ((Tracks + (index * 80)) + 0x40),
                    bus: bus,
                    detail: $"{label} Start sequence pointer {index}",
                    expected: ((Song + 0x80) + (index * 0x10))
                );
            }
        }
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x22,
            thumb: thumb,
            r0: Player
        );
        Word(
            address: (Player + 4),
            bus: bus,
            detail: $"{label} Stop status",
            expected: 0x80000000
        );
        FirmwareSwiProbe.Require(
            condition: (bus.Read8(
                access: BusAccessType.NonSequential,
                address: Tracks
            ) == 0xC0),
            detail: $"{label} Stop preserves track state"
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x23,
            thumb: thumb,
            r0: Player
        );
        Word(
            address: (Player + 4),
            bus: bus,
            detail: $"{label} Continue status",
            expected: 0
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x24,
            thumb: thumb,
            r0: Player,
            r1: 5
        );
        Word(
            address: (Player + 0x24),
            bus: bus,
            detail: $"{label} Fade interval/counter",
            expected: 0x00050005
        );
        Word(
            address: (Player + 0x28),
            bus: bus,
            detail: $"{label} Fade volume",
            expected: 0x100
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x2A,
            thumb: thumb,
            r0: Song
        );
        FirmwareSwiProbe.Require(
            condition: (core.Instance.Machine.Cpu.GetRegister(index: 0) == (Song + 144)),
            detail: $"{label} JumpList advances destination"
        );
        for (uint index = 0; (index < 36); ++index) {
            var target = bus.Read32(
                access: BusAccessType.NonSequential,
                address: (Song + (index * 4))
            );

            FirmwareSwiProbe.Require(
                condition: ((target > 0) && (target < 0x4000)),
                detail: $"{label} JumpList native pointer {index}"
            );
        }
    }
    private static void CheckMixer(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;

        foreach (var offset in new uint[] { 0x350, 0x980, (0x350 + 96), (0x980 + 96) }) {
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1A,
                thumb: true,
                r0: Area
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1B,
                thumb: true,
                r0: 0x0001F1C0
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Area + offset),
                value: 64
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1C,
                thumb: true
            );
            FirmwareSwiProbe.Require(
                condition: ((bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Area + 0x350)
                ) == 8) && (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Area + 0x980)
                ) == 8)),
                detail: $"reverb basis {offset:X4} must feed both output channels"
            );
        }
        const uint Wave = 0x02004000;

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1A,
            thumb: true,
            r0: Area
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1B,
            thumb: true,
            r0: 0x0001F200
        );
        bus.Write16(
            access: BusAccessType.NonSequential,
            address: (Wave + 2),
            value: 0x4000
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Wave + 12),
            value: 512
        );
        for (uint index = 0; (index < 512); ++index) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: ((Wave + 16) + index),
                value: 127
            );
        }
        for (uint index = 0; (index < 2); ++index) {
            var channel = ((Area + 0x50) + (index * 64));

            bus.Write32(
                access: BusAccessType.NonSequential,
                address: channel,
                value: 0xFFFF0880
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (channel + 4),
                value: 0xFF80FFFF
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (channel + 0x24),
                value: Wave
            );
        }
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: true
        );
        FirmwareSwiProbe.Require(
            condition: (bus.Read8(
                access: BusAccessType.NonSequential,
                address: (Area + 0x350)
            ) == 252),
            detail: "two-channel PCM overflow must wrap at eight bits"
        );
    }
    private static void CheckPcm(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        const uint Channel = (Area + 0x50);
        const uint Wave = 0x02004000;

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1B,
            thumb: true,
            r0: 0x0001F100
        );
        bus.Write16(
            access: BusAccessType.NonSequential,
            address: (Wave + 2),
            value: 0x4000
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Wave + 4),
            value: (5734 * 1024)
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Wave + 12),
            value: 4096
        );
        for (uint index = 0; (index < 4096); ++index) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: ((Wave + 16) + index),
                value: (((index % 8) < 4)
                ? (byte)64
                : (byte)192)
            );
        }
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: Channel,
            value: 0x80FF0880
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Channel + 4),
            value: 0xFF80FFFF
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Channel + 0x20),
            value: 5734
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Channel + 0x24),
            value: Wave
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: true
        );
        var label = ((bios is null)
            ? "Puck"
            : "retail"
        );

        for (uint index = 0; (index < 96); ++index) {
            var right = (((index % 8) < 4)
                ? (byte)63
                : (byte)192
            );
            var left = (((index % 8) < 4)
                ? (byte)31
                : (byte)224
            );

            FirmwareSwiProbe.Require(
                condition: ((bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: ((Area + 0x350) + index)
                ) == right) && (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: ((Area + 0x980) + index)
                ) == left)),
                detail: $"{label} fixed-rate stereo PCM sample {index}"
            );
        }
        Word(
            address: Channel,
            bus: bus,
            detail: $"{label} attack/decay and loop status",
            expected: 0x80FF0812
        );
        Word(
            address: (Channel + 8),
            bus: bus,
            detail: $"{label} envelope and stereo gain",
            expected: 0x7FFEFF00
        );
        Word(
            address: (Channel + 0x18),
            bus: bus,
            detail: $"{label} samples remaining",
            expected: 4000
        );
        Word(
            address: (Channel + 0x28),
            bus: bus,
            detail: $"{label} advanced waveform pointer",
            expected: (Wave + 112)
        );
        // A second update without VSync rewrites the same segment, as the DMA
        // phase, rather than the number of Main calls, selects the destination.
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: true
        );
        FirmwareSwiProbe.Require(
            condition: (bus.Read8(
                access: BusAccessType.NonSequential,
                address: ((Area + 0x350) + 96)
            ) == 0),
            detail: $"{label} Main must not advance DMA segment without VSync"
        );
    }
    private static void CheckPsg(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        const uint Psg = 0x02009000;
        ReadOnlySpan<uint> rates = [5734, 7884, 10512, 13379, 15768, 18157, 21024, 26758, 31536, 36314, 40137, 42048];
        ReadOnlySpan<uint> scales = [1463, 1064, 798, 627, 532, 462, 399, 313, 266, 231, 209, 200];

        for (uint frequency = 1; (frequency <= 12); ++frequency) {
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1B,
                thumb: true,
                r0: (frequency << 16)
            );
            Word(
                bus: bus,
                address: (Area + 0x14),
                expected: rates[(((int)frequency) - 1)],
                detail: $"sample mode {frequency} rate"
            );
            Word(
                bus: bus,
                address: (Area + 0x18),
                expected: scales[(((int)frequency) - 1)],
                detail: $"sample mode {frequency} interpolation scale"
            );
        }
        for (byte type = 1; (type <= 4); ++type) {
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1A,
                thumb: true,
                r0: Area
            );
            FirmwareSwiProbe.WriteBytes(
                address: Psg,
                bus: bus,
                bytes: new byte[256]
            );
            FirmwareSwiProbe.WriteBytes(
                address: 0x02007000,
                bus: bus,
                bytes: new byte[96]
            );
            for (uint index = 0; (index < 4); ++index) {
                bus.Write8(
                    access: BusAccessType.NonSequential,
                    address: ((Psg + (index * 64)) + 1),
                    value: ((byte)(index + 1))
                );
            }
            for (uint index = 0; (index < 3); ++index) {
                FirmwareLegacySoundProbe.InstallRecorder(
                    address: (0x03006000 + (index * 64)),
                    bus: bus,
                    destination: (0x02007000 + (index * 32))
                );
            }
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Area + 0x1C),
                value: Psg
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Area + 0x2C),
                value: 0x03006000
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Area + 0x30),
                value: 0x03006040
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Area + 0x28),
                value: 0x03006080
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x20,
                thumb: true,
                r0: Player,
                r1: Tracks,
                r2: 1
            );
            FirmwareSwiProbe.WriteBytes(
                address: Song,
                bus: bus,
                bytes: [1, 0, 7, 0]
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Song + 4),
                value: (Song + 0x100)
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Song + 8),
                value: (Song + 0x200)
            );
            FirmwareSwiProbe.WriteBytes(
                address: (Song + 0x100),
                bus: bus,
                bytes: [type, 60, 63, 0x12, 0, 0x40, 0, 2, 15, 7, 8, 7]
            );
            FirmwareSwiProbe.WriteBytes(
                address: (Song + 0x200),
                bus: bus,
                bytes: [0xBD, 0, 0xBE, 127, 0xBF, 64, 0xD3, 60, 127, 0xB0, 0xB1]
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x21,
                thumb: true,
                r0: Player,
                r1: Song
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1C,
                thumb: true
            );
            var selected = (Psg + ((uint)((type - 1) * 64)));
            var expected = new byte[64];

            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: expected,
                value: 0x7D7E0080u | ((uint)(type << 8))
            );
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: expected.AsSpan(start: 4),
                value: 0x0708070F
            );
            expected[8] = 60;
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: expected.AsSpan(start: 0x10),
                value: 0x077F3C04
            );
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: expected.AsSpan(start: 0x1C),
                value: 0x123F0000
            );
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: expected.AsSpan(start: 0x24),
                value: 0x02004000
            );
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: expected.AsSpan(start: 0x2C),
                value: Tracks
            );
            FirmwareSwiProbe.ExpectBytes(
                address: selected,
                bus: bus,
                detail: $"native PSG voice type {type} allocation",
                expected: expected
            );
            for (uint index = 0; (index < 3); ++index) {
                var record = (0x02007000 + (index * 32));

                Word(
                    address: (record + 16),
                    bus: bus,
                    detail: "PSG start callback count",
                    expected: ((index == 0)
                    ? 0u
                    : 1u)
                );
                if (index == 1) {
                    Word(
                        address: record,
                        bus: bus,
                        detail: "PSG frequency callback channel",
                        expected: type
                    );
                    Word(
                        address: (record + 4),
                        bus: bus,
                        detail: "PSG frequency callback key",
                        expected: 60
                    );
                    Word(
                        address: (record + 8),
                        bus: bus,
                        detail: "PSG frequency callback fine pitch",
                        expected: 0
                    );
                }
            }
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x22,
                thumb: true,
                r0: Player
            );
            Word(
                address: 0x02007010,
                bus: bus,
                detail: "PSG stop invokes oscillator-off callback",
                expected: 1
            );
            Word(
                address: 0x02007000,
                bus: bus,
                detail: "PSG stop identifies oscillator",
                expected: type
            );
            Word(
                address: (Tracks + 0x20),
                bus: bus,
                detail: "PSG stop unlinks track channels",
                expected: 0
            );
        }
    }
    private static void CheckResampler(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        const uint Wave = 0x02004000;
        const uint Channel = (Area + 0x50);

        foreach (var (frequency, phase, cursor, samplesHex) in new (uint, uint, uint, string)[] {
            (1000, 0x10A0, 16, "00000103040507080A0B0C0E0F111213"),
            (2867, 0x1666, 47, "0003070B0F13171B1F23272B2F33373B"),
            (4096, 0x234E, 67, "00040A10151B21272C32383D43494F54"),
            (5734, 0x1666, 95, "00070F171F272F373F474F575F676F77"),
            (8192, 0x3036, 135, "000A15212C38434F5A65712108131F2A"),
            (11468, 0x2CCC, 190, "000F1F2F3F4F5F6F000F1F2F3F4F5F6F"),
        }) {
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1A,
                thumb: true,
                r0: Area
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1B,
                thumb: true,
                r0: 0x0001F100
            );
            bus.Write16(
                access: BusAccessType.NonSequential,
                address: (Wave + 2),
                value: 0x4000
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Wave + 12),
                value: 512
            );
            for (uint index = 0; (index <= 512); ++index) {
                bus.Write8(
                    access: BusAccessType.NonSequential,
                    address: ((Wave + 16) + index),
                    value: ((byte)((index % 16) * 8))
                );
            }
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: Channel,
                value: 0xFFFF0080
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 4),
                value: 0xFF80FFFF
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 0x20),
                value: frequency
            );
            bus.Write32(
                access: BusAccessType.NonSequential,
                address: (Channel + 0x24),
                value: Wave
            );
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1C,
                thumb: true
            );
            FirmwareSwiProbe.ExpectBytes(
                bus: bus,
                address: (Area + 0x350),
                expected: Convert.FromHexString(s: samplesHex),
                detail: $"resampled waveform {frequency}"
            );
            Word(
                address: (Area + 0x18),
                bus: bus,
                detail: "resampler interpolation scale",
                expected: 1463
            );
            Word(
                address: (Channel + 0x1C),
                bus: bus,
                detail: $"resampler fractional phase {frequency}",
                expected: phase
            );
            Word(
                address: (Channel + 0x28),
                bus: bus,
                detail: $"resampler cursor {frequency}",
                expected: ((Wave + 16) + cursor)
            );
        }
    }
    private static void CheckSequence(byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: true,
            r0: Area,
            bios: bios
        );
        var bus = core.Instance.Machine.Bus;
        const uint Channel = (Area + 0x50);
        const uint Wave = 0x02004000;

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1B,
            thumb: true,
            r0: 0x0001F100
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x20,
            thumb: true,
            r0: Player,
            r1: Tracks,
            r2: 1
        );
        FirmwareSwiProbe.WriteBytes(
            address: Song,
            bus: bus,
            bytes: [1, 0, 7, 0]
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Song + 4),
            value: (Song + 0x100)
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Song + 8),
            value: (Song + 0x200)
        );
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x100),
            bus: bus,
            bytes: [8, 60, 0, 0, 0, 0x40, 0, 2, 255, 255, 128, 128]
        );
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x200),
            bus: bus,
            bytes: [0xBD, 0, 0xBE, 127, 0xBF, 64, 0xD3, 60, 127, 0x84, 0xCE, 60, 0xB1]
        );
        bus.Write16(
            access: BusAccessType.NonSequential,
            address: (Wave + 2),
            value: 0x4000
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Wave + 4),
            value: (5734 * 1024)
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Wave + 12),
            value: 4096
        );
        for (uint index = 0; (index < 4096); ++index) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: ((Wave + 16) + index),
                value: 64
            );
        }
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x21,
            thumb: true,
            r0: Player,
            r1: Song
        );
        for (var frame = 0; (frame < 6); ++frame) {
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1C,
                thumb: true
            );
            Word(
                address: Tracks,
                bus: bus,
                detail: "sequence wait and track termination",
                expected: ((frame < 4)
                ? (0x80u + ((uint)((3 - frame) << 8)))
                : 0)
            );
            Word(
                address: Channel,
                bus: bus,
                detail: "sequence gate and release status",
                expected: ((frame < 4)
                ? 0x7D7E0812u
                : 0x7D7E0852u)
            );
            Word(
                address: (Channel + 0x18),
                bus: bus,
                detail: "sequence consumes waveform samples",
                expected: (4096 - ((uint)((frame + 1) * 96)))
            );
            Word(
                address: (Channel + 0x28),
                bus: bus,
                detail: "sequence waveform cursor",
                expected: ((Wave + 16) + ((uint)((frame + 1) * 96)))
            );
            Word(
                address: (Channel + 0x2C),
                bus: bus,
                detail: "sequence detaches releasing voice",
                expected: ((frame < 4)
                ? Tracks
                : 0)
            );
            var expectedSample = ((frame < 4)
                ? 31
                : ((frame == 4)
                    ? 15
                    : 7
            ));

            FirmwareSwiProbe.Require(
                condition: (bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Area + 0x350)
                ) == expectedSample),
                detail: "sequence audible gate/release envelope"
            );
        }
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x200),
            bus: bus,
            bytes: [0xBD, 0, 0xBE, 127, 0xBF, 64, 0xCF, 60, 127, 0xB0, 0xB1]
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x21,
            thumb: true,
            r0: Player,
            r1: Song
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: true
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x24,
            thumb: true,
            r0: Player,
            r1: 2
        );
        for (var frame = 0; (frame < 32); ++frame) {
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1C,
                thumb: true
            );
            var level = (256 - (((frame + 1) / 2) * 16));
            var volume = ((127 * (level / 4)) >> 5);
            var voiceGain = (((((volume / 2) * 127) >> 7) * (254 - frame)) >> 8);
            var sample = ((64 * voiceGain) >> 8);

            FirmwareSwiProbe.Require(
                condition: (bus.Read16(
                    access: BusAccessType.NonSequential,
                    address: (Player + 0x28)
                ) == level),
                detail: "fade level progression reaches silence"
            );
            if (frame < 31) {
                FirmwareSwiProbe.Require(
                    condition: ((bus.Read16(
                        access: BusAccessType.NonSequential,
                        address: (Player + 0x26)
                    ) == (((frame % 2) == 0)
                    ? 1
                    : 2)) && (bus.Read8(
                        access: BusAccessType.NonSequential,
                        address: (Tracks + 0x13)
                    ) == (level / 4))),
                    detail: "fade interval scales the active track"
                );
            }
            var actualSample = bus.Read8(
                access: BusAccessType.NonSequential,
                address: (Area + 0x350)
            );

            FirmwareSwiProbe.Require(
                condition: (actualSample == sample),
                detail: $"{((bios is null)
                ? "Puck"
                : "retail")} fade frame {frame}: expected PCM {sample}, got {actualSample}"
            );
        }
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x200),
            bus: bus,
            bytes: [0xBD, 0, 0xBE, 127, 0xBF, 64, 0xC2, 8, 0xC4, 64, 0xCF, 60, 127, 0xB0, 0xB1]
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x21,
            thumb: true,
            r0: Player,
            r1: Song
        );
        for (var frame = 0; (frame < 32); ++frame) {
            FirmwareSwiProbe.Call(
                core: core,
                number: 0x1C,
                thumb: true
            );
            ReadOnlySpan<uint> frequencies = [5904, 6074, 6255, 6436, 6627, 6818];
            var phase = (((frame + 1) * 8) % 256);
            var modulation = ((phase < 64)
                ? phase
                : ((phase < 192)
                    ? (128 - phase)
                    : (phase - 256)
            ));

            FirmwareSwiProbe.Require(
                condition: ((bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + 0x1A)
                ) == phase) && (unchecked((sbyte)bus.Read8(
                    access: BusAccessType.NonSequential,
                    address: (Tracks + 0x16)
                )) == modulation)),
                detail: "triangle modulation phase and depth across all quadrants"
            );
            if (frame < frequencies.Length) {
                Word(
                    bus: bus,
                    address: (Channel + 0x20),
                    expected: frequencies[frame],
                    detail: "modulation retunes an already-playing voice"
                );
            }
        }
        // Install original recorder callbacks; only argument/result RAM is observed.
        for (uint index = 0; (index < 3); ++index) {
            FirmwareLegacySoundProbe.InstallRecorder(
                address: (0x03006000 + (index * 64)),
                bus: bus,
                destination: (0x02007000 + (index * 32))
            );
        }
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Area + 0x28),
            value: 0x03006000
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Area + 0x3C),
            value: 0x03006040
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Area + 0x38),
            value: 0x03006080
        );
        // No optional key/velocity bytes: the recorder replaces the note
        // consumer, so it intentionally must not leave argument bytes pending.
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x200),
            bus: bus,
            bytes: [0xFF, 0x81, 0xB1]
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x21,
            thumb: true,
            r0: Player,
            r1: Song
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: true
        );
        for (uint index = 0; (index < 3); ++index) {
            var record = (0x02007000 + (index * 32));

            Word(
                address: (record + 16),
                bus: bus,
                detail: "installed callback invocation count",
                expected: ((index == 1)
                ? 0u
                : 1u)
            );
            if (index == 0) {
                Word(
                    address: record,
                    bus: bus,
                    detail: "installed PSG main receives SoundArea",
                    expected: Area
                );
            } else if (index == 2) {
                Word(
                    address: record,
                    bus: bus,
                    detail: "installed note hook receives duration-table index",
                    expected: 48
                );
                Word(
                    address: (record + 4),
                    bus: bus,
                    detail: "installed note hook receives player",
                    expected: Player
                );
                Word(
                    address: (record + 8),
                    bus: bus,
                    detail: "installed note hook receives track",
                    expected: Tracks
                );
            }
        }
        FirmwareLegacySoundProbe.InstallRecorder(
            address: 0x030060C0,
            bus: bus,
            destination: 0x02007060
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x2A,
            thumb: true,
            r0: 0x02008000
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (0x02008000 + (9 * 4)),
            value: 0x030060C0
        );
        bus.Write32(
            access: BusAccessType.NonSequential,
            address: (Area + 0x34),
            value: 0x02008000
        );
        FirmwareSwiProbe.WriteBytes(
            address: (Song + 0x200),
            bus: bus,
            bytes: [0xBA, 0x81, 0xB1]
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x21,
            thumb: true,
            r0: Player,
            r1: Song
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: true
        );
        Word(
            address: 0x02007070,
            bus: bus,
            detail: "installed command table overrides native commands",
            expected: 1
        );
        Word(
            address: 0x02007060,
            bus: bus,
            detail: "installed command receives player",
            expected: Player
        );
        Word(
            address: 0x02007064,
            bus: bus,
            detail: "installed command receives track",
            expected: Tracks
        );
    }
    private static void Word(IAgbBus bus, uint address, uint expected, string detail) {
        var actual = bus.Read32(
            access: BusAccessType.NonSequential,
            address: address
        );

        FirmwareSwiProbe.Require(
            condition: (actual == expected),
            detail: $"{detail}: expected {expected:X8}, got {actual:X8}"
        );
    }

    public PostStageOutcome Run(PostContext context) {
        try {
            if (AgbBiosProfile.Identify(image: context.BiosImage.Span).IsCycleParityTrustworthy) {
                CheckEdges(bios: context.BiosImage.ToArray());
                CheckEnvelope(bios: context.BiosImage.ToArray());
                CheckInitialTick(bios: context.BiosImage.ToArray());
            }
            CheckEdges(bios: null);
            CheckEnvelope(bios: null);
            CheckCommands(bios: null);
            CheckHelpers(bios: null);
            CheckInitialTick(bios: null);
            CheckPcm(bios: null);
            CheckSequence(bios: null);
            CheckMixer(bios: null);
            CheckResampler(bios: null);
            CheckPsg(bios: null);
            if (AgbBiosProfile.Identify(image: context.BiosImage.Span).IsCycleParityTrustworthy) {
                CheckHelpers(bios: context.BiosImage.ToArray());
                CheckCommands(bios: context.BiosImage.ToArray());
                CheckPcm(bios: context.BiosImage.ToArray());
                CheckSequence(bios: context.BiosImage.ToArray());
                CheckMixer(bios: context.BiosImage.ToArray());
                CheckResampler(bios: context.BiosImage.ToArray());
                CheckPsg(bios: context.BiosImage.ToArray());
            }
            foreach (var thumb in new[] { false, true }) {
                CheckManagement(
                    bios: null,
                    thumb: thumb
                );
                if (AgbBiosProfile.Identify(image: context.BiosImage.Span).IsCycleParityTrustworthy) {
                    CheckManagement(
                        thumb: thumb,
                        bios: context.BiosImage.ToArray()
                    );
                }
            }
            return PostStageOutcome.Pass(detail: "native player management and exported callbacks; exact stereo PCM, resampling, reverb and wraparound; gate/release, fade-to-silence, modulation quadrants, drum pitch/pan and pseudo-echo; all sample-rate modes and four installed PSG oscillator hooks; optional verified retail controls; bounded ABI/RAM/sample evidence, not exhaustive PCM or timing parity");
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }
}
