namespace Puck.AdvancedGamingBrick;

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

    // ARES's 3-stage instruction pipeline (component/processor/arm7tdmi). Each slot carries the fetched word; the
    // decode/execute slots also carry the instruction's address (for the exception link register) and the Thumb/IRQ
    // flags sampled when it was fetched. Fills are LAZY — a branch sets m_reload and the NEXT Step pays the refill —
    // so the per-instruction cycle accounting (and the boot pipeline-fill) is byte-identical to ARES.
    private uint m_fetchWord;
    private uint m_decodeWord;
    private uint m_executeWord;
    private uint m_fetchAddress;
    private uint m_decodeAddress;
    private uint m_executeAddress;
    private bool m_decodeThumb;
    private bool m_executeThumb;
    private bool m_reload;

    private uint m_cpsr;
    private bool m_irqLine;

    // ARES's two-stage interrupt-recognition pipeline (component/processor/arm7tdmi/instruction.cpp). m_decodeIrq
    // is sampled from CPSR.I when an instruction is fetched; it slides into m_executeIrq one boundary later, gated
    // by the live synchronizer, to decide whether that instruction is pre-empted by an IRQ. This delay is the
    // hardware's interrupt-recognition latency — not a tuned constant.
    private bool m_decodeIrq;
    private bool m_executeIrq;

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
        m_decodeIrq = false;
        m_executeIrq = false;
        m_nextFetchNonSequential = false;
        m_gpr[15] = 0x00000000u;

        // Lazy reload: the first Step refills the pipeline from the reset vector, charging the fill to that
        // instruction exactly as ARES does (no eager pre-fill before the clock starts).
        m_reload = true;
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

        m_reload = true; // lazily refill from the cartridge entry on the first Step (ARES boot accounting)
    }

    /// <inheritdoc/>
    public void Step() {
        // Commit the previous instruction's cycles to the scheduler and fire any peripheral events now due (which
        // may raise interrupts) before this instruction executes — the event-driven timing model.
        m_bus.ProcessEvents();

        if (m_bus.Halted) {
            m_bus.RunUntilInterrupt();
        }

        // ARES ARM7TDMI::instruction (instruction.cpp:23-41): refill the pipeline if a branch/exception left it
        // stale (lazy — the refill is charged here, to the consuming instruction), slide one stage, then either
        // take a pre-empting IRQ or execute the instruction now in the execute slot.
        if (m_reload) {
            Reload();
        }

        Fetch();

        if (m_executeIrq) {
            TakeIrqException();

            return;
        }

        var opcode = m_executeWord;

        if (m_executeThumb) {
            var thumbOpcode = (ushort)opcode;

            unsafe { s_thumbTable[thumbOpcode >> 8](this, thumbOpcode); }
        }
        else {
            var condition = opcode >> 28;

            if ((condition == 0xEu) || CheckCondition(cpu: this, condition: condition)) {
                unsafe {
                    var index = ((opcode >> 16) & 0xFF0u) | ((opcode >> 4) & 0xFu);

                    s_armTable[index](this, opcode);
                }
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

    // ARES ARM7TDMI::fetch (instruction.cpp:10-21): slide the pipeline one stage, gate the freshly-promoted
    // execute-stage IRQ flag against the live synchronizer (so it fires only if the line is still asserted), sample
    // the decode-stage Thumb/IRQ flags from the current CPSR, then advance R15 and refill the fetch slot.
    private void Fetch() {
        m_executeWord = m_decodeWord;
        m_executeAddress = m_decodeAddress;
        m_executeThumb = m_decodeThumb;
        m_executeIrq = m_decodeIrq && (m_bus.Synchronizer || m_irqLine);

        m_decodeWord = m_fetchWord;
        m_decodeAddress = m_fetchAddress;
        m_decodeThumb = ThumbState;
        m_decodeIrq = (m_cpsr & FlagI) == 0u;

        var thumb = ThumbState;

        m_gpr[15] += thumb ? 2u : 4u;
        m_fetchAddress = m_gpr[15];
        m_fetchWord = FetchWord(address: m_fetchAddress, thumb: thumb);
    }

    // A pipeline opcode read, charged as sequential unless a branch or data access made the next fetch non-seq.
    private uint FetchWord(uint address, bool thumb) {
        var access = m_nextFetchNonSequential
            ? BusAccessType.NonSequential
            : BusAccessType.Sequential;

        m_nextFetchNonSequential = false;

        return thumb
            ? m_bus.ReadCode16(address: address & ~1u, access: access)
            : m_bus.ReadCode32(address: address & ~3u, access: access);
    }

    // ARES ARM7TDMI::reload (instruction.cpp:1-8): after a branch/exception writes R15, the pipeline is refilled
    // lazily on the next Step — the first refill fetch is non-sequential, then fetch() slides+refills again, so the
    // three refill reads are charged to the instruction that consumes them (matching ARES, including at boot).
    private void Reload() {
        m_reload = false;
        m_nextFetchNonSequential = true;

        var thumb = ThumbState;

        m_fetchAddress = m_gpr[15];
        m_fetchWord = FetchWord(address: m_fetchAddress, thumb: thumb);

        Fetch();
    }

    // Redirects execution to an address; the pipeline reloads lazily on the next Step (ARES sets pipeline.reload
    // when R15 is written, rather than refetching immediately).
    private void BranchTo(uint address) {
        m_gpr[15] = address;
        m_reload = true;
    }

    // ARES ARM7TDMI::exception (instruction.cpp:43-52): bank the mode, save the old CPSR to its SPSR, set the
    // return link from the decode-stage address, mask IRQ, enter ARM state, and vector (which arms the reload).
    private void Exception(CpuMode mode, uint vector, uint linkRegister) {
        var savedCpsr = m_cpsr;

        SwitchMode(newMode: (uint)mode);

        m_bankSpsr[BankIndex(mode: (uint)mode)] = savedCpsr;
        m_gpr[14] = linkRegister;
        m_cpsr |= FlagI;
        m_cpsr &= ~FlagT;

        BranchTo(address: vector);
    }

    // A pre-empting IRQ recognised in Step. The link register is the decode-stage address (the instruction that was
    // about to execute); ARES adds 2 in Thumb so SUBS PC,LR,#4 returns to re-run it (instruction.cpp:27-30).
    private void TakeIrqException() {
        var linkRegister = m_decodeAddress + (m_executeThumb ? 2u : 0u);

        Exception(mode: CpuMode.Irq, vector: 0x18u, linkRegister: linkRegister);
    }

    private void SoftwareInterrupt() {
        // SWI returns with MOVS PC,LR straight to the following instruction (the decode-stage address).
        Exception(mode: CpuMode.Supervisor, vector: 0x08u, linkRegister: m_decodeAddress);
    }

    private void UndefinedInstruction() {
        Exception(mode: CpuMode.Undefined, vector: 0x04u, linkRegister: m_decodeAddress);
    }
}
