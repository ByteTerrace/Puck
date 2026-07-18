namespace Puck.AdvancedGamingBrick.Post;

// A pass-through IAgbBus decorator that reports every CPU store to a watched address. This is the tracing-bus
// seam the DI composition root was designed for: the CPU is bound to IAgbBus, so wrapping that interface lets
// the harness observe stores without touching the core. It reads the AGS aging cartridge's per-test result
// flags, which the output-results patch writes to address 0x00000004 after each test returns.
internal sealed class TracingAgbBus : IAgbBus {
    private readonly IAgbBus m_inner;
    private readonly uint m_watchAddress;
    private readonly Action<uint> m_onStore;
    private readonly uint m_readWatchAddress;
    private readonly Action<uint>? m_onRead;
    private readonly uint m_readWatchAddress2;
    private readonly Action<uint, uint>? m_onRead2; // (address, value)
    private readonly uint m_readRangeBase;
    private readonly uint m_readRangeEnd;
    private readonly Action<uint, uint>? m_onReadRange; // (address, value) for any hit in [base, end)
    private readonly uint m_writeRangeBase;
    private readonly uint m_writeRangeEnd;
    private readonly Action<uint, uint>? m_onWriteRange;

    public TracingAgbBus(IAgbBus inner, uint watchAddress, Action<uint> onStore, uint readWatchAddress = 0xFFFFFFFFu, Action<uint>? onRead = null, uint readWatchAddress2 = 0xFFFFFFFFu, Action<uint, uint>? onRead2 = null, uint readRangeBase = 0xFFFFFFFFu, uint readRangeEnd = 0u, Action<uint, uint>? onReadRange = null, uint writeRangeBase = 0xFFFFFFFFu, uint writeRangeEnd = 0u, Action<uint, uint>? onWriteRange = null) {
        m_inner = inner;
        m_watchAddress = watchAddress;
        m_onStore = onStore;
        m_readWatchAddress = readWatchAddress;
        m_onRead = onRead;
        m_readWatchAddress2 = readWatchAddress2;
        m_onRead2 = onRead2;
        m_readRangeBase = readRangeBase;
        m_readRangeEnd = readRangeEnd;
        m_onReadRange = onReadRange;
        m_writeRangeBase = writeRangeBase;
        m_writeRangeEnd = writeRangeEnd;
        m_onWriteRange = onWriteRange;
    }

    public bool IrqPending => m_inner.IrqPending;

    public byte Read8(uint address, BusAccessType access) => m_inner.Read8(address: address, access: access);
    public ushort Read16(uint address, BusAccessType access) {
        var value = m_inner.Read16(address: address, access: access);
        var aligned = address & ~1u;

        if ((m_onRead is not null) && (aligned == (m_readWatchAddress & ~1u))) {
            m_onRead(obj: value);
        }

        if ((m_onRead2 is not null) && (aligned == (m_readWatchAddress2 & ~1u))) {
            m_onRead2(arg1: aligned, arg2: value);
        }

        if ((m_onReadRange is not null) && (aligned >= m_readRangeBase) && (aligned < m_readRangeEnd)) {
            m_onReadRange(arg1: aligned, arg2: value);
        }

        return value;
    }
    public uint Read32(uint address, BusAccessType access) => m_inner.Read32(address: address, access: access);
    public ushort ReadCode16(uint address, BusAccessType access) => m_inner.ReadCode16(address: address, access: access);
    public uint ReadCode32(uint address, BusAccessType access) => m_inner.ReadCode32(address: address, access: access);
    public void Write8(uint address, byte value, BusAccessType access) {
        Watch(address: address, value: value);
        m_inner.Write8(address: address, value: value, access: access);
    }
    public void Write16(uint address, ushort value, BusAccessType access) {
        Watch(address: address, value: value);
        WatchWriteRange(address: address, value: value);
        m_inner.Write16(address: address, value: value, access: access);
    }
    public void Write32(uint address, uint value, BusAccessType access) {
        Watch(address: address, value: value);
        WatchWriteRange(address: address & ~3u, value: value);
        m_inner.Write32(address: address, value: value, access: access);
    }
    public void Idle(int cycles) => m_inner.Idle(cycles: cycles);
    public void ProcessEvents() => m_inner.ProcessEvents();

    public bool Halted => m_inner.Halted;

    public void Halt(bool stop) => m_inner.Halt(stop: stop);
    public void RunUntilInterrupt() => m_inner.RunUntilInterrupt();

    private void Watch(uint address, uint value) {
        if ((address & ~0x3u) == (m_watchAddress & ~0x3u)) {
            m_onStore(obj: value);
        }
    }
    private void WatchWriteRange(uint address, uint value) {
        var aligned = address & ~1u;

        if ((m_onWriteRange is not null) && (aligned >= m_writeRangeBase) && (aligned < m_writeRangeEnd)) {
            m_onWriteRange(arg1: aligned, arg2: value);
        }
    }
}
