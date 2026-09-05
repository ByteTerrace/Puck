using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>Host-side co-simulation trace sink for <see cref="Sm83"/> (Puck.HumbleGamingBrick.Post's CosimDiagnostic).
/// Never serialized and never touched by the battery — a plain nullable field the hot fetch/dispatch path tests once,
/// the same dormant-guard shape as <c>SystemBus</c>'s debug watchpoints.</summary>
public interface ICpuTraceSink {
    /// <summary>Fires once per real instruction dispatch, at the boundary before it runs: <paramref name="pc"/> is the
    /// address the CPU is about to fetch from, and every register reflects the state left by the PRIOR instruction.</summary>
    void OnInstructionBoundary(ushort pc, byte a, byte f, byte b, byte c, byte d, byte e, byte h, byte l, ushort sp);
}

/// <summary>
/// The SM83 core (the LR35902's CPU), the machine's bus master. It executes one instruction per
/// <see cref="StepInstruction"/>; running the program is what drives the machine's timeline forward. Memory is reached
/// only through the bus, interrupts through the controller; the core holds nothing but its registers and a little
/// interrupt-enable state, all of which is snapshotted.
/// <para>
/// This is the core and its scaffolding — registers, the fetch/dispatch loop, interrupt servicing, and HALT. The ALU
/// and the instruction decode live in the sibling partials.
/// </para>
/// </summary>
public sealed partial class Sm83 : ICpu, ISnapshotable, IModeSwitchable {
    private const byte FlagCarry = 0x10;
    private const byte FlagHalfCarry = 0x20;
    private const byte FlagSubtract = 0x40;
    private const byte FlagZero = 0x80;

    private readonly ISystemBus m_bus;
    private readonly ComponentClock m_componentClock;
    // Dormant co-simulation trace seam: null on every battery run and every ordinary boot, so the fetch/dispatch path
    // pays one predicted-not-taken field test (see the guarded call in StepInstruction) and nothing else.
    private ICpuTraceSink? m_traceSink;
    // Concrete-typed like SystemBus's own collaborators: each interface below has exactly one production
    // implementation, and only ISystemBus is ever substituted (Sm83SstHarness's SST bus), so these calls devirtualize.
    private readonly HdmaController m_hdma;
    private readonly InterruptController m_interrupts;
    private readonly JoypadComponent m_joypad;
    private readonly Key1Component m_key1;

    // Mutable so a LIVE device swap re-gates the live model reads: ExecuteStop (color arms a KEY1 speed switch,
    // monochrome halts) and the I/O write conflicts, whose phases differ by family and by Color revision. The boot
    // register handoff (SeedPostBootState, incl. the AGB inc-b probe) stays construction-only.
    private bool m_samplesPaletteEarly;
    private bool m_supportsColor;
    // Cached so every 16-bit increment/decrement site tests one field instead of re-deriving the model question; a
    // Color machine never reaches NoteOamCorruption's body, let alone the bus/PPU call behind it.
    private bool m_hasOamCorruptionBug;
    private byte m_a;
    private byte m_b;
    private byte m_c;
    private byte m_d;
    private byte m_e;
    private byte m_f;
    private byte m_h;
    private byte m_l;
    private bool m_halted;
    private bool m_haltBug;
    private bool m_lockedUp;
    private bool m_interruptMasterEnable;
    private int m_interruptEnableCountdown;
    private ushort m_programCounter;
    private ushort m_stackPointer;

    /// <summary>Creates the CPU bound to the bus and interrupt controller. Without a boot ROM it is seeded to the
    /// model's documented post-boot register state so a cartridge can run from <c>0x0100</c>; with one it powers on
    /// cold at <c>0x0000</c> and executes the boot program.</summary>
    /// <param name="bus">The system bus the CPU reads and writes through.</param>
    /// <param name="interrupts">The interrupt controller the CPU dispatches from.</param>
    /// <param name="componentClock">The component clock the CPU drives one CPU T-cycle at a time as it executes.</param>
    /// <param name="key1">The Color speed-switch and stop unit the CPU performs STOP through.</param>
    /// <param name="hdma">The Color VRAM DMA unit, whose transfers stall the CPU.</param>
    /// <param name="joypad">The joypad, polled to wake the machine from stop mode.</param>
    /// <param name="configuration">The machine configuration, which selects the post-boot register state.</param>
    /// <param name="header">The cartridge header, which steers the Color boot ROM's register handoff for a monochrome
    /// cartridge (the compatibility-mode path leaves different values than a Color game).</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Sm83(ISystemBus bus, InterruptController interrupts, ComponentClock componentClock, Key1Component key1, HdmaController hdma, JoypadComponent joypad, MachineConfiguration configuration, CartridgeHeader header) {
        ArgumentNullException.ThrowIfNull(argument: bus);
        ArgumentNullException.ThrowIfNull(argument: interrupts);
        ArgumentNullException.ThrowIfNull(argument: componentClock);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: hdma);
        ArgumentNullException.ThrowIfNull(argument: joypad);
        ArgumentNullException.ThrowIfNull(argument: configuration);
        ArgumentNullException.ThrowIfNull(argument: header);

        m_bus = bus;
        m_componentClock = componentClock;
        m_hdma = hdma;
        m_interrupts = interrupts;
        m_joypad = joypad;
        m_key1 = key1;
        m_samplesPaletteEarly = configuration.Model.SamplesPaletteWriteEarly();
        m_supportsColor = configuration.Model.SupportsColor();
        m_hasOamCorruptionBug = configuration.Model.HasOamCorruptionBug();

        // With a boot ROM the CPU powers on cold — every register zero and PC at 0x0000, the overlay's reset vector —
        // and the boot program itself produces the handoff state. Without one, the documented handoff is seeded.
        if (configuration.BootRom is null) {
            SeedPostBootState(
                model: configuration.Model,
                header: header
            );
        }
    }

    /// <inheritdoc/>
    public byte A {
        get => m_a;
        set => m_a = value;
    }
    /// <inheritdoc/>
    public byte F {
        get => m_f;
        set => m_f = ((byte)(value & 0xF0));
    }
    /// <inheritdoc/>
    public byte B {
        get => m_b;
        set => m_b = value;
    }
    /// <inheritdoc/>
    public byte C {
        get => m_c;
        set => m_c = value;
    }
    /// <inheritdoc/>
    public byte D {
        get => m_d;
        set => m_d = value;
    }
    /// <inheritdoc/>
    public byte E {
        get => m_e;
        set => m_e = value;
    }
    /// <inheritdoc/>
    public byte H {
        get => m_h;
        set => m_h = value;
    }
    /// <inheritdoc/>
    public byte L {
        get => m_l;
        set => m_l = value;
    }
    /// <inheritdoc/>
    public ushort StackPointer {
        get => m_stackPointer;
        set => m_stackPointer = value;
    }
    /// <inheritdoc/>
    public ushort ProgramCounter {
        get => m_programCounter;
        set => m_programCounter = value;
    }
    /// <inheritdoc/>
    public bool IsHalted =>
        m_halted;
    /// <inheritdoc/>
    public bool InterruptMasterEnable =>
        m_interruptMasterEnable;

    private ushort Af {
        get => ((ushort)((m_a << 8) | m_f));
        set {
            m_a = ((byte)(value >> 8));
            m_f = ((byte)(value & 0xF0));
        }
    }
    private ushort Bc {
        get => ((ushort)((m_b << 8) | m_c));
        set {
            m_b = ((byte)(value >> 8));
            m_c = ((byte)value);
        }
    }
    private ushort De {
        get => ((ushort)((m_d << 8) | m_e));
        set {
            m_d = ((byte)(value >> 8));
            m_e = ((byte)value);
        }
    }
    private ushort Hl {
        get => ((ushort)((m_h << 8) | m_l));
        set {
            m_h = ((byte)(value >> 8));
            m_l = ((byte)value);
        }
    }

    /// <inheritdoc/>
    public void StepInstruction() {
        StepInstructionCore();
        // Nothing outlives the instruction with cycles owed: an instruction boundary is where every other component
        // samples the CPU, and where a snapshot can be taken.
        FlushBusCycles();
    }
    private void StepInstructionCore() {
        // M-06: the debug watchpoint PC witness. A cheap unconditional field write on the bus side (SystemBus.cs); the
        // bus itself decides whether anything downstream cares (a watch hit latches this, otherwise it is never read).
        m_bus.NoteInstructionStart(pc: m_programCounter);

        if (m_lockedUp) {
            // An illegal opcode wedges the CPU like real hardware: it fetches nothing and never advances, but time keeps
            // flowing — advance exactly one machine cycle so the PPU, timer, and the rest keep running and the screen
            // keeps refreshing. Interrupts cannot break the lock; only a reset (a fresh machine) clears it.
            IdleMachineCycle();

            return;
        }

        // The speed-switch stall runs one machine cycle at a time — STOP armed it, KEY1's countdowns run it — so the
        // machine stays steppable at instruction granularity through the whole re-gear. The clock is re-synced each
        // cycle (the speed flips two machine cycles in), and a pending interrupt aborts the stall like a halt wake once
        // the switch's interrupt-block window has closed.
        if (m_key1.IsSwitching) {
            IdleMachineCycle();

            m_componentClock.IsDoubleSpeed = m_key1.IsDoubleSpeed;

            if (
                !m_key1.AreInterruptsBlocked &&
                (m_interrupts.Pending != InterruptKind.None)
            ) {
                m_key1.CancelSwitch();
            }

            return;
        }

        // Stop mode parks the CPU until a button is held; the clock keeps running so the (blanked) PPU stays live.
        if (m_key1.IsStopped) {
            if (m_joypad.AnyButtonHeld) {
                m_key1.LeaveStop();
            } else {
                IdleMachineCycle();

                return;
            }
        }

        if (m_halted) {
            if (m_interrupts.Pending != InterruptKind.None) {
                m_halted = false;

                m_hdma.OnCpuWoke();
            } else {
                IdleMachineCycle();

                return;
            }
        }

        // A newly armed VRAM DMA transfer loses the race to a pending interrupt: the hardware only freezes the CPU at
        // its next fetch, so a dispatch already due runs to completion first. Once the unit owns the bus the roles flip
        // and dispatch waits for the transfer to finish.
        if (
            m_interruptMasterEnable &&
            !m_key1.AreInterruptsBlocked &&
            (m_interrupts.Pending != InterruptKind.None) &&
            !m_hdma.IsTransferLocked
        ) {
            ServiceInterrupt();

            return;
        }

        // A VRAM DMA transfer freezes the CPU while it owns the bus: no fetch, just time. The acknowledge tells the
        // unit the CPU has yielded; its start-up chain is measured from here.
        if (m_hdma.IsCpuStalled) {
            m_hdma.AcknowledgeStall();
            IdleMachineCycle();

            return;
        }

        // EI's delayed enable lands here: after the arbitration above has already used this step's PRE-flip IME (so a
        // pending interrupt cannot preempt the instruction the delay promised would run), but before this step's own
        // fetch and dispatch (so that instruction's own logic — HALT's bug check is the one that reads IME mid-dispatch
        // — observes the flip the instant it lands rather than one whole instruction late). Landing the flip after
        // Execute instead would leave HALT one step behind real hardware: EI immediately followed by HALT with an
        // interrupt already pending would halt for real rather than falling straight through into dispatch.
        AdvanceInterruptEnable();

        if (m_traceSink is not null) {
            m_traceSink.OnInstructionBoundary(
                a: m_a,
                b: m_b,
                c: m_c,
                d: m_d,
                e: m_e,
                f: m_f,
                h: m_h,
                l: m_l,
                pc: m_programCounter,
                sp: m_stackPointer
            );
        }

        byte opcode;

        if (m_haltBug) {
            // The HALT bug: the fetch after a bugged HALT reads the opcode without advancing PC, so that byte executes
            // twice (or an operand is consumed as an opcode).
            opcode = ReadCycle(address: m_programCounter);
            m_haltBug = false;
        } else {
            opcode = ReadNextByte();
        }

        Execute(opcode: opcode);
    }
    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) {
        m_samplesPaletteEarly = model.SamplesPaletteWriteEarly();
        m_supportsColor = model.SupportsColor();
        m_hasOamCorruptionBug = model.HasOamCorruptionBug();
    }
    // Reports a 16-bit register's pre-operation value to the bus for the OAM corruption bug's register-bump trigger,
    // but only on a revision that has it — the single guarded call site every INC/DEC rr, the stack pointer's implicit
    // move opening PUSH/CALL/RST/interrupt dispatch, and LD SP,HL funnel through, so a Color machine pays one field
    // test and nothing past it. A direct CPU read or write that itself lands on OAM is a separate trigger the bus
    // applies from ReadByte/WriteByte, independent of this one.
    private void NoteOamCorruption(ushort preValue) {
        if (m_hasOamCorruptionBug) {
            m_bus.NoteRegisterAddressBus(address: preValue);
        }
    }
    /// <summary>Arms or clears the co-simulation trace sink. Host-side debug state — never snapshotted, never touched
    /// by the battery.</summary>
    public void SetTraceSink(ICpuTraceSink? sink) =>
        m_traceSink = sink;
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteByte(value: m_a);
        writer.WriteByte(value: m_f);
        writer.WriteByte(value: m_b);
        writer.WriteByte(value: m_c);
        writer.WriteByte(value: m_d);
        writer.WriteByte(value: m_e);
        writer.WriteByte(value: m_h);
        writer.WriteByte(value: m_l);
        writer.WriteUInt16(value: m_stackPointer);
        writer.WriteUInt16(value: m_programCounter);
        writer.WriteBoolean(value: m_halted);
        writer.WriteBoolean(value: m_haltBug);
        writer.WriteBoolean(value: m_lockedUp);
        writer.WriteBoolean(value: m_interruptMasterEnable);
        writer.WriteInt32(value: m_interruptEnableCountdown);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_a = reader.ReadByte();
        m_f = reader.ReadByte();
        m_b = reader.ReadByte();
        m_c = reader.ReadByte();
        m_d = reader.ReadByte();
        m_e = reader.ReadByte();
        m_h = reader.ReadByte();
        m_l = reader.ReadByte();
        m_stackPointer = reader.ReadUInt16();
        m_programCounter = reader.ReadUInt16();
        m_halted = reader.ReadBoolean();
        m_haltBug = reader.ReadBoolean();
        m_lockedUp = reader.ReadBoolean();
        m_interruptMasterEnable = reader.ReadBoolean();
        m_interruptEnableCountdown = reader.ReadInt32();

        // The double-speed flag is snapshotted by KEY1, which loads before the CPU; re-apply it to the component clock,
        // whose speed is not itself part of the snapshot.
        m_componentClock.IsDoubleSpeed = m_key1.IsDoubleSpeed;
    }

    // The boot ROM's register handoff, per family. A F B C D E H L, with SP = 0xFFFE and PC = 0x0100 everywhere:
    //
    //   DMG0  01 00 FF 13 00 C1 84 03      SGB   01 00 00 14 00 00 C0 60
    //   DMG   01 B0 00 13 00 D8 01 4D      SGB2  FF 00 00 14 00 00 C0 60
    //   MGB   FF B0 00 13 00 D8 01 4D      CGB   11 80 00 00 FF 56 00 0D
    //
    // The DMG and MGB flags hold for a nonzero header checksum; hardware clears carry and half-carry (F = 0x80) when it
    // is zero, which is not modelled. A monochrome cartridge on Color hardware takes the boot ROM's compatibility path
    // instead, which leaves the title checksum in B (first-party titles only), 0x08 in E, and HL pointing where the
    // palette/logo work ended — 0x991A for the copy-logo checksums, 0x007C otherwise.
    private void SeedPostBootState(ConsoleModel model, CartridgeHeader header) {
        switch (model.Family()) {
            case ConsoleFamily.Cgb:
            case ConsoleFamily.Agb:
            case ConsoleFamily.Ags:
                SeedColorHandoff(header: header);

                break;
            case ConsoleFamily.Sgb:
            case ConsoleFamily.Sgb2:
                SeedSuperHandoff(model: model);

                break;
            default:
                SeedMonochromeHandoff(model: model);

                break;
        }

        // The Advanced boot ROM's extra `inc b`: zero and half-carry reflect the increment, subtract clears, carry is
        // untouched (both Color handoff paths leave it clear).
        if (model.HasAgbBootHandoff()) {
            var incremented = ((byte)(m_b + 1));

            m_f = ((byte)(((incremented == 0x00)
                ? 0x80
                : 0x00) | (((m_b & 0x0F) == 0x0F)
                ? 0x20
                : 0x00)));
            m_b = incremented;
        }

        m_stackPointer = 0xFFFE;
        m_programCounter = 0x0100;
    }
    private void SeedColorHandoff(CartridgeHeader header) {
        m_a = 0x11;
        m_f = 0x80;
        m_c = 0x00;

        if (header.SupportsColor) {
            m_b = 0x00;
            m_d = 0xFF;
            m_e = 0x56;
            m_h = 0x00;
            m_l = 0x0D;

            return;
        }

        var checksum = (header.IsFirstPartyGame
            ? header.TitleChecksum
            : (byte)0x00);
        var copyLogo = ((checksum == 0x43) || (checksum == 0x58));

        m_b = checksum;
        m_d = 0x00;
        m_e = 0x08;
        m_h = (copyLogo
            ? (byte)0x99
            : (byte)0x00);
        m_l = (copyLogo
            ? (byte)0x1A
            : (byte)0x7C);
    }
    private void SeedMonochromeHandoff(ConsoleModel model) {
        if (model == ConsoleModel.Dmg0) {
            m_a = 0x01;
            m_f = 0x00;
            m_b = 0xFF;
            m_c = 0x13;
            m_d = 0x00;
            m_e = 0xC1;
            m_h = 0x84;
            m_l = 0x03;

            return;
        }

        m_a = ((model == ConsoleModel.Mgb)
            ? (byte)0xFF
            : (byte)0x01);
        m_f = 0xB0;
        m_b = 0x00;
        m_c = 0x13;
        m_d = 0x00;
        m_e = 0xD8;
        m_h = 0x01;
        m_l = 0x4D;
    }
    private void SeedSuperHandoff(ConsoleModel model) {
        m_a = ((model == ConsoleModel.Sgb2)
            ? (byte)0xFF
            : (byte)0x01);
        m_f = 0x00;
        m_b = 0x00;
        m_c = 0x14;
        m_d = 0x00;
        m_e = 0x00;
        m_h = 0xC0;
        m_l = 0x60;
    }
    private void ServiceInterrupt() {
        // Dispatch costs five machine cycles: two internal, the two-byte push of PC, and the jump to the vector. The
        // vector is decided late: the enable mask is committed after the high push and the request mask after the low
        // push, so a push that lands on IE or IF participates in the decision — and a write during the pushes that
        // clears the last eligible line CANCELS the dispatch: nothing is acknowledged, IME stays cleared, and execution
        // falls into 0x0000.
        m_interruptMasterEnable = false;

        InternalCycle();

        // The implicit SP move behind this dispatch's two-byte push reports once, against SP's value before either
        // decrement — matching the single register-bump trigger PUSH/CALL/RST also fire, not one per byte. It lands
        // between the two internal cycles, not after both: the second one is a plain delay ahead of the first push,
        // while this report belongs to the machine cycle immediately before it, mirroring PushWord's own ordering.
        NoteOamCorruption(preValue: m_stackPointer);
        InternalCycle();
        PushStackByte(value: ((byte)(m_programCounter >> 8)));

        FlushBusCycles();

        var enabled = m_interrupts.Enabled;

        PushStackByte(value: ((byte)m_programCounter));

        FlushBusCycles();

        var pending = ((InterruptKind)(((byte)m_interrupts.Requested) & ((byte)enabled) & 0x1F));

        if (pending == InterruptKind.None) {
            m_programCounter = 0x0000;
        } else {
            var kind = ((InterruptKind)(((byte)pending) & ((byte)(-((sbyte)pending))))); // the lowest set line = highest priority

            m_interrupts.Acknowledge(kind: kind);

            m_programCounter = kind switch {
                InterruptKind.VBlank => 0x0040,
                InterruptKind.LcdStatus => 0x0048,
                InterruptKind.Timer => 0x0050,
                InterruptKind.Serial => 0x0058,
                _ => 0x0060,
            };
        }

        InternalCycle();
    }
    private void AdvanceInterruptEnable() {
        if (m_interruptEnableCountdown > 0) {
            if (--m_interruptEnableCountdown == 0) {
                m_interruptMasterEnable = true;
            }
        }
    }
}
