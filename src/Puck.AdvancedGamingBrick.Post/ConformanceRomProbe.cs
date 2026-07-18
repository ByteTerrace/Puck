namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Runs a reference conformance-suite ROM and reads its verdict from the CPU register file. These ROMs accumulate the
/// first failing test number in <c>r12</c> (0 = every test passed) before spinning in a vsync/idle loop; with no display
/// driver the ROM hangs in that wait, but <c>r12</c> already holds the result. The probe steps the machine until
/// execution settles, guards against a settled PC in unmapped memory (a crash, not a pass), then reads <c>r12</c>.
/// </summary>
internal static class ConformanceRomProbe {
    private const int ProgramCounter = 15;
    private const int VerdictRegister = 12;

    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="romCase">The case to run.</param>
    /// <param name="bios">The BIOS image to boot with.</param>
    /// <returns>The pass/fail result and a one-line detail.</returns>
    public static (bool Pass, string Detail) Run(RomCase romCase, ReadOnlyMemory<byte> bios) {
        var rom = File.ReadAllBytes(path: romCase.FullPath);

        using var machine = PostMachine.Build(bios: bios, rom: rom);

        MachineProbe.RunUntilSettled(machine: machine);

        var pc = machine.Machine.Cpu.GetRegister(index: ProgramCounter);

        if (!MachineProbe.IsExecutable(pc: pc)) {
            return (false, $"ran off to unmapped PC 0x{pc:X8}");
        }

        var verdict = machine.Machine.Cpu.GetRegister(index: VerdictRegister);

        return ((verdict == 0u)
            ? (true, "all tests passed")
            : (false, $"first failing test = {verdict}"));
    }
}
