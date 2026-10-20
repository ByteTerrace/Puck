using System.Buffers.Binary;
using Puck.Machines;

namespace Puck.AdvancedGamingBrick.Tests;

/// <summary>
/// The bundled firmware's CpuSet (SWI 0x0B) and CpuFastSet (SWI 0x0C) write exactly what the public contract says
/// (GBATEK, "BIOS Memory Copy"): R0 source, R1 destination, R2 bits 0-20 the unit count, bit 24 a fixed source (fill)
/// and, for CpuSet, bit 26 32-bit units. Both addresses round down to the unit size; CpuFastSet always moves words and
/// rounds its count up to a multiple of eight. Each case calls the vector from a cartridge in ARM and in Thumb state
/// and compares the whole destination window, canaries included, with that model.
/// </summary>
public sealed class FirmwareTransferLawTests {
    private const uint Source = 0x02000000;
    private const uint Destination = 0x02001000;
    private const int Window = 0x180;
    private const byte Canary = 0xCC;
    private const int MaximumInstructions = 2_000_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpuSetMatchesTheContract(bool thumb) {
        using var core = CreateCore();

        foreach (var count in new uint[] { 0, 1, 2, 7, 8, 9, 33 }) {
            foreach (var word in new[] { false, true }) {
                foreach (var fill in new[] { false, true }) {
                    foreach (var (sourceSkew, destinationSkew) in new (uint, uint)[] { (0, 0), (1, 3), (2, 2), (3, 1) }) {
                        var control = (count | (fill ? (1u << 24) : 0u) | (word ? (1u << 26) : 0u));

                        Check(
                            control: control,
                            core: core,
                            destination: (Destination + destinationSkew),
                            fast: false,
                            source: (Source + sourceSkew),
                            thumb: thumb
                        );
                    }
                }
            }
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpuFastSetMatchesTheContract(bool thumb) {
        using var core = CreateCore();

        foreach (var count in new uint[] { 0, 1, 7, 8, 9, 16, 17 }) {
            foreach (var fill in new[] { false, true }) {
                foreach (var (sourceSkew, destinationSkew) in new (uint, uint)[] { (0, 0), (2, 1), (3, 3) }) {
                    Check(
                        control: (count | (fill ? (1u << 24) : 0u)),
                        core: core,
                        destination: (Destination + destinationSkew),
                        fast: true,
                        source: (Source + sourceSkew),
                        thumb: thumb
                    );
                }
            }
        }
    }

    // The contract's result over a byte image of the destination window, which starts at Destination.
    private static byte[] Expected(ReadOnlySpan<byte> sourceImage, uint source, uint destination, uint control, bool fast) {
        var window = new byte[Window];

        window.AsSpan().Fill(value: Canary);

        var count = (control & 0x1FFFFFu);
        var width = ((fast || ((control & (1u << 26)) != 0u)) ? 4u : 2u);
        var fill = ((control & (1u << 24)) != 0u);

        if (fast) {
            count = ((count + 7u) & ~7u);
        }

        var from = ((source & ~(width - 1u)) - Source);
        var to = ((destination & ~(width - 1u)) - Destination);

        for (var unit = 0u; (unit < count); ++unit) {
            var read = (fill ? from : (from + (unit * width)));

            sourceImage.Slice(
                length: ((int)width),
                start: ((int)read)
            ).CopyTo(destination: window.AsSpan(start: ((int)(to + (unit * width)))));
        }

        return window;
    }
    private static void Check(AdvancedGamingBrickCore core, uint source, uint destination, uint control, bool fast, bool thumb) {
        var bus = core.Instance.Machine.Bus;
        var sourceImage = new byte[Window];

        for (var index = 0; (index < Window); ++index) {
            sourceImage[index] = ((byte)((index * 73) ^ (index >> 1) ^ 0x5A));
            bus.Write8(access: BusAccessType.NonSequential, address: (Source + ((uint)index)), value: sourceImage[index]);
            bus.Write8(access: BusAccessType.NonSequential, address: (Destination + ((uint)index)), value: Canary);
        }

        Call(
            core: core,
            number: (fast ? (byte)0x0C : (byte)0x0B),
            r0: source,
            r1: destination,
            r2: control,
            thumb: thumb
        );

        var expected = Expected(
            control: control,
            destination: destination,
            fast: fast,
            source: source,
            sourceImage: sourceImage
        );

        for (var index = 0; (index < Window); ++index) {
            var actual = bus.Read8(access: BusAccessType.NonSequential, address: (Destination + ((uint)index)));

            Assert.True(
                condition: (actual == expected[index]),
                userMessage: $"{(fast ? "CpuFastSet" : "CpuSet")} {(thumb ? "Thumb" : "ARM")} src=0x{source:X8} dst=0x{destination:X8} r2=0x{control:X8}: byte +0x{index:X3} is {actual:X2}, expected {expected[index]:X2}"
            );
        }
    }
    // Enters the cartridge stub for the vector with the given registers and steps until it reports the return.
    private static void Call(AdvancedGamingBrickCore core, byte number, uint r0, uint r1, uint r2, bool thumb) {
        var machine = core.Instance.Machine;
        var entry = (AdvancedGamingBrickMachine.CartridgeEntryPoint + EntryOffset(number: number, thumb: thumb));

        machine.Cpu.SetupDirectBoot(entryPoint: entry);
        machine.Cpu.SetRegister(index: 0, value: r0);
        machine.Cpu.SetRegister(index: 1, value: r1);
        machine.Cpu.SetRegister(index: 2, value: r2);
        machine.Cpu.SetRegister(index: 7, value: 0);

        for (var instruction = 0; (instruction < MaximumInstructions); ++instruction) {
            machine.Step();

            var pc = machine.Cpu.GetRegister(index: 15);

            if ((machine.Cpu.GetRegister(index: 7) == 0x5Au) && (pc >= entry) && (pc < (entry + 64u))) {
                return;
            }
        }

        Assert.Fail(message: $"SWI 0x{number:X2} did not return; pc=0x{machine.Cpu.GetRegister(index: 15):X8}");
    }
    private static AdvancedGamingBrickCore CreateCore() =>
        new(configuration: AgbFirmware.CreateConfiguration(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: CreateRom()
        ));
    // One ARM and one Thumb stub per vector: SWI, MOV r7,#0x5A, branch-to-self. The Thumb stub is reached through an
    // ARM BX trampoline whose literal carries the Thumb bit.
    private static byte[] CreateRom() {
        var rom = new byte[8192];

        foreach (var number in new byte[] { 0x0B, 0x0C }) {
            var arm = ((int)EntryOffset(number: number, thumb: false));

            BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: arm), value: (0xEF000000u | (((uint)number) << 16)));
            BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: (arm + 4)), value: 0xE3A0705Au);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: (arm + 8)), value: 0xEAFFFFFEu);

            var thumb = ((int)EntryOffset(number: number, thumb: true));

            BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: thumb), value: 0xE59FC000u);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: (thumb + 4)), value: 0xE12FFF1Cu);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: (thumb + 8)), value: ((AdvancedGamingBrickMachine.CartridgeEntryPoint + ((uint)thumb)) + 0x21u));
            BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: (thumb + 0x20)), value: ((ushort)(0xDF00 | number)));
            BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: (thumb + 0x22)), value: 0x275A);
            BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: (thumb + 0x24)), value: 0xE7FE);
        }

        return rom;
    }
    private static uint EntryOffset(byte number, bool thumb) =>
        ((((uint)number) * 64u) + (thumb ? 4096u : 0u));
}
