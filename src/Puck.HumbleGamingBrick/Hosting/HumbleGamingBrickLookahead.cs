using Puck.Abstractions.Machines;
using Puck.Hosting;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A bare SM83-family lookahead sibling for machine-neutral runahead — a headless forked <see cref="MachineInstance"/>
/// (no audio, no GPU) the <see cref="MachineTimeTravel{TInput}"/> layer rebases from the authoritative machine and
/// advances on predicted input. It never drives audio, so only the real machine ever opens a speaker; disposing it
/// returns the fork to the core's bounded instance pool.
/// </summary>
internal sealed class HumbleGamingBrickLookahead : ITimeTravelLookahead<MachinePadState> {
    private readonly MachineFork m_instance;
    private readonly IJoypad m_joypad;
    private readonly ITiltSensor m_tiltSensor;
    private readonly IFramebuffer m_framebuffer;
    private readonly ulong m_oneFrameCycles;

    public HumbleGamingBrickLookahead(MachineFork instance, ulong oneFrameCycles) {
        m_instance = instance;
        m_joypad = instance.GetRequiredService<IJoypad>();
        m_tiltSensor = instance.GetRequiredService<ITiltSensor>();
        m_framebuffer = instance.GetRequiredService<IFramebuffer>();
        m_oneFrameCycles = oneFrameCycles;
    }

    /// <inheritdoc/>
    public long NativeFrameIndex =>
        (long)(m_instance.Machine.Clock.CycleCount / m_oneFrameCycles);

    /// <inheritdoc/>
    public ReadOnlySpan<uint> Framebuffer =>
        m_framebuffer.Pixels;

    /// <inheritdoc/>
    public void RestoreState(byte[] buffer, int length) =>
        m_instance.Machine.RestoreState(reader: new StateReader(buffer: buffer, start: 0, length: length));

    /// <inheritdoc/>
    public void ApplyInput(in MachinePadState input) {
        m_joypad.SetButtons(pressed: BrickPad.ToJoypad(pad: in input));

        // The lookahead applies the SAME full input image the authoritative core does (buttons AND the recorded tilt
        // sensor sample) so a sensor-bearing cart's predicted branch matches the authority's — a no-op on a cart that
        // never reads the tilt sensor.
        m_tiltSensor.SetTilt(x: input.Tilt.X, y: input.Tilt.Y);
    }

    /// <inheritdoc/>
    public void RunFrame() =>
        m_instance.Machine.Run(tCycles: m_oneFrameCycles);

    /// <inheritdoc/>
    public void Dispose() =>
        m_instance.Dispose();
}
