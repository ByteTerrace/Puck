using System.Buffers.Binary;
using Puck.GamingBricks;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Calls a real BIOS vector from a small native cartridge; no host-side SWI interception.</summary>
internal static class FirmwareSwiProbe {
    internal const uint Source = 0x02000000;
    internal const uint Destination = 0x02001000;
    internal const uint Info = 0x02002000;
    private const int MaximumInstructions = 2_000_000;

    internal static AdvancedGamingBrickCore Run(byte number, bool thumb, uint r0 = 0, uint r1 = 0, uint r2 = 0, uint r3 = 0, Action<AdvancedGamingBrickMachine>? setup = null, byte[]? bios = null) {
        var core = new AdvancedGamingBrickCore(configuration: AgbFirmware.CreateConfiguration(
            cartridgeRom: CreateRom(), bootMode: MachineBootMode.Fast, bios: bios));
        try {
            Call(core: core, number: number, thumb: thumb, r0: r0, r1: r1, r2: r2, r3: r3, setup: setup);
            return core;
        } catch {
            core.Dispose();
            throw;
        }
    }

    /// <summary>Calls one immutable cartridge probe entry, retaining RAM and I/O while reseeding the caller CPU state.</summary>
    /// <param name="core">The native machine executing the selected BIOS.</param>
    /// <param name="number">The native SWI number, from zero through 0x2A.</param>
    /// <param name="thumb">Whether the cartridge invokes SWI in Thumb state.</param>
    /// <param name="r0">The first input register.</param>
    /// <param name="r1">The second input register.</param>
    /// <param name="r2">The third input register.</param>
    /// <param name="r3">The fourth input register.</param>
    /// <param name="setup">Optional caller-owned state setup before execution.</param>
    /// <param name="afterStep">Optional synchronous observer after each instruction step, including the cartridge trampoline.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is greater than 0x2A.</exception>
    /// <exception cref="InvalidOperationException">The call does not return within the fixed instruction budget.</exception>
    /// <remarks>Every probe entry is already in the cartridge; calls never modify cartridge bytes or copy BIOS instructions into diagnostics.</remarks>
    internal static void Call(AdvancedGamingBrickCore core, byte number, bool thumb, uint r0 = 0, uint r1 = 0, uint r2 = 0, uint r3 = 0, Action<AdvancedGamingBrickMachine>? setup = null, Action<AdvancedGamingBrickMachine>? afterStep = null) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: number, other: (byte)0x2A);
        var machine = core.Instance.Machine;
        var entry = AdvancedGamingBrickMachine.CartridgeEntryPoint + (uint)(number * 64) + (thumb ? 4096u : 0u);
        machine.Cpu.SetupDirectBoot(entryPoint: entry);
        machine.Cpu.SetRegister(index: 0, value: r0);
        machine.Cpu.SetRegister(index: 1, value: r1);
        machine.Cpu.SetRegister(index: 2, value: r2);
        machine.Cpu.SetRegister(index: 3, value: r3);
        machine.Cpu.SetRegister(index: 7, value: 0);
        setup?.Invoke(obj: machine);
        for (var instruction = 0; instruction < MaximumInstructions; ++instruction) {
            machine.Step();
            afterStep?.Invoke(obj: machine);
            var pc = machine.Cpu.GetRegister(index: 15);
            if ((machine.Cpu.GetRegister(index: 7) == 0x5A) && (pc >= entry) && (pc < entry + 64)) {
                return;
            }
        }
        throw new InvalidOperationException(message: $"BIOS SWI {number:X2} ({(thumb ? "Thumb" : "ARM")}) did not return; PC={machine.Cpu.GetRegister(index: 15):X8}");
    }

    private static byte[] CreateRom() {
        var rom = new byte[8192];
        for (var number = 0; number <= 0x2A; ++number) {
            var arm = number * 64;
            WriteWord(image: rom, offset: arm, value: 0xEF000000 | ((uint)number << 16));
            WriteWord(image: rom, offset: arm + 4, value: 0xE3A0705A);
            WriteWord(image: rom, offset: arm + 8, value: 0xEAFFFFFE);
            var thumb = arm + 4096;
            WriteWord(image: rom, offset: thumb, value: 0xE59FC000);
            WriteWord(image: rom, offset: thumb + 4, value: 0xE12FFF1C);
            WriteWord(image: rom, offset: thumb + 8, value: AdvancedGamingBrickMachine.CartridgeEntryPoint + (uint)thumb + 0x21);
            BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: thumb + 0x20), value: (ushort)(0xDF00 | number));
            BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: thumb + 0x22), value: 0x275A);
            BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: thumb + 0x24), value: 0xE7FE);
        }
        return rom;
    }

    internal static void WriteBytes(IAgbBus bus, uint address, ReadOnlySpan<byte> bytes) {
        for (var index = 0; index < bytes.Length; ++index) {
            bus.Write8(address: address + (uint)index, value: bytes[index], access: BusAccessType.NonSequential);
        }
    }

    internal static void ExpectBytes(IAgbBus bus, uint address, ReadOnlySpan<byte> expected, string detail) {
        for (var index = 0; index < expected.Length; ++index) {
            var actual = bus.Read8(address: address + (uint)index, access: BusAccessType.NonSequential);
            Require(condition: actual == expected[index], detail: $"{detail}: byte {index}, expected {expected[index]:X2}, got {actual:X2}");
        }
    }

    internal static void Require(bool condition, string detail) {
        if (!condition) {
            throw new InvalidOperationException(message: detail);
        }
    }

    private static void WriteWord(byte[] image, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: offset), value: value);
}
