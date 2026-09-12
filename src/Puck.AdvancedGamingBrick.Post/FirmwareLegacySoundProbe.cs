namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Calls an opaque firmware-exported address using original IWRAM shim instructions.</summary>
internal static class FirmwareLegacySoundProbe {
    private const uint Entry = 0x03007000;

    internal static void InstallRecorder(IAgbBus bus, uint address, uint destination) {
        // Capture callback arguments and a count in caller-owned EWRAM. This
        // original ARM stub is unrelated to any selected BIOS implementation.
        ReadOnlySpan<uint> shim = [0xE59FC01C, 0xE88C000F, 0xE59C0010, 0xE2800001, 0xE58C0010, 0xE3A00000, 0xE12FFF1E, 0xE1A00000, 0xE1A00000, destination];
        for (var index = 0; index < shim.Length; ++index) {
            bus.Write32(address: address + (uint)(index * 4), value: shim[index], access: BusAccessType.NonSequential);
        }
    }

    internal static void CallAddress(AdvancedGamingBrickCore core, uint target, uint r0 = 0, uint r1 = 0, uint r2 = 0, uint r3 = 0, Action<AdvancedGamingBrickMachine>? setup = null) {
        FirmwareSwiProbe.Require(condition: (target & ~1u) < 0x4000 && target > 0, detail: "legacy sound callback is outside firmware");
        var machine = core.Instance.Machine;
        // LDR ip, literal; MOV lr, pc; BX ip; MOV r7, #0x5A; B .; target.
        // No instruction bytes are read from the selected BIOS.
        ReadOnlySpan<uint> shim = [0xE59FC00C, 0xE1A0E00F, 0xE12FFF1C, 0xE3A0705A, 0xEAFFFFFE, target];
        for (var index = 0; index < shim.Length; ++index) {
            machine.Bus.Write32(address: Entry + (uint)(index * 4), value: shim[index], access: BusAccessType.NonSequential);
        }
        machine.Cpu.SetupDirectBoot(entryPoint: Entry);
        machine.Cpu.SetRegister(index: 0, value: r0);
        machine.Cpu.SetRegister(index: 1, value: r1);
        machine.Cpu.SetRegister(index: 2, value: r2);
        machine.Cpu.SetRegister(index: 3, value: r3);
        machine.Cpu.SetRegister(index: 7, value: 0);
        setup?.Invoke(obj: machine);
        for (var instruction = 0; instruction < 2_000_000; ++instruction) {
            machine.Step();
            var pc = machine.Cpu.GetRegister(index: 15);
            if (machine.Cpu.GetRegister(index: 7) == 0x5A && pc >= Entry + 12 && pc < Entry + 24) {
                return;
            }
        }
        throw new InvalidOperationException(message: "legacy sound callback did not return within the native instruction budget");
    }
}
