using System.Text;

namespace Puck.AdvancedGamingBrick.Post;

// An IGbaBus decorator that emulates mGBA's debug-log register block and injects controller input, so the
// menu-driven mGBA test suite (mgba-emu/suite) can be run head-lessly. The suite probes for mGBA by writing
// 0xC0DE to 0x04FFF780 and expecting 0x1DEA back; once "open" it prints each suite's "BEGIN:"/"END: p/t" line
// by formatting into the 0x04FFF600 buffer and writing the level (| 0x100) to 0x04FFF700. KEYINPUT (0x04000130)
// is overridden so the harness can drive the menu.
internal sealed class MgbaDebugBus : IGbaBus {
    private const uint DebugString = 0x04FFF600u;
    private const uint DebugFlags = 0x04FFF700u;
    private const uint DebugEnable = 0x04FFF780u;
    private const uint KeyInput = 0x04000130u;

    private readonly IGbaBus m_inner;
    private readonly byte[] m_string = new byte[0x100];
    private readonly Action<int, string> m_onLog;
    private bool m_enabled;

    public MgbaDebugBus(IGbaBus inner, Action<int, string> onLog) {
        m_inner = inner;
        m_onLog = onLog;
    }

    /// <summary>The KEYINPUT value reads see (active-low: a clear bit is a pressed button). 0x3FF = all released.</summary>
    public ushort Keys { get; set; } = 0x3FF;

    public bool IrqPending => m_inner.IrqPending;
    public bool Halted => m_inner.Halted;

    public void Halt(bool stop) => m_inner.Halt(stop: stop);

    public void RunUntilInterrupt() => m_inner.RunUntilInterrupt();

    public byte Read8(uint address, BusAccessType access) {
        if (TryReadDebug(address: address, width: 1, out var value)) {
            return (byte)value;
        }

        return m_inner.Read8(address: address, access: access);
    }

    public ushort Read16(uint address, BusAccessType access) {
        if (TryReadDebug(address: address, width: 2, out var value)) {
            return (ushort)value;
        }

        return m_inner.Read16(address: address, access: access);
    }

    public uint Read32(uint address, BusAccessType access) {
        if (TryReadDebug(address: address, width: 4, out var value)) {
            return value;
        }

        return m_inner.Read32(address: address, access: access);
    }

    public ushort ReadCode16(uint address, BusAccessType access) => m_inner.ReadCode16(address: address, access: access);

    public uint ReadCode32(uint address, BusAccessType access) => m_inner.ReadCode32(address: address, access: access);

    public void Write8(uint address, byte value, BusAccessType access) {
        if (WriteDebug(address: address, width: 1, value: value)) {
            return;
        }

        m_inner.Write8(address: address, value: value, access: access);
    }

    public void Write16(uint address, ushort value, BusAccessType access) {
        if (WriteDebug(address: address, width: 2, value: value)) {
            return;
        }

        m_inner.Write16(address: address, value: value, access: access);
    }

    public void Write32(uint address, uint value, BusAccessType access) {
        if (WriteDebug(address: address, width: 4, value: value)) {
            return;
        }

        m_inner.Write32(address: address, value: value, access: access);
    }

    public void Idle(int cycles) => m_inner.Idle(cycles: cycles);

    public void ProcessEvents() => m_inner.ProcessEvents();

    private bool TryReadDebug(uint address, int width, out uint value) {
        if (address == DebugEnable) {
            value = m_enabled ? 0x1DEAu : 0u;

            return true;
        }

        // KEYINPUT is overridden so the harness can press menu buttons.
        if ((address & ~1u) == KeyInput) {
            var keys = (uint)Keys;

            value = width switch {
                1 => ((address & 1u) == 0u) ? (keys & 0xFFu) : ((keys >> 8) & 0xFFu),
                _ => keys,
            };

            return true;
        }

        value = 0u;

        return false;
    }

    private bool WriteDebug(uint address, int width, uint value) {
        if (address == DebugEnable) {
            m_enabled = value == 0xC0DEu;

            return true;
        }

        if (address == DebugFlags) {
            if ((value & 0x100u) != 0u) {
                Flush(level: (int)(value & 0x7u));
            }

            return true;
        }

        if ((address >= DebugString) && (address < DebugFlags)) {
            for (var i = 0; i < width; ++i) {
                var index = (int)((address - DebugString) + (uint)i);

                if (index < m_string.Length) {
                    m_string[index] = (byte)(value >> (i * 8));
                }
            }

            return true;
        }

        return false;
    }

    private void Flush(int level) {
        var length = Array.IndexOf(array: m_string, value: (byte)0);

        if (length < 0) {
            length = m_string.Length;
        }

        m_onLog(arg1: level, arg2: Encoding.ASCII.GetString(bytes: m_string, index: 0, count: length));
    }
}
