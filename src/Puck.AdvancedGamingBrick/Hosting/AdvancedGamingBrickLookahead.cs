using Puck.Abstractions.Machines;
using Puck.Hosting;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A bare ARM7TDMI lookahead sibling for machine-neutral runahead — a headless forked <see cref="AgbMachineInstance"/>
/// (no audio, no GPU) the <see cref="MachineTimeTravel{TInput}"/> layer rebases from the authoritative machine and
/// advances on predicted input. It never drives audio, so only the real machine ever opens a speaker; disposing it
/// returns the fork to the core's bounded instance pool.
/// </summary>
internal sealed class AdvancedGamingBrickLookahead : ITimeTravelLookahead<MachinePadState> {
    private readonly AgbMachineFork m_instance;
    private readonly AdvancedGamingBrickMachine m_machine;
    private readonly AgbCartridge m_cartridge;

    public AdvancedGamingBrickLookahead(AgbMachineFork instance) {
        m_instance = instance;
        m_machine = instance.Machine;
        m_cartridge = instance.GetRequiredService<AgbCartridge>();
    }

    /// <inheritdoc/>
    public long NativeFrameIndex =>
        (m_machine.Cycles / AdvancedGamingBrickMachine.CyclesPerFrame);

    /// <inheritdoc/>
    public ReadOnlySpan<uint> Framebuffer =>
        m_machine.Framebuffer;

    /// <inheritdoc/>
    public void RestoreState(byte[] buffer, int length) =>
        m_machine.RestoreState(reader: new StateReader(buffer: buffer, start: 0, length: length));

    /// <inheritdoc/>
    public void ApplyInput(in MachinePadState input) {
        m_machine.SetKeyInput(keys: AdvancedPad.ToKeyInput(pad: in input));

        // The lookahead applies the SAME full input image the authoritative core does (buttons AND the recorded solar
        // light level AND tilt) so a sensor-bearing cart's predicted branch matches the authority's — a no-op on a cart
        // with no matching sensor.
        m_cartridge.SetLightLevel(level: input.LightLevel);
        m_cartridge.SetTilt(x: input.Tilt.X, y: input.Tilt.Y);
    }

    /// <inheritdoc/>
    public void RunFrame() =>
        _ = m_machine.RunFrame();

    /// <inheritdoc/>
    public void Dispose() =>
        m_instance.Dispose();
}
