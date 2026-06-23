namespace Puck.GameBoy;

/// <summary>
/// The Sharp SM83 CPU core: the bus master that defines the machine-cycle vocabulary the rest of the system is
/// clocked against. It is cycle-stepped — every instruction performs its bus traffic through the
/// <see cref="ICpuBus"/> cycle accessors in the exact order and count the hardware does, so an instruction's
/// timing emerges from its access pattern rather than from a cycle table. <see cref="Step"/> runs one whole
/// instruction (or one machine cycle of <c>HALT</c>, or one interrupt dispatch).
/// </summary>
public sealed partial class Sm83 {
    private readonly ICpuBus m_bus;

    private byte m_a;
    private byte m_b;
    private byte m_c;
    private byte m_d;
    private byte m_e;
    private byte m_h;
    private byte m_l;
    private bool m_flagCarry;
    private bool m_flagHalfCarry;
    private bool m_flagSubtract;
    private bool m_flagZero;
    private bool m_halted;
    private bool m_haltBug;
    private bool m_interruptMasterEnable;
    private int m_interruptEnableDelay;
    private bool m_stopped;
    private ushort m_programCounter;
    private ushort m_stackPointer;

    /// <summary>Gets or sets the accumulator (<c>A</c>).</summary>
    public byte A {
        get => m_a;
        set => m_a = value;
    }
    /// <summary>Gets or sets the flags register (<c>F</c>); only the high nibble (Z, N, H, C) is significant.</summary>
    public byte F {
        get => (byte)(
            (m_flagZero ? 0x80 : 0x00) |
            (m_flagSubtract ? 0x40 : 0x00) |
            (m_flagHalfCarry ? 0x20 : 0x00) |
            (m_flagCarry ? 0x10 : 0x00)
        );
        set {
            m_flagCarry = ((value & 0x10) != 0);
            m_flagHalfCarry = ((value & 0x20) != 0);
            m_flagSubtract = ((value & 0x40) != 0);
            m_flagZero = ((value & 0x80) != 0);
        }
    }
    /// <summary>Gets or sets the <c>B</c> register.</summary>
    public byte B {
        get => m_b;
        set => m_b = value;
    }
    /// <summary>Gets or sets the <c>C</c> register.</summary>
    public byte C {
        get => m_c;
        set => m_c = value;
    }
    /// <summary>Gets or sets the <c>D</c> register.</summary>
    public byte D {
        get => m_d;
        set => m_d = value;
    }
    /// <summary>Gets or sets the <c>E</c> register.</summary>
    public byte E {
        get => m_e;
        set => m_e = value;
    }
    /// <summary>Gets or sets the <c>H</c> register.</summary>
    public byte H {
        get => m_h;
        set => m_h = value;
    }
    /// <summary>Gets or sets the <c>L</c> register.</summary>
    public byte L {
        get => m_l;
        set => m_l = value;
    }
    /// <summary>Gets or sets the <c>AF</c> register pair.</summary>
    public ushort AF {
        get => (ushort)((m_a << 8) | F);
        set {
            m_a = (byte)(value >> 8);
            F = (byte)value;
        }
    }
    /// <summary>Gets or sets the <c>BC</c> register pair.</summary>
    public ushort BC {
        get => (ushort)((m_b << 8) | m_c);
        set {
            m_b = (byte)(value >> 8);
            m_c = (byte)value;
        }
    }
    /// <summary>Gets or sets the <c>DE</c> register pair.</summary>
    public ushort DE {
        get => (ushort)((m_d << 8) | m_e);
        set {
            m_d = (byte)(value >> 8);
            m_e = (byte)value;
        }
    }
    /// <summary>Gets or sets the <c>HL</c> register pair.</summary>
    public ushort HL {
        get => (ushort)((m_h << 8) | m_l);
        set {
            m_h = (byte)(value >> 8);
            m_l = (byte)value;
        }
    }
    /// <summary>Gets or sets the stack pointer (<c>SP</c>).</summary>
    public ushort StackPointer {
        get => m_stackPointer;
        set => m_stackPointer = value;
    }
    /// <summary>Gets or sets the program counter (<c>PC</c>).</summary>
    public ushort ProgramCounter {
        get => m_programCounter;
        set => m_programCounter = value;
    }
    /// <summary>Gets or sets the interrupt master enable (<c>IME</c>).</summary>
    public bool InterruptMasterEnable {
        get => m_interruptMasterEnable;
        set => m_interruptMasterEnable = value;
    }
    /// <summary>Gets whether the CPU is halted, waiting for an interrupt to become pending.</summary>
    public bool IsHalted =>
        m_halted;
    /// <summary>Gets whether the CPU is stopped (CGB speed switch or low-power <c>STOP</c>).</summary>
    public bool IsStopped =>
        m_stopped;

    /// <summary>Initializes the CPU bound to a bus.</summary>
    /// <param name="bus">The bus the CPU drives.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bus"/> is <see langword="null"/>.</exception>
    public Sm83(ICpuBus bus) {
        ArgumentNullException.ThrowIfNull(bus);

        m_bus = bus;
    }

    /// <summary>Runs one instruction, or — when the CPU is halted — advances a single machine cycle, or services
    /// a pending interrupt. The bus is advanced for every cycle consumed.</summary>
    public void Step() {
        // Discharge the machine cycle deferred by the previous instruction's last access, so the interrupt
        // decision below and the upcoming fetch observe current peripheral state (the deferred-cycle model).
        m_bus.FlushPendingCycles();

        if (m_stopped) {
            // An illegal opcode hard-locks the CPU, and STOP halts it; in both cases the core does nothing
            // further while time keeps passing. (STOP's joypad/interrupt wake is modeled once the joypad exists.)
            m_bus.InternalCycle();

            return;
        }

        if (m_halted) {
            if (m_bus.Interrupts.HasPending) {
                m_halted = false;
            }
            else {
                // Time still passes while halted so a component can raise the interrupt that wakes the CPU.
                m_bus.InternalCycle();

                return;
            }
        }

        if (m_interruptMasterEnable && m_bus.Interrupts.HasPending) {
            ServiceInterrupt();

            return;
        }

        var opcode = FetchOpcode();

        Execute(opcode: opcode);

        // The EI enable is delayed by one instruction; DI cancels a pending enable by zeroing the counter.
        if (m_interruptEnableDelay > 0) {
            m_interruptEnableDelay -= 1;

            if (m_interruptEnableDelay == 0) {
                m_interruptMasterEnable = true;
            }
        }
    }

    private void ServiceInterrupt() {
        m_interruptMasterEnable = false;

        // Two internal cycles, then push PC high and low; the dispatched vector is sampled after the pushes so a
        // push that lands on IE/IF can redirect or cancel the dispatch (vector 0x0000).
        m_bus.InternalCycle();
        m_bus.InternalCycle();

        m_stackPointer -= 1;
        m_bus.WriteCycle(
            address: m_stackPointer,
            value: (byte)(m_programCounter >> 8)
        );

        // Latch the dispatched vector here — after the high-byte push (which may have just overwritten IE if SP
        // pointed at 0xFFFF), but before the low-byte push (too late to affect the decision). This is the ie_push
        // cancellation quirk: an IE write during the high push can redirect or cancel the dispatch (vector 0x0000).
        var hasPending = m_bus.Interrupts.TryGetPending(out var dispatched);

        m_stackPointer -= 1;
        m_bus.WriteCycle(
            address: m_stackPointer,
            value: (byte)m_programCounter
        );

        if (hasPending) {
            m_bus.Interrupts.Clear(kind: dispatched);
            m_programCounter = InterruptController.VectorFor(kind: dispatched);
        }
        else {
            m_programCounter = 0x0000;
        }

        m_bus.InternalCycle();
    }

    private byte FetchOpcode() {
        var opcode = m_bus.ReadCycle(address: m_programCounter);

        // The HALT bug: a HALT with IME=0 and an interrupt already pending fails to halt and re-reads the byte
        // after HALT without advancing PC, so the next opcode byte is fetched twice.
        if (m_haltBug) {
            m_haltBug = false;
        }
        else {
            m_programCounter += 1;
        }

        return opcode;
    }

    private byte ReadImmediate8() {
        var value = m_bus.ReadCycle(address: m_programCounter);

        m_programCounter += 1;

        return value;
    }
    private ushort ReadImmediate16() {
        var low = ReadImmediate8();
        var high = ReadImmediate8();

        return (ushort)((high << 8) | low);
    }

    private void Push16(ushort value) {
        // Each stack-pointer decrement drives the pre-decrement value onto the address bus through the IDU, which
        // triggers the OAM corruption bug when the stack points into OAM.
        m_bus.TriggerOamBug(address: m_stackPointer, isWrite: true);
        m_stackPointer -= 1;
        m_bus.WriteCycle(
            address: m_stackPointer,
            value: (byte)(value >> 8)
        );

        m_bus.TriggerOamBug(address: m_stackPointer, isWrite: true);
        m_stackPointer -= 1;
        m_bus.WriteCycle(
            address: m_stackPointer,
            value: (byte)value
        );
    }
    private ushort Pop16() {
        var low = m_bus.ReadCycle(address: m_stackPointer);

        // The post-increment likewise drives the pre-increment value onto the address bus.
        m_bus.TriggerOamBug(address: m_stackPointer, isWrite: true);
        m_stackPointer += 1;

        var high = m_bus.ReadCycle(address: m_stackPointer);

        m_bus.TriggerOamBug(address: m_stackPointer, isWrite: true);
        m_stackPointer += 1;

        return (ushort)((high << 8) | low);
    }

    /// <summary>Reads the 8-bit register selected by a 3-bit opcode field (<c>0</c>=B … <c>6</c>=(HL), <c>7</c>=A);
    /// the <c>(HL)</c> case performs a memory read cycle.</summary>
    private byte ReadOperand(int index) =>
        index switch {
            0 => m_b,
            1 => m_c,
            2 => m_d,
            3 => m_e,
            4 => m_h,
            5 => m_l,
            6 => m_bus.ReadCycle(address: HL),
            _ => m_a,
        };
    /// <summary>Writes the 8-bit register selected by a 3-bit opcode field; the <c>(HL)</c> case performs a
    /// memory write cycle.</summary>
    private void WriteOperand(int index, byte value) {
        switch (index) {
            case 0:
                m_b = value;

                break;
            case 1:
                m_c = value;

                break;
            case 2:
                m_d = value;

                break;
            case 3:
                m_e = value;

                break;
            case 4:
                m_h = value;

                break;
            case 5:
                m_l = value;

                break;
            case 6:
                m_bus.WriteCycle(
                    address: HL,
                    value: value
                );

                break;
            default:
                m_a = value;

                break;
        }
    }
}
