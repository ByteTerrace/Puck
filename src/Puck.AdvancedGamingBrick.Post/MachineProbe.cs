namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Shared machinery for the reference-ROM probes that read their verdict from memory: step a machine until its
/// execution settles into a tight loop, and classify whether a settled program counter is in a legitimately executable
/// region (so a crash cannot masquerade as a pass).</summary>
internal static class MachineProbe {
    /// <summary>The hard ceiling on instructions a probe steps before giving up (a ROM that never settles).</summary>
    public const long StepCap = 64_000_000;

    private const long CheckInterval = 0x40000;

    /// <summary>Steps the machine until two checkpoints land in the same small (64-byte) window — execution has settled
    /// into a loop — or the step cap is reached.</summary>
    /// <param name="machine">The machine to advance.</param>
    public static void RunUntilSettled(PostMachine machine) {
        var lastPc = 0xFFFFFFFFu;

        for (long i = 1; (i <= StepCap); ++i) {
            machine.Machine.Step();

            if ((i % CheckInterval) == 0) {
                var pc = machine.Machine.Cpu.GetRegister(index: 15);

                if ((pc & ~0x3Fu) == (lastPc & ~0x3Fu)) {
                    return;
                }

                lastPc = pc;
            }
        }
    }

    /// <summary>Whether a program counter lies in a region the machine can legitimately fetch code from — BIOS, either WRAM,
    /// VRAM, or game-pak ROM and its mirrors. A settled PC anywhere else means the core ran off into unmapped memory
    /// rather than reaching the ROM's result loop.</summary>
    /// <param name="pc">The program counter to classify.</param>
    /// <returns><see langword="true"/> when the PC is in an executable region.</returns>
    public static bool IsExecutable(uint pc) {
        var region = (pc >> 24);

        return ((pc < 0x4000u)                              // BIOS
            || (region == 0x02u)                           // on-board WRAM
            || (region == 0x03u)                           // on-chip WRAM
            || (region == 0x06u)                           // VRAM (the nes test executes from here)
            || ((region >= 0x08u) && (region <= 0x0Du)));   // game-pak ROM + mirrors
    }
}
