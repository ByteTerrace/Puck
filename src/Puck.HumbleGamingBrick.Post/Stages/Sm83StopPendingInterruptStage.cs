using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage (M-05): proves <c>Sm83.Decode.cs</c>'s <c>ExecuteStop</c> consumes STOP's pad byte only when no
/// interrupt is pending — the SameBoy-pinned <c>IE &amp; IF &amp; 0x1F</c> condition (<c>sm83_cpu.c</c> <c>stop()</c>,
/// ~lines 397/405), independent of IME. A synthetic two-byte program (<c>10 00</c> — STOP, pad <c>00</c>) is stepped one
/// instruction at a time on a bare DMG machine (no boot ROM, no Color speed-switch branch, to isolate the pad-byte
/// decision from KEY1): with no interrupt pending the pad is consumed in the same dispatch (PC+2, parked); with one
/// already latched (IME left disabled, so it is not serviced) the pad is left unconsumed (PC+1) and decoded as the very
/// next instruction on the following step (PC+2) — the PC sequence a corpus-vector comparison cannot express, since
/// SingleStepTests/sm83 never carries a pending-interrupt STOP vector (see <see cref="Sm83SstStage"/>'s conflict-skip
/// note).
/// </summary>
internal sealed class Sm83StopPendingInterruptStage : IPostStage {
    // 0x0100: STOP (10), pad byte (00 — decodes as NOP if left for the next fetch, as the pending-interrupt case does).
    private static readonly byte[] Program = [0x10, 0x00];

    /// <inheritdoc/>
    public string Name =>
        "sm83-stop-pending-interrupt";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (RunNoPendingLeg() is { } noPendingFailure) {
            return PostStageOutcome.Fail(detail: noPendingFailure);
        }

        if (RunPendingLeg() is { } pendingFailure) {
            return PostStageOutcome.Fail(detail: pendingFailure);
        }

        return PostStageOutcome.Pass(detail: "STOP consumes its pad byte (PC+2) with no interrupt pending, and leaves it for the next fetch (PC+1, then executed as PC+2) with IE&IF already latched and IME disabled");
    }

    // No interrupt pending: STOP's one dispatch consumes both bytes (PC 0x0100 -> 0x0102) and the (monochrome) CPU
    // parks — a second step, still with nothing pending, must not move PC further.
    private static string? RunNoPendingLeg() {
        using var machine = BuildMachine();

        machine.Machine.StepInstruction();

        if (machine.GetRequiredService<ICpu>().ProgramCounter != 0x0102) {
            return $"no-pending STOP: PC=0x{machine.GetRequiredService<ICpu>().ProgramCounter:X4} after one step; expected 0x0102 (pad byte consumed)";
        }

        machine.Machine.StepInstruction();

        return ((machine.GetRequiredService<ICpu>().ProgramCounter == 0x0102)
            ? null
            : $"no-pending STOP: PC=0x{machine.GetRequiredService<ICpu>().ProgramCounter:X4} after a second step; expected 0x0102 (still parked)");
    }
    // An interrupt already latched (IE & IF both set) with IME disabled — never serviced, so it stays pending across
    // both steps: STOP's dispatch must leave PC at 0x0101 (pad byte NOT consumed), and the NEXT step must decode that
    // pad byte as its own instruction, landing PC at 0x0102.
    private static string? RunPendingLeg() {
        using var machine = BuildMachine();
        var bus = machine.GetRequiredService<ISystemBus>();

        bus.WriteByte(address: MemoryMap.InterruptEnable, value: (byte)InterruptKind.VBlank);
        bus.WriteByte(address: MemoryMap.InterruptFlag, value: (byte)InterruptKind.VBlank);

        machine.Machine.StepInstruction();

        if (machine.GetRequiredService<ICpu>().ProgramCounter != 0x0101) {
            return $"pending STOP: PC=0x{machine.GetRequiredService<ICpu>().ProgramCounter:X4} after the STOP dispatch; expected 0x0101 (pad byte left for the next fetch)";
        }

        machine.Machine.StepInstruction();

        return ((machine.GetRequiredService<ICpu>().ProgramCounter == 0x0102)
            ? null
            : $"pending STOP: PC=0x{machine.GetRequiredService<ICpu>().ProgramCounter:X4} after the follow-up step; expected 0x0102 (pad byte executed as the next instruction)");
    }
    // A bare monochrome machine (no boot ROM, seeded post-boot handoff) isolates STOP's pad-byte decision from the
    // Color speed-switch branch (KEY1), which is a separate, already-covered path.
    private static MachineInstance BuildMachine() {
        var rom = new byte[0x8000];

        Program.CopyTo(array: rom, index: 0x0100);

        return PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);
    }
}
