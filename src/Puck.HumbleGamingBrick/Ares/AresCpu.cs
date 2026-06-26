using System.Numerics;

namespace Puck.HumbleGamingBrick.Ares;

/// <summary>The five Game Boy interrupt sources, in priority order (lowest bit = highest priority).</summary>
public enum AresInterrupt {
    /// <summary>Vertical blank (IF/IE bit 0).</summary>
    VerticalBlank = 0,

    /// <summary>LCD STAT (IF/IE bit 1).</summary>
    Stat = 1,

    /// <summary>Timer overflow (IF/IE bit 2).</summary>
    Timer = 2,

    /// <summary>Serial transfer complete (IF/IE bit 3).</summary>
    Serial = 3,

    /// <summary>Joypad (IF/IE bit 4).</summary>
    Joypad = 4,
}

/// <summary>
/// The Game Boy CPU, ported faithfully from ares (<c>gb/cpu</c>). It supplies the Sharp SM83 core with its
/// cycle-addressed memory access (<see cref="Read"/>/<see cref="Write"/>/<see cref="Idle"/> — five bus sub-cycles
/// with a <see cref="Step"/> between each), drives DIV/timer/serial/joypad one clock at a time inside that step,
/// performs the interrupt dispatch with the IE-poll-between-pushes quirk, and owns work RAM, HRAM, the timer/serial/
/// joypad/interrupt registers, KEY1 speed switching, and HDMA. Each clock advances <see cref="Clock"/> and drives
/// the PPU to catch up — ares' cothread synchronize, reduced to a single-threaded coroutine driver (equal clocks).
/// </summary>
public sealed partial class AresCpu : AresSm83, IAresIo {
    private readonly bool m_color;
    private readonly byte[] m_wram;
    private readonly byte[] m_hram;

    private AresBus? m_bus;

    /// <summary>The CPU clock in elapsed clocks; the PPU is driven to catch up to this (ares synchronize, reduced to
    /// a single-threaded coroutine driver since CPU and PPU share one frequency).</summary>
    internal ulong Clock;

    // Status (ares CPU::Status).
    private byte m_interruptLatch;
    private bool m_hblank;
    private bool m_hdmaPending;
    private int m_timerLine;
    private byte m_joyp = 0x0F;
    private bool m_p14;
    private bool m_p15;
    private byte m_serialData;
    private int m_serialBits;
    private bool m_serialClock;
    private bool m_serialSpeed;
    private bool m_serialTransfer;
    private ushort m_div;
    private byte m_tima;
    private byte m_tma;
    private int m_timerClock;
    private bool m_timerEnable;
    private byte m_interruptFlag;
    private byte m_interruptEnable;
    private bool m_cgbMode;
    private bool m_speedSwitch;
    private int m_speedDouble;
    private ushort m_dmaSource;
    private ushort m_dmaTarget;
    private int m_dmaLength;
    private bool m_hdmaActive;
    private int m_wramBank = 1;

    // Live joypad state (active-low: a set bit means released). Default = nothing pressed.
    private byte m_joypButtons = 0x0F;
    private byte m_joypDpad = 0x0F;

    /// <summary>Creates the CPU for the given model and seeds its post-boot state.</summary>
    /// <param name="color">Whether the machine is a Game Boy Color.</param>
    public AresCpu(bool color) {
        m_color = color;
        m_wram = new byte[color ? 0x8000 : 0x2000];
        m_hram = new byte[0x80];
        m_div = 8; // ares power(): post-boot DIV for DMG ABC/mgb.
        m_cgbMode = color;
    }

    /// <summary>Returns the current PPU scanline (LY); used to gate HDMA. Set during machine assembly.</summary>
    public Func<int>? PpuLine { get; set; }

    /// <summary>Advances the PPU to the CPU's current clock; set during machine assembly.</summary>
    public Action<ulong>? DrivePpu { get; set; }

    /// <summary>Connects the CPU to the bus once every component is constructed.</summary>
    public void Connect(AresBus bus) {
        ArgumentNullException.ThrowIfNull(argument: bus);

        m_bus = bus;
    }

    /// <summary>The double-speed flag (CGB); 0 = normal, 1 = double.</summary>
    public int SpeedDouble => m_speedDouble;

    /// <summary>Whether the CPU is in STOP (the PPU blanks the screen while stopped).</summary>
    public bool IsStopped => RegisterStop;

    /// <summary>The current program counter (for terminal-condition detection in the conformance harness).</summary>
    public ushort ProgramCounter => PC;

    /// <summary>Seeds the DMG post-boot CPU register and timer/interrupt state (boot ROM not run).</summary>
    public void SeedPostBootDmg() {
        AF = 0x01B0;
        BC = 0x0013;
        DE = 0x00D8;
        HL = 0x014D;
        SP = 0xFFFE;
        PC = 0x0100;
        RegisterIme = false;
        m_interruptFlag = 0x01;
        m_div = 8;
    }

    /// <summary>Executes one CPU step: HDMA if pending, interrupt dispatch, then one instruction (ares CPU::main).</summary>
    public void Main() {
        if (((PpuLine?.Invoke() ?? 0) < 144) && m_hdmaPending) {
            PerformHdma();
            m_hdmaPending = false;
        }

        if (RegisterIme && (m_interruptLatch != 0)) {
            Idle();
            Idle();
            Idle();
            RegisterIme = false;
            Write(address: --SP, data: (byte)(PC >> 8)); // upper byte may write to IE before it is polled again
            var mask = (byte)(m_interruptFlag & m_interruptEnable);

            Write(address: --SP, data: (byte)PC); // lower byte write to IE has no effect

            if (mask != 0) {
                var interruptId = BitOperations.TrailingZeroCount(value: (uint)mask);

                Lower(interruptId: interruptId);
                PC = (ushort)(0x0040 + (interruptId * 8));
            }
            else {
                PC = 0x0000;
            }
        }

        Instruction();
    }

    /// <summary>Returns whether the given interrupt's request flag is set.</summary>
    public bool Raised(int interruptId) =>
        ((m_interruptFlag >> interruptId) & 1) != 0;

    /// <summary>Raises an interrupt; wakes HALT (and STOP for joypad) when the corresponding enable is set.</summary>
    public void Raise(AresInterrupt interrupt) {
        var interruptId = (int)interrupt;

        m_interruptFlag |= (byte)(1 << interruptId);

        if (((m_interruptEnable >> interruptId) & 1) != 0) {
            RegisterHalt = false;

            if (interrupt == AresInterrupt.Joypad) {
                RegisterStop = false;
            }
        }
    }

    private void Lower(int interruptId) =>
        m_interruptFlag &= (byte)~(1 << interruptId);

    protected override bool Stoppable() {
        m_div = 0;

        if (m_speedSwitch) {
            m_speedSwitch = false;
            m_speedDouble ^= 1;

            return false;
        }

        return true;
    }

    // === Timing (ares timing.cpp) ===

    private void Step() =>
        Step(clocks: 1);

    private void Step(int clocks) {
        var timerBit = 9;

        if (m_timerClock != 0) {
            timerBit = ((m_timerClock * 2) + 1);
        }

        for (var n = 0; n < clocks; n += 1) {
            if (!RegisterStop) {
                m_div += 1;
            }

            var timerLineNew = (((m_div >> timerBit) & 1) != 0) && m_timerEnable ? 1 : 0;

            if ((timerLineNew == 0) && (m_timerLine == 1)) {
                TimerTick();
            }

            if ((m_div & 0x01FF) == 0) {
                Timer8192Hz();
            }

            if ((m_div & 0x0FFF) == 0) {
                Timer1024Hz();
            }

            m_timerLine = timerLineNew;

            Clock += 1;
            DrivePpu!(obj: Clock);
        }
    }

    private void TimerTick() {
        m_tima += 1;

        if (m_tima == 0) {
            m_tima = m_tma;
            Raise(interrupt: AresInterrupt.Timer);
        }
    }

    private void Timer8192Hz() {
        if (m_serialTransfer && m_serialClock) {
            m_serialData = (byte)((m_serialData << 1) | 1);
            SerialByteShifted?.Invoke(obj: m_serialData);

            if (--m_serialBits == 0) {
                m_serialTransfer = false;
                Raise(interrupt: AresInterrupt.Serial);
            }
        }
    }

    private void Timer1024Hz() =>
        JoypPoll();

    /// <summary>Invoked when a serial bit completes; carries the running SB shift register (mooneye link-cable use).</summary>
    public Action<byte>? SerialByteShifted { get; set; }

    /// <summary>Called by the PPU when it enters H-blank; arms HDMA.</summary>
    public void HblankIn() {
        HdmaTrigger(hblank: true, active: m_hdmaActive);
        m_hblank = true;
    }

    /// <summary>Called by the PPU when it leaves H-blank.</summary>
    public void HblankOut() =>
        m_hblank = false;

    private void HdmaTrigger(bool hblank, bool active) {
        var previousState = (m_hdmaActive && m_hblank);
        var newState = (active && hblank);

        m_hdmaPending = (!previousState && newState);
    }

    private void PerformHdma() {
        Step(clocks: 4);

        for (var loop = 0; loop < 16; loop += 1) {
            WriteDma(address: m_dmaTarget++, data: ReadDma(address: m_dmaSource++, data: 0xFF));
            Step(clocks: 2 << m_speedDouble);
        }

        if (m_dmaLength-- == 0) {
            m_hdmaActive = false;
        }
    }

    // === Memory access (ares memory.cpp) ===

    protected override void Stop() {
        Idle();

        // Super Game Boy scheduler exit is not modelled.
    }

    protected override void Halt() {
        Idle();

        if (m_interruptLatch != 0) {
            RegisterHalt = false;
        }
    }

    protected override void HaltBugTrigger() {
        if (!RegisterIme && (m_interruptLatch != 0)) {
            RegisterHaltBug = true;
        }
    }

    protected override void Idle() {
        if (RegisterEi) {
            RegisterEi = false;
            RegisterIme = true;
        }

        Step();
        Step();
        m_interruptLatch = (byte)(m_interruptFlag & m_interruptEnable);
        Step();
        Step();
    }

    protected override byte Read(ushort address) {
        var bus = m_bus!;
        var data = (byte)0xFF;

        if (RegisterEi) {
            RegisterEi = false;
            RegisterIme = true;
        }

        data &= bus.Read(cycle: 0, address: address, data: data);
        Step();
        data &= bus.Read(cycle: 1, address: address, data: data);
        Step();
        data &= bus.Read(cycle: 2, address: address, data: data);
        m_interruptLatch = (byte)(m_interruptFlag & m_interruptEnable);
        Step();
        data &= bus.Read(cycle: 3, address: address, data: data);
        Step();
        data &= bus.Read(cycle: 4, address: address, data: data);

        return data;
    }

    protected override void Write(ushort address, byte data) {
        var bus = m_bus!;

        if (RegisterEi) {
            RegisterEi = false;
            RegisterIme = true;
        }

        bus.Write(cycle: 0, address: address, data: data);
        Step();
        bus.Write(cycle: 1, address: address, data: data);
        Step();
        bus.Write(cycle: 2, address: address, data: data);
        Step();
        bus.Write(cycle: 3, address: address, data: data);
        Step();
        bus.Write(cycle: 4, address: address, data: data);
        m_interruptLatch = (byte)(m_interruptFlag & m_interruptEnable);
    }

    // VRAM DMA source can only be ROM or RAM.
    private byte ReadDma(ushort address, byte data) {
        var bus = m_bus!;

        if (address < 0x8000) {
            return bus.Read(address: address, data: data);
        }

        if (address < 0xA000) {
            return data;
        }

        if (address < 0xE000) {
            return bus.Read(address: address, data: data);
        }

        return data;
    }

    // VRAM DMA target is always VRAM.
    private void WriteDma(ushort address, byte data) =>
        m_bus!.Write(address: (ushort)(0x8000 | (address & 0x1FFF)), data: data);
}
