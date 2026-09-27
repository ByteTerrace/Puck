namespace Puck.HumbleGamingDeck;

/// <summary>A forked machine the time-travel layer advances ahead of the authority for runahead, one completed frame at a
/// time.</summary>
public sealed class HumbleGamingDeckLookahead : ITimeTravelLookahead<MachinePads> {
    // A frame is 89,342 dots, one fewer on odd frames while rendering; stepping in CPU-cycle slices until the frame index
    // moves never overshoots by more than one slice.
    private const ulong SliceTicks = 12;

    private readonly MachineFork<HgdMachine, HgdMachineConfiguration> m_fork;
    private readonly HgdMachine m_machine;

    private readonly uint[] m_colours = new uint[(HgdPpu.Width * HgdPpu.Height)];
    private long m_colouredFrame = -1;

    /// <summary>Initializes a new instance of the <see cref="HumbleGamingDeckLookahead"/> class over a fork it owns.</summary>
    /// <param name="fork">The fork to advance; the lookahead disposes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fork"/> is <see langword="null"/>.</exception>
    public HumbleGamingDeckLookahead(MachineFork<HgdMachine, HgdMachineConfiguration> fork) {
        ArgumentNullException.ThrowIfNull(argument: fork);

        m_fork = fork;
        m_machine = fork.Machine;
    }

    /// <inheritdoc/>
    public long NativeFrameIndex => m_machine.FrameIndex;
    /// <inheritdoc/>
    public ReadOnlySpan<uint> Framebuffer {
        get {
            if (m_colouredFrame != m_machine.FrameIndex) {
                HgdPalette.Convert(
                    codes: m_machine.Ppu.Frame,
                    colours: m_colours
                );
                m_colouredFrame = m_machine.FrameIndex;
            }

            return m_colours;
        }
    }

    /// <inheritdoc/>
    public void ApplyInput(in MachinePads input) =>
        HgdPad.Apply(
            controllers: m_machine.Controllers,
            pads: in input
        );
    /// <inheritdoc/>
    public void RestoreState(byte[] buffer, int length) {
        m_machine.RestoreState(reader: new StateReader(
            buffer: buffer,
            length: length,
            start: 0
        ));
        m_colouredFrame = -1;
    }
    /// <inheritdoc/>
    public void RunFrame() {
        var frame = m_machine.FrameIndex;

        while (m_machine.FrameIndex == frame) {
            m_machine.RunCycles(masterTicks: SliceTicks);
        }
    }
    /// <inheritdoc/>
    public void Dispose() =>
        m_fork.Dispose();
}
