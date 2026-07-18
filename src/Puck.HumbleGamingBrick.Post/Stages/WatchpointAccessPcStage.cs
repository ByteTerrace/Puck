using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage (M-06): proves a watchpoint hit reports the PC of the instruction that actually made the access —
/// <c>SystemBus.NoteInstructionStart</c>'s latch, fed once per <c>Sm83.StepInstruction</c> dispatch — rather than
/// whatever the CPU's live PC has advanced to by drain time, across every granularity the debugger advances a paused
/// cabinet with: a budget <c>Run</c> (continuous and frame-sized alike) and a <c>StepInstruction</c> loop (single-step
/// and run-until alike, <c>GamingBrickChildNode</c>'s <c>hgb.step</c>/<c>hgb.frame</c>/<c>hgb.until</c>). A
/// hand-assembled program at the post-boot entry point puts one known write instruction and one known read instruction
/// at known addresses, so each leg arms exactly one watch kind and asserts the drained PC against the address that
/// literally performed the access.
/// <para>
/// Program (0x0100): <c>LD A,0x7A</c> (write's deterministic value) · <c>LD (0xC060),A</c> — the WRITE instruction,
/// PC 0x0102 · <c>LD A,(0xC050)</c> — the READ instruction, PC 0x0105 · <c>JR -2</c> — an infinite loop at 0x0108, so
/// over-running the budget or step count never runs off into unmapped memory.
/// </para>
/// </summary>
internal sealed class WatchpointAccessPcStage : IPostStage {
    private const ushort WriteInstructionPc = 0x0102;
    private const ushort WriteWatchAddress = 0xC060;
    private const byte WriteValue = 0x7A;
    private const ushort ReadInstructionPc = 0x0105;
    private const ushort ReadWatchAddress = 0xC050;
    private const byte ReadValue = 0x33;
    private const ushort LoopPc = 0x0108;

    // 0x0100: LD A,0x7A (3E 7A) · LD (0xC060),A (EA 60 C0) · LD A,(0xC050) (FA 50 C0) · JR -2 (18 FE, infinite loop).
    private static readonly byte[] Program = [0x3E, WriteValue, 0xEA, 0x60, 0xC0, 0xFA, 0x50, 0xC0, 0x18, 0xFE];

    /// <inheritdoc/>
    public string Name =>
        "watchpoint-access-pc";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var legs = new (string Mode, Action<MachineInstance> Advance)[] {
            ("continuous", static instance => instance.Machine.Run(tCycles: 256)),
            ("frame", static instance => instance.Machine.Run(tCycles: PostMachine.TCyclesPerFrame)),
            ("step", static instance => RunSteps(instance: instance, count: 2)),
            ("until", static instance => RunUntil(instance: instance, target: LoopPc)),
        };

        foreach (var (mode, advance) in legs) {
            if (RunLeg(mode: mode, read: false, write: true, watchAddress: WriteWatchAddress, expectedPc: WriteInstructionPc, expectedValue: WriteValue, expectedIsWrite: true, advance: advance) is { } writeFailure) {
                return PostStageOutcome.Fail(detail: writeFailure);
            }

            // "step" only needs 2 steps to reach the WRITE; the READ is the 3rd instruction, so its own leg needs one more.
            var readAdvance = ((mode == "step") ? (static instance => RunSteps(instance: instance, count: 3)) : advance);

            if (RunLeg(mode: mode, read: true, write: false, watchAddress: ReadWatchAddress, expectedPc: ReadInstructionPc, expectedValue: ReadValue, expectedIsWrite: false, advance: readAdvance) is { } readFailure) {
                return PostStageOutcome.Fail(detail: readFailure);
            }
        }

        return PostStageOutcome.Pass(detail: $"{legs.Length} execution modes (continuous/frame/step/until) x 2 access kinds (read/write) all report the accessing instruction's PC (write=0x{WriteInstructionPc:X4}, read=0x{ReadInstructionPc:X4}), not the CPU's post-advance PC");
    }

    private static string? RunLeg(string mode, bool read, bool write, ushort watchAddress, ushort expectedPc, byte expectedValue, bool expectedIsWrite, Action<MachineInstance> advance) {
        var rom = new byte[0x8000];

        Program.CopyTo(array: rom, index: 0x0100);

        using var instance = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);

        var bus = instance.GetRequiredService<SystemBus>();

        bus.WriteByte(address: ReadWatchAddress, value: ReadValue);
        bus.AddWatch(address: watchAddress, read: read, write: write);

        advance(instance);

        if (!bus.TryTakeWatchHit(address: out var hitAddress, value: out var hitValue, isWrite: out var hitIsWrite, pc: out var hitPc)) {
            return $"{mode}/{(write ? "write" : "read")}: no watch hit reported at all";
        }

        if (hitPc != expectedPc) {
            return $"{mode}/{(write ? "write" : "read")}: reported PC=0x{hitPc:X4}; expected the accessing instruction's PC 0x{expectedPc:X4}";
        }

        if (hitAddress != watchAddress) {
            return $"{mode}/{(write ? "write" : "read")}: reported address=0x{hitAddress:X4}; expected 0x{watchAddress:X4}";
        }

        if (hitValue != expectedValue) {
            return $"{mode}/{(write ? "write" : "read")}: reported value=0x{hitValue:X2}; expected 0x{expectedValue:X2}";
        }

        if (hitIsWrite != expectedIsWrite) {
            return $"{mode}/{(write ? "write" : "read")}: reported isWrite={hitIsWrite}; expected {expectedIsWrite}";
        }

        return null;
    }
    // Mirrors GamingBrickChildNode.DebugStep's loop shape: a fixed instruction count, no early exit.
    private static void RunSteps(MachineInstance instance, int count) {
        for (var index = 0; (index < count); ++index) {
            instance.Machine.StepInstruction();
        }
    }
    // Mirrors GamingBrickChildNode.DebugUntil's loop shape: single-step forward until a target PC (or a cap; the
    // program's infinite loop makes a cap unreachable here, so none is needed).
    private static void RunUntil(MachineInstance instance, ushort target) {
        var cpu = instance.GetRequiredService<ICpu>();

        while (cpu.ProgramCounter != target) {
            instance.Machine.StepInstruction();
        }
    }
}
