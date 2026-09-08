using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Writes Puck's own co-simulation trace — the mirror of SameBoy's <c>sb-trace events</c> output — by implementing
/// both core trace-sink interfaces (<see cref="ICpuTraceSink"/>, <see cref="IPpuTraceSink"/>) and stamping every
/// record with <see cref="MasterClock.CycleCount"/> at the instant the core calls in, which is exact because each
/// callback fires synchronously before the clock advances any further.
/// <para>
/// PCM12/PCM34 have no seam of their own (<c>Puck.HumbleGamingBrick</c> carries a trace seam only in <c>Ppu.cs</c> and
/// <c>Sm83.cs</c>), so a PCM-kind run polls <see cref="IApu.ReadPcm"/> from inside the CPU instruction-boundary
/// callback instead of every T-cycle — coarser than SameBoy's per-<c>GB_run()</c> sampling, but the frame sequencer
/// that drives PCM change is far slower than the CPU's instruction rate, so no distinct level is lost in practice.
/// </para>
/// </summary>
internal sealed class CosimTraceSink : ICpuTraceSink, IPpuTraceSink, IDisposable {
    private readonly IApu? m_apu;
    private readonly MasterClock m_clock;
    private readonly BinaryWriter m_writer;
    private readonly bool m_wantCpu;
    private readonly bool m_wantPcm;
    private readonly bool m_wantPpu;

    private bool m_firstPcm = true;
    private byte m_lastPcm12 = 0xFF;
    private byte m_lastPcm34 = 0xFF;

    /// <summary>Creates a sink writing to <paramref name="output"/>, filtered to the requested kinds.</summary>
    /// <param name="output">The stream to write records to. The sink owns and disposes it.</param>
    /// <param name="clock">The machine's master clock, read for every record's cycle stamp.</param>
    /// <param name="wantCpu">Whether CPU instruction-boundary records are written.</param>
    /// <param name="wantPpu">Whether PPU mode-transition and pixel-pop records are written.</param>
    /// <param name="wantPcm">Whether PCM12/PCM34 sample records are written.</param>
    /// <param name="apu">The APU to poll for PCM samples; required when <paramref name="wantPcm"/> is set.</param>
    public CosimTraceSink(Stream output, MasterClock clock, bool wantCpu, bool wantPpu, bool wantPcm, IApu? apu) {
        m_apu = apu;
        m_clock = clock;
        m_wantCpu = wantCpu;
        m_wantPcm = wantPcm;
        m_wantPpu = wantPpu;
        m_writer = new BinaryWriter(output: output);
    }

    /// <summary>Gets the number of records written so far.</summary>
    public long RecordCount { get; private set; }

    /// <inheritdoc/>
    public void OnInstructionBoundary(ushort pc, byte a, byte f, byte b, byte c, byte d, byte e, byte h, byte l, ushort sp) {
        if (m_wantCpu) {
            new CosimEvent {
                A = a,
                B = b,
                C = c,
                Cycle = m_clock.CycleCount,
                D = d,
                E = e,
                F = f,
                H = h,
                Kind = CosimEventKind.Cpu,
                L = l,
                Pc = pc,
                Sp = sp,
            }.WriteTo(writer: m_writer);

            ++RecordCount;
        }

        if (
            m_wantPcm &&
            (m_apu is not null)
        ) {
            var pcm12 = m_apu.ReadPcm(address: MemoryMap.PcmAmplitude12);
            var pcm34 = m_apu.ReadPcm(address: MemoryMap.PcmAmplitude34);

            if (
                m_firstPcm ||
                (pcm12 != m_lastPcm12) ||
                (pcm34 != m_lastPcm34)
            ) {
                new CosimEvent {
                    Cycle = m_clock.CycleCount,
                    Kind = CosimEventKind.Pcm,
                    Pcm12 = pcm12,
                    Pcm34 = pcm34,
                }.WriteTo(writer: m_writer);

                m_firstPcm = false;
                m_lastPcm12 = pcm12;
                m_lastPcm34 = pcm34;
                ++RecordCount;
            }
        }
    }
    /// <inheritdoc/>
    public void OnModeTransition(byte ly, int mode) {
        if (!m_wantPpu) {
            return;
        }

        new CosimEvent {
            Cycle = m_clock.CycleCount,
            Kind = CosimEventKind.PpuMode,
            Ly = ly,
            Mode = mode,
        }.WriteTo(writer: m_writer);

        ++RecordCount;
    }
    /// <inheritdoc/>
    public void OnPixelPop(byte ly, int x, uint color) {
        if (!m_wantPpu) {
            return;
        }

        new CosimEvent {
            Color = color,
            Cycle = m_clock.CycleCount,
            Kind = CosimEventKind.PpuPixel,
            Ly = ly,
            X = x,
        }.WriteTo(writer: m_writer);

        ++RecordCount;
    }
    /// <inheritdoc/>
    public void Dispose() {
        m_writer.Flush();
        m_writer.Dispose();
    }
}
