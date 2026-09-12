using Puck.GamingBricks;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Asset-free functional vectors for the bundled native BIOS. These are not retail timing-parity evidence.</summary>
internal sealed class BundledFirmwareStage : IPostStage<PostContext> {
    public string Name => "bundled-firmware";
    public PostTier Tier => PostTier.A;
    public bool IsConcurrent => true;

    public PostStageOutcome Run(PostContext context) {
        try {
            CheckIdentity();
            foreach (var thumb in new[] { false, true }) {
                CheckMath(thumb: thumb);
                CheckTransfers(thumb: thumb);
                CheckCompression(thumb: thumb);
                CheckUnpack(thumb: thumb);
                CheckWait(thumb: thumb);
                CheckAffine(thumb: thumb);
                CheckSound(thumb: thumb);
                CheckIrqWait(thumb: thumb);
            }
            return PostStageOutcome.Pass(detail: "bundled image identity/copy isolation and fast-mode firmware retention; ARM and Thumb vectors for division, square root, angles, checksum, CpuSet/FastSet, LZ/RL/differential/Huffman decode, bit unpack, affine transforms, sound initialization/bias/MIDI octave and pending/VBlank interrupt waits; real IRQ dispatch and return; functional smoke only, not retail timing or full sound/multiboot conformance");
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }

    private static void CheckIdentity() {
        var image = AgbFirmware.GetImage();
        var original = image[0];
        image[0] ^= 0xFF;
        FirmwareSwiProbe.Require(condition: AgbFirmware.GetImage()[0] == original, detail: "bundled image aliases caller storage");
        FirmwareSwiProbe.Require(condition: AgbBiosProfile.Identify(image: image).Kind == AgbBiosKind.Unknown, detail: "modified BIOS identified as bundled");
        var identified = AgbBiosProfile.Identify(image: AgbFirmware.GetImage());
        FirmwareSwiProbe.Require(condition: identified.Kind == AgbBiosKind.Puck && !identified.IsCycleParityTrustworthy, detail: "Puck image missing identity or falsely claiming retail parity");
        using var cold = new AdvancedGamingBrickCore(cartridgeRom: SyntheticRom.Create());
        using var fast = new AdvancedGamingBrickCore(cartridgeRom: SyntheticRom.Create(), bootMode: MachineBootMode.Fast);
        FirmwareSwiProbe.Require(condition: cold.Instance.Machine.Identity == fast.Instance.Machine.Identity, detail: "startup mode discarded firmware identity");
        FirmwareSwiProbe.Require(condition: cold.Instance.Machine.Cpu.GetRegister(index: 15) < ReplacementBios.ImageSize, detail: "cold core skipped its reset vector");
        FirmwareSwiProbe.Require(condition: fast.Instance.Machine.Cpu.GetRegister(index: 15) >= AdvancedGamingBrickMachine.CartridgeEntryPoint, detail: "fast core did not seed cartridge handoff");
        cold.Instance.Machine.Restore(snapshot: fast.Instance.Machine.Snapshot());
        cold.RunCycles(cycles: 4096);
        fast.RunCycles(cycles: 4096);
        FirmwareSwiProbe.Require(condition: cold.Instance.Machine.Snapshot().Data.SequenceEqual(other: fast.Instance.Machine.Snapshot().Data), detail: "same-image cross-mode snapshot replay diverged");
    }

    private static void CheckMath(bool thumb) {
        foreach (var vector in new (byte Number, uint R0, uint R1, uint Quotient, uint Remainder)[] {
            (6, 100, 7, 14, 2), (6, 0xFFFFFF9C, 7, 0xFFFFFFF2, 0xFFFFFFFE),
            (6, 100, 0xFFFFFFF9, 0xFFFFFFF2, 2), (7, 7, 100, 14, 2),
        }) {
            using var core = FirmwareSwiProbe.Run(number: vector.Number, thumb: thumb, r0: vector.R0, r1: vector.R1);
            var cpu = core.Instance.Machine.Cpu;
            FirmwareSwiProbe.Require(condition: cpu.GetRegister(index: 0) == vector.Quotient && cpu.GetRegister(index: 1) == vector.Remainder && cpu.GetRegister(index: 3) == 14, detail: $"division output mismatch, SWI {vector.Number}, Thumb={thumb}");
        }
        foreach (var (input, expected) in new (uint, uint)[] { (0, 0), (1, 1), (2, 1), (9, 3), (uint.MaxValue, 65535) }) {
            using var core = FirmwareSwiProbe.Run(number: 8, thumb: thumb, r0: input);
            FirmwareSwiProbe.Require(condition: core.Instance.Machine.Cpu.GetRegister(index: 0) == expected, detail: $"sqrt({input}) mismatch");
        }
        foreach (var (x, y, expected) in new (uint, uint, uint)[] { (1, 0, 0), (0, 1, 0x4000), (uint.MaxValue, 0, 0x8000), (0, uint.MaxValue, 0xC000), (1, 1, 0x2000) }) {
            using var core = FirmwareSwiProbe.Run(number: 10, thumb: thumb, r0: x, r1: y);
            FirmwareSwiProbe.Require(condition: core.Instance.Machine.Cpu.GetRegister(index: 0) == expected, detail: $"ArcTan2({x},{y}) mismatch");
        }
        using var checksum = FirmwareSwiProbe.Run(number: 13, thumb: thumb);
        FirmwareSwiProbe.Require(condition: checksum.Instance.Machine.Cpu.GetRegister(index: 0) == 0xBAAE187F, detail: "BIOS compatibility checksum mismatch");
    }

    private static void CheckTransfers(bool thumb) {
        foreach (var (number, control, length) in new (byte, uint, int)[] { (11, 4, 8), (11, 0x05000002, 8), (12, 1, 32) }) {
            using var core = FirmwareSwiProbe.Run(number: number, thumb: thumb, r0: FirmwareSwiProbe.Source, r1: FirmwareSwiProbe.Destination, r2: control, setup: static machine => {
                for (var index = 0; index < 36; ++index) {
                    machine.Bus.Write8(address: FirmwareSwiProbe.Source + (uint)index, value: (byte)(index + 1), access: BusAccessType.NonSequential);
                    machine.Bus.Write8(address: FirmwareSwiProbe.Destination + (uint)index, value: 0xCC, access: BusAccessType.NonSequential);
                }
            });
            var expected = new byte[length + 1];
            for (var index = 0; index < length; ++index) {
                expected[index] = (byte)((control == 0x05000002 ? index % 4 : index) + 1);
            }
            expected[^1] = 0xCC;
            FirmwareSwiProbe.ExpectBytes(bus: core.Instance.Machine.Bus, address: FirmwareSwiProbe.Destination, expected: expected, detail: $"transfer {number:X2}/{control:X8}");
        }
    }

    private static void CheckCompression(bool thumb) {
        foreach (var (number, source, expected, vram) in new (byte, byte[], byte[], bool)[] {
            (0x11, [0x10, 8, 0, 0, 0x20, 65, 66, 0x30, 1], [65, 66, 65, 66, 65, 66, 65, 66], false),
            (0x12, [0x10, 8, 0, 0, 0x20, 65, 66, 0x30, 1], [65, 66, 65, 66, 65, 66, 65, 66], true),
            (0x14, [0x30, 8, 0, 0, 0x85, 65], [65, 65, 65, 65, 65, 65, 65, 65], false),
            (0x15, [0x30, 8, 0, 0, 0x85, 65], [65, 65, 65, 65, 65, 65, 65, 65], true),
            (0x16, [0x80, 4, 0, 0, 1, 2, 255, 4], [1, 3, 2, 6], false),
            (0x17, [0x80, 4, 0, 0, 1, 2, 255, 4], [1, 3, 2, 6], true),
            (0x18, [0x82, 4, 0, 0, 0xFF, 0xFF, 2, 0], [0xFF, 0xFF, 1, 0], false),
            (0x13, [0x28, 4, 0, 0, 1, 0xC0, 65, 66, 0, 0, 0, 0x50], [65, 66, 65, 66], false),
        }) {
            var destination = vram ? 0x06000000u : FirmwareSwiProbe.Destination;
            using var core = FirmwareSwiProbe.Run(number: number, thumb: thumb, r0: FirmwareSwiProbe.Source, r1: destination,
                setup: machine => FirmwareSwiProbe.WriteBytes(bus: machine.Bus, address: FirmwareSwiProbe.Source, bytes: source));
            FirmwareSwiProbe.ExpectBytes(bus: core.Instance.Machine.Bus, address: destination, expected: expected, detail: $"decompression {number:X2}");
        }
    }

    private static void CheckUnpack(bool thumb) {
        using var core = FirmwareSwiProbe.Run(number: 0x10, thumb: thumb, r0: FirmwareSwiProbe.Source, r1: FirmwareSwiProbe.Destination, r2: FirmwareSwiProbe.Info, setup: static machine => {
            FirmwareSwiProbe.WriteBytes(bus: machine.Bus, address: FirmwareSwiProbe.Source, bytes: [0xE4]);
            FirmwareSwiProbe.WriteBytes(bus: machine.Bus, address: FirmwareSwiProbe.Info, bytes: [1, 0, 2, 8, 0x10, 0, 0, 0]);
        });
        FirmwareSwiProbe.ExpectBytes(bus: core.Instance.Machine.Bus, address: FirmwareSwiProbe.Destination, expected: [0, 17, 18, 19], detail: "BitUnpack 2-to-8 with nonzero-only offset");
    }

    private static void CheckWait(bool thumb) {
        using var core = FirmwareSwiProbe.Run(number: 4, thumb: thumb, r0: 0, r1: 1, setup: static machine =>
            machine.Bus.Write16(address: 0x03007FF8, value: 3, access: BusAccessType.NonSequential));
        FirmwareSwiProbe.Require(condition: core.Instance.Machine.Bus.Read16(address: 0x03007FF8, access: BusAccessType.NonSequential) == 2, detail: "IntrWait did not consume only the requested pending bit");
    }

    private static void CheckAffine(bool thumb) {
        using var background = FirmwareSwiProbe.Run(number: 0x0E, thumb: thumb, r0: FirmwareSwiProbe.Source, r1: FirmwareSwiProbe.Destination, r2: 1, setup: static machine =>
            FirmwareSwiProbe.WriteBytes(bus: machine.Bus, address: FirmwareSwiProbe.Source, bytes: [0, 10, 0, 0, 0, 20, 0, 0, 3, 0, 4, 0, 0, 1, 0, 1, 0, 0, 0, 0]));
        FirmwareSwiProbe.ExpectBytes(bus: background.Instance.Machine.Bus, address: FirmwareSwiProbe.Destination,
            expected: [0, 1, 0, 0, 0, 0, 0, 1, 0, 7, 0, 0, 0, 16, 0, 0], detail: "BgAffineSet identity scale and translated origin");
        using var objects = FirmwareSwiProbe.Run(number: 0x0F, thumb: thumb, r0: FirmwareSwiProbe.Source, r1: FirmwareSwiProbe.Destination, r2: 1, r3: 4, setup: static machine =>
            FirmwareSwiProbe.WriteBytes(bus: machine.Bus, address: FirmwareSwiProbe.Source, bytes: [0, 1, 0, 1, 0, 64, 0, 0]));
        FirmwareSwiProbe.ExpectBytes(bus: objects.Instance.Machine.Bus, address: FirmwareSwiProbe.Destination,
            expected: [0, 0, 0, 0, 0, 255, 0, 0, 0, 1, 0, 0, 0, 0], detail: "ObjAffineSet quarter turn with stride four");
    }

    private static void CheckSound(bool thumb) {
        using var initialized = FirmwareSwiProbe.Run(number: 0x1A, thumb: thumb, r0: FirmwareSwiProbe.Source);
        var bus = initialized.Instance.Machine.Bus;
        FirmwareSwiProbe.Require(condition: bus.Read32(address: 0x03007FF0, access: BusAccessType.NonSequential) == FirmwareSwiProbe.Source,
            detail: "SoundDriverInit did not publish its area pointer");
        FirmwareSwiProbe.Require(condition: bus.Read32(address: FirmwareSwiProbe.Source, access: BusAccessType.NonSequential) == 0x68736D53,
            detail: "SoundDriverInit did not publish its signature");
        foreach (var initial in new ushort[] { 0, 0x100, 0x200, 0x3FE, 0xC000, 0xC100, 0xC200, 0xC3FE }) {
            foreach (var enabled in new[] { false, true }) {
                using var bias = FirmwareSwiProbe.Run(number: 0x19, thumb: thumb, r0: enabled ? 1u : 0u, setup: machine =>
                    machine.Bus.Write16(address: 0x04000088, value: initial, access: BusAccessType.NonSequential));
                var actual = bias.Instance.Machine.Bus.Read16(address: 0x04000088, access: BusAccessType.NonSequential);
                // Independently checked by firmware-oracle: up means raise to at least 0x200, never lower a
                // caller-selected higher bias. Both directions retain the output-resolution bits.
                var expected = (initial & ~0x3FF) | (enabled ? Math.Max(val1: initial & 0x3FF, val2: 0x200) : 0);
                FirmwareSwiProbe.Require(condition: actual == expected,
                    detail: $"SoundBias direction/resolution mismatch: initial={initial:X4}, enabled={enabled}, actual={actual:X4}, expected={expected:X4}");
            }
        }
        foreach (var (key, expected) in new (uint, uint)[] { (60, 16384), (72, 32768) }) {
            using var midi = FirmwareSwiProbe.Run(number: 0x1F, thumb: thumb, r0: FirmwareSwiProbe.Source, r1: key, setup: static machine =>
                machine.Bus.Write32(address: FirmwareSwiProbe.Source + 4, value: 0x01000000, access: BusAccessType.NonSequential));
            FirmwareSwiProbe.Require(condition: midi.Instance.Machine.Cpu.GetRegister(index: 0) == expected, detail: "MidiKey2Freq octave vector mismatch");
        }
    }

    private static void CheckIrqWait(bool thumb) {
        using var core = FirmwareSwiProbe.Run(number: 5, thumb: thumb, setup: static machine => {
            // A native caller-owned IRQ callback acknowledges VBlank and ORs the BIOS wait flag, as required by
            // IntrWait. The BIOS must preserve the interrupted context and return through its real IRQ vector.
            uint[] handler = [0xE59F0018, 0xE3A01001, 0xE1C010B0, 0xE59F0010, 0xE1D010B0,
                0xE3811001, 0xE1C010B0, 0xE12FFF1E, 0x04000202, 0x03007FF8];
            for (var index = 0; index < handler.Length; ++index) {
                machine.Bus.Write32(address: 0x03007000 + (uint)(index * 4), value: handler[index], access: BusAccessType.NonSequential);
            }
            machine.Bus.Write32(address: 0x03007FFC, value: 0x03007000, access: BusAccessType.NonSequential);
            machine.Bus.Write16(address: 0x04000004, value: 8, access: BusAccessType.NonSequential);
            machine.Bus.Write16(address: 0x04000200, value: 1, access: BusAccessType.NonSequential);
            machine.Bus.Write16(address: 0x04000208, value: 1, access: BusAccessType.NonSequential);
        });
        FirmwareSwiProbe.Require(condition: core.CycleCount >= 160 * 1232, detail: "VBlankIntrWait returned before VBlank");
        FirmwareSwiProbe.Require(condition: (core.Instance.Machine.Cpu.Cpsr & 0x3F) == (thumb ? 0x3Fu : 0x1Fu), detail: "IRQ/SWI return lost caller execution mode");
        FirmwareSwiProbe.Require(condition: core.Instance.Machine.Bus.Read16(address: 0x03007FF8, access: BusAccessType.NonSequential) == 0,
            detail: "VBlankIntrWait did not consume its acknowledged BIOS flag");
    }
}
