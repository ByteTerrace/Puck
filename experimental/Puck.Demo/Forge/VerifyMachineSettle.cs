using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge;

/// <summary>
/// The shared frame-boundary settle every forge verify driver runs after stepping frames: a fixed-size
/// <see cref="Machine.Run"/> can phase-lock its boundary INSIDE the VBlank handler's OAM DMA, where the bus
/// faithfully emulates the transfer conflict and every work-RAM read returns the DMA's in-flight bytes (the
/// shadow-OAM page — zeros, on a quiet screen). Whether the lock happens depends on the exact per-frame cycle
/// count, i.e. on the ROM's code layout — a battery that skips this settle passes or fails by layout luck.
/// Stepping is deterministic (a pure function of machine state), so replay assertions are unaffected; when the
/// program counter never visits the HRAM trampoline the settle is a no-op.
/// </summary>
internal static class VerifyMachineSettle {
    // The HRAM OAM-DMA trampoline's address range (the kernel copies it to 0xFF80; its countdown outlasts the
    // transfer by design, so the CPU leaving this range proves no OAM DMA is in flight).
    private const ushort DmaTrampolineFirst = FrameworkMemoryMap.DmaTrampoline;
    private const ushort DmaTrampolineLast = (ushort)(FrameworkMemoryMap.DmaTrampoline + 11);

    /// <summary>Steps the machine out of the OAM-DMA trampoline so subsequent bus reads observe real memory.</summary>
    /// <param name="machine">The machine to step.</param>
    /// <param name="cpu">Its CPU (for the program counter).</param>
    /// <param name="label">The battery's label for the wedge diagnostic.</param>
    public static void SettleOutOfOamDma(Machine machine, ICpu cpu, string label) {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(cpu);

        for (var guard = 0; (guard < 4096); guard++) {
            var pc = cpu.ProgramCounter;

            if ((pc < DmaTrampolineFirst) || (pc > DmaTrampolineLast)) {
                return;
            }

            machine.Run(tCycles: 8);
        }

        throw new InvalidOperationException(message: $"{label} ROM verification failed: the CPU never left the OAM-DMA trampoline (the VBlank handler is wedged).");
    }
}
