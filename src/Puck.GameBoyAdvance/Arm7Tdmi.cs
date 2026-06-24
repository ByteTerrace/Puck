namespace Puck.GameBoyAdvance;

/// <summary>
/// The ARM7TDMI core: an ARMv4T processor with a 3-stage (fetch/decode/execute) pipeline, running either the
/// 32-bit ARM or the 16-bit Thumb instruction set. Timing emerges from the bus accessors — the core counts no
/// cycles itself; each fetch, data transfer, and idle charges the machine through <see cref="IGbaBus"/>, the
/// same deferred-cycle discipline proven on the DMG/CGB core. The pipeline is modelled so that reading R15
/// yields the executing instruction's address plus the architectural prefetch offset (8 in ARM, 4 in Thumb).
/// </summary>
public sealed partial class Arm7Tdmi : IArmCpu {
    private const uint FlagN = 1u << 31;
    private const uint FlagZ = 1u << 30;
    private const uint FlagC = 1u << 29;
    private const uint FlagV = 1u << 28;
    private const uint FlagI = 1u << 7;
    private const uint FlagF = 1u << 6;
    private const uint FlagT = 1u << 5;
    private const uint ModeMask = 0x1Fu;

    private readonly IGbaBus m_bus;

    // The 16 currently visible general-purpose registers (R0–R15); R13=SP, R14=LR, R15=PC.
    private readonly uint[] m_gpr = new uint[16];

    // Banked R13/R14/SPSR, indexed by bank (0=usr/sys, 1=fiq, 2=irq, 3=svc, 4=abt, 5=und).
    private readonly uint[] m_bankR13 = new uint[6];
    private readonly uint[] m_bankR14 = new uint[6];
    private readonly uint[] m_bankSpsr = new uint[6];

    // R8–R12 have a dedicated FIQ bank; every non-FIQ mode shares the other set.
    private readonly uint[] m_fiqR8to12 = new uint[5];
    private readonly uint[] m_usrR8to12 = new uint[5];

    // The two prefetched instruction words (decode and fetch slots of the 3-stage pipeline).
    private readonly uint[] m_pipe = new uint[2];

    private uint m_cpsr;
    private bool m_irqLine;

    // Set when the executing instruction redirected the PC, so Step() skips the normal sequential advance.
    private bool m_branched;

    // Set after a data transfer so the next opcode fetch is charged as non-sequential.
    private bool m_nextFetchNonSequential;

    /// <summary>Creates the core bound to a bus and resets it.</summary>
    /// <param name="bus">The system bus the core drives every access through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bus"/> is <see langword="null"/>.</exception>
    public Arm7Tdmi(IGbaBus bus) {
        ArgumentNullException.ThrowIfNull(bus);

        m_bus = bus;

        Reset();
    }

    /// <inheritdoc/>
    public uint Cpsr => m_cpsr;

    /// <inheritdoc/>
    public bool IrqLine {
        get => m_irqLine;
        set => m_irqLine = value;
    }

    private bool ThumbState => (m_cpsr & FlagT) != 0u;

    private uint CurrentMode => m_cpsr & ModeMask;

    private bool HasSpsr => BankIndex(mode: CurrentMode) != 0;

    /// <summary>Gets or sets the SPSR of the current mode; in User/System mode (which have none) the getter
    /// returns the CPSR and the setter is ignored.</summary>
    private uint Spsr {
        get => HasSpsr ? m_bankSpsr[BankIndex(mode: CurrentMode)] : m_cpsr;
        set {
            if (HasSpsr) {
                m_bankSpsr[BankIndex(mode: CurrentMode)] = value;
            }
        }
    }

    /// <inheritdoc/>
    public uint GetRegister(int index) => m_gpr[index];

    /// <inheritdoc/>
    public void SetRegister(int index, uint value) => m_gpr[index] = value;

    /// <inheritdoc/>
    public void Reset() {
        Array.Clear(array: m_gpr);
        Array.Clear(array: m_bankR13);
        Array.Clear(array: m_bankR14);
        Array.Clear(array: m_bankSpsr);
        Array.Clear(array: m_fiqR8to12);
        Array.Clear(array: m_usrR8to12);

        // Power-on: Supervisor mode, ARM state, both interrupt lines masked, executing from the reset vector.
        m_cpsr = (uint)CpuMode.Supervisor | FlagI | FlagF;
        m_irqLine = false;
        m_branched = false;
        m_nextFetchNonSequential = false;
        m_gpr[15] = 0x00000000u;

        ReloadPipeline();
    }

    /// <inheritdoc/>
    public void SetupDirectBoot(uint entryPoint) {
        Reset();

        // Lay down the stack pointers the GBA BIOS leaves for each mode, then settle in System mode and vector
        // to the entry point. Each SwitchMode banks the SP just written before loading the next mode's bank.
        m_gpr[13] = 0x03007FE0u; // SP_svc (we are in Supervisor after Reset)
        SwitchMode(newMode: (uint)CpuMode.Irq);
        m_gpr[13] = 0x03007FA0u; // SP_irq
        SwitchMode(newMode: (uint)CpuMode.System);
        m_gpr[13] = 0x03007F00u; // SP_sys / SP_usr

        m_cpsr = (uint)CpuMode.System; // System mode, ARM state, interrupts unmasked at the CPSR level
        m_gpr[15] = entryPoint;

        ReloadPipeline();
    }

    /// <inheritdoc/>
    public void Step() {
        // Commit the previous instruction's cycles to the scheduler and fire any peripheral events now due (which
        // may raise interrupts) before this instruction's boundary interrupt sample — the event-driven timing model.
        m_bus.ProcessEvents();

        // Interrupts are sampled at the instruction boundary; taking one consumes this step. The line comes from
        // the bus's interrupt controller (or the directly-set IrqLine, for isolated CPU tests).
        if ((m_irqLine || m_bus.IrqPending) && ((m_cpsr & FlagI) == 0u)) {
            TakeIrq();

            return;
        }

        m_branched = false;

        var fetchType = m_nextFetchNonSequential
            ? BusAccessType.NonSequential
            : BusAccessType.Sequential;

        m_nextFetchNonSequential = false;

        if (ThumbState) {
            var address = m_gpr[15];
            var opcode = (ushort)m_pipe[0];

            m_pipe[0] = m_pipe[1];
            m_pipe[1] = m_bus.ReadCode16(address: address, access: fetchType);

            ExecuteThumb(opcode: opcode);

            if (!m_branched) {
                m_gpr[15] = address + 2u;
            }
        }
        else {
            var address = m_gpr[15];
            var opcode = m_pipe[0];

            m_pipe[0] = m_pipe[1];
            m_pipe[1] = m_bus.ReadCode32(address: address, access: fetchType);

            ExecuteArm(opcode: opcode);

            if (!m_branched) {
                m_gpr[15] = address + 4u;
            }
        }
    }

    // Maps a processor mode to its banked-register set.
    private static int BankIndex(uint mode) => mode switch {
        (uint)CpuMode.Fiq => 1,
        (uint)CpuMode.Irq => 2,
        (uint)CpuMode.Supervisor => 3,
        (uint)CpuMode.Abort => 4,
        (uint)CpuMode.Undefined => 5,
        _ => 0,
    };

    // Swaps the banked registers from the current mode out and the target mode's in, then records the new mode.
    private void SwitchMode(uint newMode) {
        var oldMode = CurrentMode;
        var oldBank = BankIndex(mode: oldMode);
        var newBank = BankIndex(mode: newMode);

        m_bankR13[oldBank] = m_gpr[13];
        m_bankR14[oldBank] = m_gpr[14];

        var oldFiq = oldMode == (uint)CpuMode.Fiq;
        var newFiq = newMode == (uint)CpuMode.Fiq;

        if (oldFiq != newFiq) {
            if (oldFiq) {
                for (var i = 0; i < 5; ++i) {
                    m_fiqR8to12[i] = m_gpr[8 + i];
                    m_gpr[8 + i] = m_usrR8to12[i];
                }
            }
            else {
                for (var i = 0; i < 5; ++i) {
                    m_usrR8to12[i] = m_gpr[8 + i];
                    m_gpr[8 + i] = m_fiqR8to12[i];
                }
            }
        }

        m_gpr[13] = m_bankR13[newBank];
        m_gpr[14] = m_bankR14[newBank];
        m_cpsr = (m_cpsr & ~ModeMask) | (newMode & ModeMask);
    }

    // Applies a whole CPSR value, performing a bank swap first when the mode field changes.
    private void WriteCpsr(uint value) {
        if ((value & ModeMask) != CurrentMode) {
            SwitchMode(newMode: value & ModeMask);
        }

        m_cpsr = value;
    }

    // Refills both pipeline slots from the (newly set) PC; the first fetch is non-sequential.
    private void ReloadPipeline() {
        if (ThumbState) {
            var pc = m_gpr[15] & ~1u;

            m_pipe[0] = m_bus.ReadCode16(address: pc, access: BusAccessType.NonSequential);
            m_pipe[1] = m_bus.ReadCode16(address: pc + 2u, access: BusAccessType.Sequential);
            m_gpr[15] = pc + 4u;
        }
        else {
            var pc = m_gpr[15] & ~3u;

            m_pipe[0] = m_bus.ReadCode32(address: pc, access: BusAccessType.NonSequential);
            m_pipe[1] = m_bus.ReadCode32(address: pc + 4u, access: BusAccessType.Sequential);
            m_gpr[15] = pc + 8u;
        }

        m_branched = true;
        m_nextFetchNonSequential = false;
    }

    // Redirects execution to an address and refills the pipeline.
    private void BranchTo(uint address) {
        m_gpr[15] = address;

        ReloadPipeline();
    }

    // Common exception entry: bank the mode, save the old CPSR to its SPSR, set the return link, mask IRQ,
    // enter ARM state, and vector.
    private void TakeException(CpuMode mode, uint vector, uint linkRegister) {
        var savedCpsr = m_cpsr;

        SwitchMode(newMode: (uint)mode);

        m_bankSpsr[BankIndex(mode: (uint)mode)] = savedCpsr;
        m_gpr[14] = linkRegister;
        m_cpsr |= FlagI;
        m_cpsr &= ~FlagT;

        BranchTo(address: vector);
    }

    private void TakeIrq() {
        // SUBS PC,LR,#4 returns to the interrupted instruction, so LR = interrupted-instruction address + 4.
        var linkRegister = m_gpr[15] - (ThumbState ? 0u : 4u);

        TakeException(mode: CpuMode.Irq, vector: 0x18u, linkRegister: linkRegister);
    }

    private void SoftwareInterrupt() {
        // SWI returns with MOVS PC,LR straight to the following instruction.
        var linkRegister = m_gpr[15] - (ThumbState ? 2u : 4u);

        TakeException(mode: CpuMode.Supervisor, vector: 0x08u, linkRegister: linkRegister);
    }

    private void UndefinedInstruction() {
        var linkRegister = m_gpr[15] - (ThumbState ? 2u : 4u);

        TakeException(mode: CpuMode.Undefined, vector: 0x04u, linkRegister: linkRegister);
    }
}
