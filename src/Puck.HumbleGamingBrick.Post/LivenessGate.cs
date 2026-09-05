namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// A shared liveness check for <see cref="ScreenshotProbe"/> and <see cref="AudioProbe"/>: both compare a fixed
/// framebuffer or audio snapshot against an expectation, so a machine that never ran anything can satisfy either
/// probe by accident — a ROM whose entry point is an unconditional self-branch retires instructions forever without
/// ever leaving that one address, leaving the framebuffer at its power-on color and the audio ring at its power-on
/// silence, which happens to be the expected outcome for a "blank screen" or "_outaudio0" case. <see cref="Attach"/>
/// installs a trace sink on the machine's core for the probe's whole run; the returned <see cref="Handle"/> reports
/// the run dead unless the observed program counter left a small window around its first value and at least
/// <see cref="MinimumRetiredInstructions"/> instruction boundaries were observed.
/// </summary>
internal static class LivenessGate {
    private const int EntryWindowBytes = 64;
    private const int MinimumRetiredInstructions = 64;

    internal sealed class Sink : ICpuTraceSink {
        private ushort? m_entryPc;

        public long InstructionCount { get; private set; }
        public bool LeftEntryRegion { get; private set; }

        public void OnInstructionBoundary(ushort pc, byte a, byte f, byte b, byte c, byte d, byte e, byte h, byte l, ushort sp) {
            m_entryPc ??= pc;
            ++InstructionCount;

            if (
                !LeftEntryRegion &&
                (Math.Abs(value: (pc - m_entryPc.Value)) > EntryWindowBytes)
            ) {
                LeftEntryRegion = true;
            }
        }
    }

    /// <summary>Installs a fresh liveness sink on <paramref name="cpu"/> for the caller's whole run.</summary>
    /// <param name="cpu">The core to observe.</param>
    /// <returns>A handle whose <see cref="Handle.IsAlive"/> is meaningful once the run is over; disposing it detaches
    /// the sink.</returns>
    public static Handle Attach(Sm83 cpu) {
        var sink = new Sink();

        cpu.SetTraceSink(sink: sink);

        return new Handle(
            cpu: cpu,
            sink: sink
        );
    }

    /// <summary>The liveness sink's lifetime and verdict.</summary>
    public sealed class Handle : IDisposable {
        private readonly Sm83 m_cpu;
        private readonly Sink m_sink;

        internal Handle(Sm83 cpu, Sink sink) {
            m_cpu = cpu;
            m_sink = sink;
        }

        /// <summary>Gets a value indicating whether the observed run left its entry region and retired enough
        /// instructions to trust the probe's comparison.</summary>
        public bool IsAlive =>
            (m_sink.LeftEntryRegion && (m_sink.InstructionCount >= MinimumRetiredInstructions));
        /// <summary>Gets a one-line reason for a dead verdict.</summary>
        public string Reason =>
            $"the core never left its entry region and retired {m_sink.InstructionCount} instruction(s) — needs forward progress past a {EntryWindowBytes}-byte window and at least {MinimumRetiredInstructions}; presumed dead, not evaluated";

        /// <inheritdoc/>
        public void Dispose() =>
            m_cpu.SetTraceSink(sink: null);
    }
}
