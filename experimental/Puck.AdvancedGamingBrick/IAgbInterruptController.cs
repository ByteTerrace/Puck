namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The Advanced GamingBrick interrupt controller: the IE (enable), IF (request), and IME (master enable) registers
/// and the line they drive to the CPU. It holds no reference to the CPU or any peripheral — peripherals push
/// requests in via <see cref="Request"/>, and the CPU samples <see cref="Synchronizer"/> through the bus — so
/// the wiring stays acyclic and every part remains independently swappable. The registers are double-buffered
/// exactly like the cycle-stepped hardware reference (the per-cycle interrupt-step): writes land in a "next" stage and reads return
/// the committed stage, and <see cref="StepSync"/> shifts the pipeline once per master cycle. The 1-cycle
/// register-visibility delay and the 2-cycle overflow→recognition latency emerge from that pipeline, not from a
/// tuned constant.
/// </summary>
public interface IAgbInterruptController {
    /// <summary>Gets the level of the IRQ line into the CPU — the interrupt synchronizer, recomputed each
    /// master cycle by <see cref="StepSync"/> from the committed pipeline stage. This is what the CPU samples.</summary>
    bool Synchronizer { get; }

    /// <summary>Gets a value indicating whether an enabled interrupt is requested (committed IE &amp; IF), ignoring
    /// the master enable (IME). This is the condition that wakes the CPU from a HALT/STOP low-power state — halt
    /// resumes on any enabled+requested interrupt regardless of IME.</summary>
    bool HasPendingInterrupt { get; }

    /// <summary>Gets a value indicating whether the interrupt pipeline is settled — both stages agree and the
    /// synchronizer is already at its fixed point — so a batch of <see cref="StepSync"/> calls would change
    /// nothing and the per-cycle stepping over an idle span can be skipped.</summary>
    bool PipelineQuiescent { get; }

    /// <summary>Advances the IRQ recognition pipeline by one master cycle: recomputes the synchronizer from the
    /// committed stage, then shifts the programmed ("next") stage down into it (the interrupt-step). While the
    /// CPU is stalled by DMA the pipeline freezes (the synchronizer holds), matching the CPU-stall behavior.</summary>
    /// <param name="stallingCpu">Whether DMA is currently holding the CPU off the bus.</param>
    void StepSync(bool stallingCpu);

    /// <summary>Latches an interrupt request from a peripheral by setting its IF bit in the "next" stage; it
    /// becomes visible to the CPU one <see cref="StepSync"/> later.</summary>
    /// <param name="source">The interrupting source.</param>
    void Request(InterruptSource source);

    /// <summary>Reads one of the controller's 16-bit registers (IE 0x200, IF 0x202, IME 0x208). Returns the
    /// committed stage.</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);

    /// <summary>Writes one of the controller's 16-bit registers into the "next" stage. Writing IF acknowledges
    /// (write-one-to-clear).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);
}
