namespace Puck.GameBoyAdvance;

/// <summary>
/// The ARM7TDMI core, behind a swappable seam so the machine can host the production core, a tracing decorator,
/// or an alternate implementation under test without changing its wiring. The CPU drives the machine entirely
/// through its <see cref="IGbaBus"/>; this surface is what the machine and the conformance harness need: reset,
/// single-instruction stepping, the IRQ line, and register/CPSR inspection for test fingerprints.
/// </summary>
public interface IArmCpu {
    /// <summary>Resets the core to its power-on state and reloads the pipeline from the reset vector.</summary>
    void Reset();

    /// <summary>Initialises the core to the post-BIOS "direct boot" state — System mode with the standard stack
    /// pointers the BIOS leaves behind (SP_sys, SP_irq, SP_svc) — and begins execution at
    /// <paramref name="entryPoint"/>. Used to launch a cartridge without running the BIOS.</summary>
    /// <param name="entryPoint">The address to begin executing from (typically the cartridge entry, 0x08000000).</param>
    void SetupDirectBoot(uint entryPoint);

    /// <summary>Executes one instruction, charging its cycles to the machine through the bus.</summary>
    void Step();

    /// <summary>Gets or sets the level of the IRQ line the interrupt controller drives. While asserted and IRQs
    /// are enabled in the CPSR, the core takes the IRQ exception at the next instruction boundary.</summary>
    bool IrqLine { get; set; }

    /// <summary>Reads a general-purpose register as the currently visible mode bank sees it.</summary>
    /// <param name="index">The register number, 0–15 (15 is the program counter).</param>
    /// <returns>The register value.</returns>
    uint GetRegister(int index);

    /// <summary>Writes a general-purpose register in the currently visible mode bank. Writing R15 does not
    /// reload the pipeline; use it only for test setup, not to model a branch.</summary>
    /// <param name="index">The register number, 0–15.</param>
    /// <param name="value">The value to store.</param>
    void SetRegister(int index, uint value);

    /// <summary>Gets the current program status register.</summary>
    uint Cpsr { get; }
}
