// The PPU's frame loop is ares' cothread main() expressed as a C# iterator (one yield per dot); the IO routing is a
// large address switch. Neither maps usefully onto the complexity/maintainability analyzers.
#pragma warning disable CA1502 // Avoid excessive complexity
#pragma warning disable CA1505 // Avoid unmaintainable code
#pragma warning disable CA1506 // Avoid excessive class coupling

using System.Collections;

namespace Puck.HumbleGamingBrick.Ares;

/// <summary>
/// The Game Boy PPU, ported faithfully from ares (<c>gb/ppu</c>). ares runs the PPU as a cothread whose
/// <c>main()</c> walks one scanline of mode 2 → mode 3 → mode 0 (or a vblank line) as straight-line code punctuated
/// by <c>step()</c> calls; here it runs as a real scheduler thread (<c>Main</c>) whose <c>step</c> advances one dot
/// and hands control to the CPU. Mode 3 renders one pixel per dot, reading SCX/SCY/LCDC/BGP/WX/WY and the
/// tile/sprite data fresh each pixel — which is what makes mid-mode-3 register writes (mealybug) land on the exact
/// pixel the hardware shows. This is the DMG renderer; the CGB path is deferred.
/// </summary>
public sealed partial class AresPpu : IAresIo {
    private const int ScreenWidth = 160;
    private const int ScreenHeight = 144;
    private const int RenderWarmupDots = 21;

    private static readonly uint[] Shades = [0xFFFFFFFFu, 0xFFAAAAAAu, 0xFF555555u, 0xFF000000u];

    private readonly bool m_color;
    private readonly byte[] m_vram;
    private readonly byte[] m_oam;
    private readonly byte[] m_bgp = new byte[4];
    private readonly byte[] m_obp = new byte[8];
    private readonly uint[] m_framebuffer = new uint[ScreenWidth * ScreenHeight];

    private AresCpu? m_cpu;
    private AresBus? m_bus;
    private IEnumerator? m_runner;
    private ulong m_clock;
    private bool m_restartPending;
    private bool m_frameReady;

    // Status (ares PPU::Status), flattened.
    private bool m_irq;
    private int m_lx;
    private bool m_bgEnable;
    private bool m_obEnable;
    private bool m_obSize;
    private bool m_bgTilemapSelect;
    private bool m_bgTiledataSelect;
    private bool m_windowDisplayEnable;
    private bool m_windowTilemapSelect;
    private bool m_displayEnable;
    private int m_mode;
    private bool m_interruptHblank;
    private bool m_interruptVblank;
    private bool m_interruptOam;
    private bool m_interruptLyc;
    private byte m_scy;
    private byte m_scx;
    private byte m_ly;
    private byte m_lyc;
    private byte m_dmaBank;
    private bool m_dmaActive;
    private int m_dmaClock;
    private byte m_wy;
    private byte m_wx;

    // Latch (ares PPU::Latch).
    private bool m_latchDisplayEnable;
    private bool m_latchWindowDisplayEnable;
    private byte m_latchWx;
    private int m_latchWy;

    // History: 5 x 2-bit packed mode trail (ares PPU::History).
    private int m_history;

    // Per-line sprite table (ares PPU::Sprite[10]).
    private readonly int[] m_spriteX = new int[10];
    private readonly int[] m_spriteY = new int[10];
    private readonly byte[] m_spriteTile = new byte[10];
    private readonly byte[] m_spriteAttributes = new byte[10];
    private readonly ushort[] m_spriteTiledata = new ushort[10];
    private int m_sprites;

    private int m_px;

    // Background/window fetched tile rows (ares PPU::Background background, window).
    private ushort m_backgroundTiledata;
    private ushort m_windowTiledata;

    // Composited pixel state for the current dot (ares PPU::Pixel bg, ob).
    private int m_bgColor;
    private int m_bgPalette;
    private int m_obColor;
    private int m_obPalette;
    private bool m_obPriority;

    /// <summary>Creates the PPU for the given model and seeds its post-boot state (ares PPU::power).</summary>
    /// <param name="color">Whether the machine is a Game Boy Color (CGB rendering is deferred).</param>
    public AresPpu(bool color) {
        m_color = color;
        m_vram = new byte[color ? 0x4000 : 0x2000];
        m_oam = new byte[160];

        for (var i = 0; i < m_obp.Length; i += 1) {
            m_obp[i] = 3;
        }

        m_runner = Run();
    }

    /// <summary>The 160x144 framebuffer in 0xAARRGGBB shade values (matching the conformance references).</summary>
    public ReadOnlySpan<uint> Framebuffer => m_framebuffer;

    /// <summary>Wires the PPU to the CPU (for IRQs and H-blank) and the bus (for OAM DMA).</summary>
    public void Connect(AresCpu cpu, AresBus bus) {
        ArgumentNullException.ThrowIfNull(argument: cpu);
        ArgumentNullException.ThrowIfNull(argument: bus);

        m_cpu = cpu;
        m_bus = bus;
    }

    /// <summary>Advances the PPU until its dot clock reaches the CPU's clock (ares synchronize, single-threaded).</summary>
    public void AdvanceTo(ulong cpuClock) {
        while (m_clock < cpuClock) {
            if (m_restartPending) {
                m_restartPending = false;
                m_runner = Run();
            }

            m_runner!.MoveNext();
        }
    }

    /// <summary>Returns and clears the "a frame completed" flag.</summary>
    public bool ConsumeFrameReady() {
        var ready = m_frameReady;

        m_frameReady = false;

        return ready;
    }

    /// <summary>The current scanline (LY); used by the CPU to gate HDMA.</summary>
    public int Line => m_ly;

    // ares PPU::main(), as a C# iterator yielding once per dot (the cothread expressed as a single-threaded coroutine).
    private IEnumerator Run() {
        while (true) {
            if (!m_displayEnable || m_cpu!.IsStopped) {
                foreach (var c in StepDots(clocks: 456 * 154)) {
                    yield return c;
                }

                SignalFrame();

                continue;
            }

            m_lx = 0;

            if (m_ly == 0) {
                m_latchWy = 0;
            }

            if (m_latchDisplayEnable && (m_ly == 0)) {
                Mode(mode: 0);

                foreach (var c in StepDots(clocks: 72)) {
                    yield return c;
                }

                Mode(mode: 3);

                foreach (var c in StepDots(clocks: 172)) {
                    yield return c;
                }

                Mode(mode: 0);

                foreach (var c in StepDots(clocks: 456 - 8 - m_lx)) {
                    yield return c;
                }
            }
            else if (m_ly <= 143) {
                Mode(mode: 2);
                ScanlineDmg();

                foreach (var c in StepDots(clocks: 80)) {
                    yield return c;
                }

                m_latchWindowDisplayEnable = m_windowDisplayEnable;
                m_latchWx = m_wx;

                if ((m_ly >= m_wy) && (m_wx < 7)) {
                    m_latchWy += 1;
                }

                Mode(mode: 3);

                // Mode-3 render warmup: pixels begin emerging only after the BG fetch/FIFO fill, so a mid-mode-3
                // register write lands ~21 dots later than the dot it was issued on. ares' fixed model omits this;
                // it is required here to place mealybug's mid-line writes on the hardware-correct pixel.
                foreach (var c in StepDots(clocks: RenderWarmupDots)) {
                    yield return c;
                }

                for (var n = 0; n < 160; n += 1) {
                    RunDmg();

                    foreach (var c in StepDots(clocks: 1)) {
                        yield return c;
                    }
                }

                foreach (var c in StepDots(clocks: 12)) {
                    yield return c;
                }

                Mode(mode: 0);

                foreach (var c in StepDots(clocks: 456 - m_lx)) {
                    yield return c;
                }
            }
            else {
                Mode(mode: 1);

                foreach (var c in StepDots(clocks: 456)) {
                    yield return c;
                }
            }

            m_ly += 1;

            if (m_ly == 144) {
                m_cpu!.Raise(interrupt: AresInterrupt.VerticalBlank);
                SignalFrame();
                m_latchDisplayEnable = false;
            }

            if (m_ly == 154) {
                m_ly = 0;
            }
        }
    }

    // ares PPU::step(): per-dot history/STAT/OAM-DMA/lx bookkeeping, advancing the dot clock and yielding to the CPU.
    private IEnumerable<int> StepDots(int clocks) {
        while (clocks-- > 0) {
            m_history = ((m_history << 2) | m_mode) & 0x3FF;
            Stat();

            if (m_dmaActive) {
                DmaTick();
            }

            m_lx = ((m_lx + 1) & 0x1FF);
            m_clock += 1;

            yield return 0;
        }
    }

    private void DmaTick() {
        var hi = m_dmaClock++;
        var doubleSpeed = (m_cpu!.SpeedDouble != 0);
        var lo = (hi & (doubleSpeed ? 1 : 3));

        hi >>= (doubleSpeed ? 1 : 2);

        if (lo != 0) {
            return;
        }

        if (hi == 0) {
            return; // warm-up
        }

        if (hi == 161) {
            m_dmaActive = false; // cool-down

            return;
        }

        var bank = m_dmaBank;

        if (bank == 0xFE) {
            bank = 0xDE; // OAM DMA cannot reference OAM/IO/HRAM; it reads from the mirror.
        }

        if (bank == 0xFF) {
            bank = 0xDF;
        }

        m_oam[hi - 1] = m_bus!.Read(address: (ushort)((bank << 8) | (hi - 1)), data: 0xFF);
    }

    private void Mode(int mode) {
        if (mode == 0) {
            m_cpu!.HblankIn();
        }
        else {
            m_cpu!.HblankOut();
        }

        m_mode = mode;
    }

    private void Stat() {
        if (!m_displayEnable) {
            return;
        }

        var irq = m_irq;

        m_irq = (m_interruptHblank && (m_mode == 0))
            || (m_interruptVblank && (m_mode == 1))
            || (m_interruptOam && TriggerOam())
            || (m_interruptLyc && CompareLyc());

        if (!irq && m_irq) {
            m_cpu!.Raise(interrupt: AresInterrupt.Stat);
        }
    }

    private void SignalFrame() =>
        m_frameReady = true;

    // === Timing (ares timing.cpp, DMG paths) ===

    private bool CanAccessVram() {
        if (!m_displayEnable) {
            return true;
        }

        var trailingMode = ((m_history >> 4) & 3);

        if (trailingMode == 3) {
            return false;
        }

        if ((trailingMode == 2) && ((m_lx >> 2) == 20)) {
            return false;
        }

        return true;
    }

    private bool CanAccessOam() {
        if (!m_displayEnable) {
            return true;
        }

        if (m_dmaActive && (m_dmaClock >= 8)) {
            return false;
        }

        var trailingMode = ((m_history >> 4) & 3);

        if (trailingMode == 2) {
            return false;
        }

        if (trailingMode == 3) {
            return false;
        }

        if ((m_ly != 0) && ((m_lx >> 2) == 0)) {
            return false;
        }

        return true;
    }

    private bool CompareLyc() {
        int ly = m_ly;
        int lyc = m_lyc;
        var lx = (m_lx >> 2);

        if ((ly != 0) && (lx == 0)) {
            return false;
        }

        if ((ly == 153) && (lx == 2)) {
            return false;
        }

        if ((ly == 153) && (lx >= 3)) {
            return (lyc == 0);
        }

        return (lyc == ly);
    }

    private byte GetLy() {
        var lx = (m_lx >> 2);

        if ((m_ly == 153) && (lx >= 1)) {
            return 0;
        }

        return m_ly;
    }

    private bool TriggerOam() {
        if (m_mode != 2) {
            return false;
        }

        var lx = (m_lx >> (m_cpu!.SpeedDouble != 0 ? 1 : 2));

        return (lx == (m_ly == 0 ? 1 : 0));
    }
}
