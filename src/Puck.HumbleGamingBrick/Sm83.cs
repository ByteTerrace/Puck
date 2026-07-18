using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

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
    private readonly IHdma m_hdma;
    private readonly IInterruptController m_interrupts;
    private readonly IJoypad m_joypad;
    private readonly IKey1 m_key1;
    // Mutable so a LIVE device swap re-gates the only live model read (ExecuteStop: color arms a KEY1 speed switch,
    // monochrome halts). The boot register handoff (SeedPostBootState, incl. the AGB inc-b probe) stays construction-only.
    private bool m_supportsColor;
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
    public Sm83(ISystemBus bus, IInterruptController interrupts, ComponentClock componentClock, IKey1 key1, IHdma hdma, IJoypad joypad, MachineConfiguration configuration, CartridgeHeader header) {
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
        m_supportsColor = configuration.Model.SupportsColor();

        // With a boot ROM the CPU powers on cold — every register zero and PC at 0x0000, the overlay's reset vector —
        // and the boot program itself produces the handoff state. Without one, the documented handoff is seeded.
        if (configuration.BootRom is null) {
            SeedPostBootState(model: configuration.Model, header: header);
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
        set => m_f = (byte)(value & 0xF0);
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
        get => (ushort)((m_a << 8) | m_f);
        set {
            m_a = (byte)(value >> 8);
            m_f = (byte)(value & 0xF0);
        }
    }
    private ushort Bc {
        get => (ushort)((m_b << 8) | m_c);
        set {
            m_b = (byte)(value >> 8);
            m_c = (byte)value;
        }
    }
    private ushort De {
        get => (ushort)((m_d << 8) | m_e);
        set {
            m_d = (byte)(value >> 8);
            m_e = (byte)value;
        }
    }
    private ushort Hl {
        get => (ushort)((m_h << 8) | m_l);
        set {
            m_h = (byte)(value >> 8);
            m_l = (byte)value;
        }
    }

    /// <inheritdoc/>
    public void StepInstruction() {
        // M-06: the debug watchpoint PC witness. A cheap unconditional field write on the bus side (SystemBus.cs); the
        // bus itself decides whether anything downstream cares (a watch hit latches this, otherwise it is never read).
        m_bus.NoteInstructionStart(pc: m_programCounter);

        if (m_lockedUp) {
            // An illegal opcode wedges the CPU like real hardware: it fetches nothing and never advances, but time keeps
            // flowing — advance exactly one machine cycle so the PPU, timer, and the rest keep running and the screen
            // keeps refreshing. Interrupts cannot break the lock; only a reset (a fresh machine) clears it.
            InternalCycle();

            return;
        }

        // The speed-switch stall runs one machine cycle at a time — STOP armed it, KEY1's countdowns run it — so the
        // machine stays steppable at instruction granularity through the whole re-gear. The clock is re-synced each
        // cycle (the speed flips two machine cycles in), and a pending interrupt aborts the stall like a halt wake once
        // the switch's interrupt-block window has closed.
        if (m_key1.IsSwitching) {
            InternalCycle();

            m_componentClock.IsDoubleSpeed = m_key1.IsDoubleSpeed;

            if (!m_key1.AreInterruptsBlocked && (m_interrupts.Pending != InterruptKind.None)) {
                m_key1.CancelSwitch();
            }

            return;
        }

        // Stop mode parks the CPU until a button is held; the clock keeps running so the (blanked) PPU stays live.
        if (m_key1.IsStopped) {
            if (m_joypad.AnyButtonHeld) {
                m_key1.LeaveStop();
            } else {
                InternalCycle();

                return;
            }
        }

        if (m_halted) {
            if (m_interrupts.Pending != InterruptKind.None) {
                m_halted = false;

                m_hdma.OnCpuWoke();
            } else {
                InternalCycle();

                return;
            }
        }

        // A newly armed VRAM DMA transfer loses the race to a pending interrupt: the hardware only freezes the CPU at
        // its next fetch, so a dispatch already due runs to completion first. Once the unit owns the bus the roles flip
        // and dispatch waits for the transfer to finish.
        if (m_interruptMasterEnable && !m_key1.AreInterruptsBlocked && (m_interrupts.Pending != InterruptKind.None) && !m_hdma.IsTransferLocked) {
            ServiceInterrupt();

            return;
        }

        // A VRAM DMA transfer freezes the CPU while it owns the bus: no fetch, just time. The acknowledge tells the
        // unit the CPU has yielded; its start-up chain is measured from here.
        if (m_hdma.IsCpuStalled) {
            m_hdma.AcknowledgeStall();
            InternalCycle();

            return;
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
        AdvanceInterruptEnable();
    }
    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) =>
        m_supportsColor = model.SupportsColor();

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

    // The boot ROM's documented register handoff, which differs per model. The CGB leaves A = 0x11, the value ROMs test
    // to detect Color hardware; the flags follow the standard post-boot values. A monochrome cartridge on Color hardware
    // takes the boot ROM's compatibility path, which leaves the title checksum in B (first-party titles only), 0x08 in E,
    // and HL pointing where the palette/logo work ended — 0x991A for the copy-logo checksums, 0x007C otherwise. The AGB
    // boot ROM hands off the CGB state after one extra `inc b` — the single register difference cartridges probe to
    // detect Advance hardware — so B is one higher and F carries the increment's flags.
    private void SeedPostBootState(ConsoleModel model, CartridgeHeader header) {
        if (model.SupportsColor() && !header.SupportsColor) {
            var checksum = (header.IsFirstPartyGame ? header.TitleChecksum : (byte)0x00);
            var copyLogo = ((checksum == 0x43) || (checksum == 0x58));

            m_a = 0x11;
            m_f = 0x80;
            m_b = checksum;
            m_c = 0x00;
            m_d = 0x00;
            m_e = 0x08;
            m_h = (copyLogo ? (byte)0x99 : (byte)0x00);
            m_l = (copyLogo ? (byte)0x1A : (byte)0x7C);
        } else if (model.SupportsColor()) {
            m_a = 0x11;
            m_f = 0x80;
            m_b = 0x00;
            m_c = 0x00;
            m_d = 0xFF;
            m_e = 0x56;
            m_h = 0x00;
            m_l = 0x0D;
        } else {
            m_a = 0x01;
            m_f = 0xB0;
            m_b = 0x00;
            m_c = 0x13;
            m_d = 0x00;
            m_e = 0xD8;
            m_h = 0x01;
            m_l = 0x4D;
        }

        // The AGB's extra `inc b`: zero and half-carry reflect the increment, subtract clears, carry is untouched
        // (both CGB handoff paths leave it clear).
        if (model == ConsoleModel.Agb) {
            var incremented = (byte)(m_b + 1);

            m_f = (byte)(((incremented == 0x00) ? 0x80 : 0x00) | (((m_b & 0x0F) == 0x0F) ? 0x20 : 0x00));
            m_b = incremented;
        }

        m_stackPointer = 0xFFFE;
        m_programCounter = 0x0100;
    }
    private void ServiceInterrupt() {
        // Dispatch costs five machine cycles: two internal, the two-byte push of PC, and the jump to the vector. The
        // vector is decided late: the enable mask is committed after the high push and the request mask after the low
        // push, so a push that lands on IE or IF participates in the decision — and a write during the pushes that
        // clears the last eligible line CANCELS the dispatch: nothing is acknowledged, IME stays cleared, and execution
        // falls into 0x0000.
        m_interruptMasterEnable = false;

        InternalCycle();
        InternalCycle();

        m_stackPointer = (ushort)(m_stackPointer - 1);
        WriteCycle(address: m_stackPointer, value: (byte)(m_programCounter >> 8));

        var enabled = m_interrupts.Enabled;

        m_stackPointer = (ushort)(m_stackPointer - 1);
        WriteCycle(address: m_stackPointer, value: (byte)m_programCounter);

        var pending = (InterruptKind)((byte)m_interrupts.Requested & (byte)enabled & 0x1F);

        if (pending == InterruptKind.None) {
            m_programCounter = 0x0000;
        } else {
            var kind = (InterruptKind)((byte)pending & (byte)(-(sbyte)pending)); // the lowest set line = highest priority

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
