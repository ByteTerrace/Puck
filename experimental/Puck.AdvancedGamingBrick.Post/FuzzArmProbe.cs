namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Runs a FuzzARM ROM (randomized ARM/Thumb instruction coverage) and reads its verdict from the start of EWRAM: on the
/// first failing instruction the ROM dumps a marker (<c>'AAAA'</c> for ARM, <c>'TTTT'</c> for Thumb) to
/// <c>0x02000000</c> and otherwise leaves it zero. The probe steps the machine until execution settles, then inspects
/// that word.
/// </summary>
internal static class FuzzArmProbe {
    private const uint EwramStart = 0x02000000u;
    private const uint MarkerArm = 0x41414141u;   // 'AAAA'
    private const uint MarkerThumb = 0x54545454u; // 'TTTT'

    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="romCase">The case to run.</param>
    /// <param name="bios">The BIOS image to boot with.</param>
    /// <returns>The pass/fail result and a one-line detail.</returns>
    public static (bool Pass, string Detail) Run(RomCase romCase, ReadOnlyMemory<byte> bios) {
        var rom = File.ReadAllBytes(path: romCase.FullPath);

        using var machine = PostMachine.Build(bios: bios, rom: rom);

        MachineProbe.RunUntilSettled(machine: machine);

        var marker = machine.Machine.Bus.Read32(address: EwramStart, access: BusAccessType.NonSequential);

        return ((marker != MarkerArm) && (marker != MarkerThumb))
            ? (true, "all tests passed")
            : (false, $"failure marker '{(char)(marker & 0xFFu)}' dumped to EWRAM (0x{marker:X8})");
    }
}
