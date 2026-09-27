namespace Puck.HumbleGamingDeck;

/// <summary>A resumable NMOS 2A03 CPU. Each step performs exactly one bus access, including dummy and halted reads.</summary>
/// <typeparam name="TBus">A struct bus adapter specialized by the JIT, without an interface dispatch in the CPU path.</typeparam>
public sealed partial class HgdCpu<TBus> : ISnapshotable where TBus : struct, IHgdCpuBus {
    private const byte Carry = 1;
    private const byte Zero = 2;
    private const byte Interrupt = 4;
    private const byte Decimal = 8;
    private const byte Overflow = 64;
    private const byte Negative = 128;

    private readonly TBus m_bus;
    private readonly byte m_precharge;

    private byte m_a;
    private byte m_x;
    private byte m_y;

    private byte m_p = 0x24;

    private byte m_s;
    private ushort m_pc;
    private byte m_opcode;
    private byte m_step;

    private Sequence m_sequence = Sequence.Reset;

    private ushort m_address;
    private ushort m_baseAddress;
    private ushort m_pointer;
    private ushort m_vector;
    private byte m_operand;
    private byte m_data;
    private byte m_specialBus;
    private bool m_crossed;
    private JamPhase m_jamPhase;

    private bool m_ready = true;

    private bool m_irq;
    private bool m_nmi;
    private bool m_nmiPending;
    private bool m_irqSample;
    private bool m_nmiSample;
    private bool m_polledIrq;
    private bool m_polledNmi;
    private bool m_storeHalted;
    private ulong m_cycles;

    /// <summary>Initializes a new instance of the <see cref="HgdCpu{TBus}"/> class at the beginning of its seven-cycle reset sequence.</summary>
    /// <param name="bus">The bus adapter, borrowed for this CPU's lifetime.</param>
    /// <param name="model">The revision whose special-bus precharge applies.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> is unimplemented.</exception>
    public HgdCpu(TBus bus, HgdConsoleModel model = HgdConsoleModel.NtscRp2A03G) {
        m_bus = bus;
        m_precharge = model.SpecialBusPrecharge();
        m_specialBus = m_precharge;
    }

    /// <summary>Gets or sets the accumulator.</summary>
    public byte A {
        get => m_a;
        set => m_a = value;
    }
    /// <summary>Gets or sets the X index register.</summary>
    public byte X {
        get => m_x;
        set => m_x = value;
    }
    /// <summary>Gets or sets the Y index register.</summary>
    public byte Y {
        get => m_y;
        set => m_y = value;
    }
    /// <summary>Gets or sets status; bit 5 is always high and the nonphysical B bit is always low.</summary>
    public byte P {
        get => m_p;
        set => m_p = ((byte)((value | 0x20) & 0xEF));
    }
    /// <summary>Gets or sets the stack offset within page one.</summary>
    public byte S {
        get => m_s;
        set => m_s = value;
    }
    /// <summary>Gets or sets the program counter.</summary>
    public ushort ProgramCounter {
        get => m_pc;
        set => m_pc = value;
    }
    /// <summary>Gets or sets RDY. Low repeats reads, including their side effects; writes still complete.</summary>
    public bool Ready {
        get => m_ready;
        set => m_ready = value;
    }
    /// <summary>Gets or sets the active-high maskable interrupt input.</summary>
    public bool Irq {
        get => m_irq;
        set => m_irq = value;
    }
    /// <summary>Gets or sets the active-high NMI input. Each rising edge latches a request until serviced.</summary>
    public bool Nmi {
        get => m_nmi;
        set {
            if (value && !m_nmi) {
                m_nmiPending = true;
            }
            m_nmi = value;
        }
    }
    /// <summary>Gets the completed CPU-cycle count, including RDY repeats and JAM cycles.</summary>
    public ulong Cycles => m_cycles;
    /// <summary>Gets whether the next completed read fetches a new opcode or starts an interrupt.</summary>
    public bool AtInstructionBoundary => ((m_step == 0) && (m_sequence == Sequence.Instruction) && !IsJammed);
    /// <summary>Gets whether an illegal JAM encoding has locked the sequencer until reset.</summary>
    public bool IsJammed => (m_jamPhase != JamPhase.None);
    /// <summary>Gets the instruction or reset cycle index; JAM retains its entry cycle index.</summary>
    public byte Microcycle => m_step;
    /// <summary>Gets the last byte sampled or driven on the CPU data bus.</summary>
    public byte DataBus => m_data;

    /// <summary>Begins reset without changing A, X, Y, decimal status, or RAM. The bus performs three stack reads.</summary>
    public void Reset() {
        m_sequence = Sequence.Reset;
        m_step = 0;
        m_jamPhase = JamPhase.None;
        m_polledIrq = false;
        m_polledNmi = false;
        m_nmiPending = false;
        m_nmiSample = false;
    }
    /// <summary>Seeds a diagnostic instruction boundary without performing a reset.</summary>
    /// <param name="pc">The next opcode address.</param>
    /// <param name="a">The accumulator.</param>
    /// <param name="x">The X register.</param>
    /// <param name="y">The Y register.</param>
    /// <param name="p">The status register.</param>
    /// <param name="s">The stack offset.</param>
    public void Seed(ushort pc, byte a, byte x, byte y, byte p, byte s) {
        m_pc = pc;
        m_a = a;
        m_x = x;
        m_y = y;
        P = p;
        m_s = s;
        m_opcode = 0;
        m_step = 0;
        m_sequence = Sequence.Instruction;
        m_operand = 0;
        m_data = 0;
        m_address = 0;
        m_baseAddress = 0;
        m_pointer = 0;
        m_vector = 0;
        m_specialBus = m_precharge;
        m_crossed = false;
        m_jamPhase = JamPhase.None;
        m_irq = false;
        m_nmi = false;
        m_nmiPending = false;
        m_irqSample = false;
        m_nmiSample = false;
        m_polledIrq = false;
        m_polledNmi = false;
        m_storeHalted = false;
        m_ready = true;
        m_cycles = 0;
    }
    /// <summary>Completes one CPU cycle. A read always reaches the bus even when RDY prevents sequencer advance.</summary>
    public void StepCycle() {
        var access = NextAccess();

        if (access.Write) {
            m_bus.Write(address: access.Address, value: access.Value);
            m_data = access.Value;
        } else {
            m_data = m_bus.Read(address: access.Address);
        }
        ++m_cycles;
        if (!access.Write && !m_ready) {
            // The input latch still samples during RDY: unstable immediate feedback ANDs every held operand.
            // https://www.nesdev.org/wiki/Visual6502wiki/6502_Opcode_8B_(XAA,_ANE)
            if ((m_sequence == Sequence.Instruction) && (m_step == 1) && (CurrentOperation is Operation.Xaa or Operation.Lxa)) {
                m_specialBus &= m_data;
                m_a &= m_data;
            }
            if ((m_sequence == Sequence.Instruction) && IsUnstableStoreDummy()) {
                m_storeHalted = true;
            }
        } else {
            Advance(value: m_data);
        }
        m_irqSample = m_irq;
        m_nmiSample = m_nmiPending;
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    }
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain a complete CPU state.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    }

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        var sequence = ((byte)m_sequence);
        var jamPhase = ((byte)m_jamPhase);

        transfer.Byte(value: ref m_a);
        transfer.Byte(value: ref m_x);
        transfer.Byte(value: ref m_y);
        transfer.Byte(value: ref m_p);
        transfer.Byte(value: ref m_s);
        transfer.UInt16(value: ref m_pc);
        transfer.Byte(value: ref m_opcode);
        transfer.Byte(value: ref m_step);
        transfer.Byte(value: ref sequence);
        transfer.UInt16(value: ref m_address);
        transfer.UInt16(value: ref m_baseAddress);
        transfer.UInt16(value: ref m_pointer);
        transfer.UInt16(value: ref m_vector);
        transfer.Byte(value: ref m_operand);
        transfer.Byte(value: ref m_data);
        transfer.Byte(value: ref m_specialBus);
        transfer.Boolean(value: ref m_crossed);
        transfer.Byte(value: ref jamPhase);
        transfer.Boolean(value: ref m_ready);
        transfer.Boolean(value: ref m_irq);
        transfer.Boolean(value: ref m_nmi);
        transfer.Boolean(value: ref m_nmiPending);
        transfer.Boolean(value: ref m_irqSample);
        transfer.Boolean(value: ref m_nmiSample);
        transfer.Boolean(value: ref m_polledIrq);
        transfer.Boolean(value: ref m_polledNmi);
        transfer.Boolean(value: ref m_storeHalted);
        transfer.UInt64(value: ref m_cycles);

        m_sequence = ((Sequence)sequence);
        m_jamPhase = ((JamPhase)jamPhase);
    }
    private void PollInterrupts() {
        m_polledIrq |= (m_irqSample && ((m_p & Interrupt) == 0));
        m_polledNmi |= m_nmiSample;
    }
    private void Finish(bool poll = true) {
        if (poll) {
            PollInterrupts();
        }
        m_step = 0;
        m_sequence = Sequence.Instruction;
    }

    private enum Sequence : byte {
        Instruction,
        Reset,
        Irq,
        Nmi,
    }
    private enum JamPhase : byte {
        None,
        FirstHighRead,
        FirstLowRead,
        SecondLowRead,
        RepeatedHighRead,
    }
    private readonly record struct Access(ushort Address, byte Value = 0, bool Write = false);
}
