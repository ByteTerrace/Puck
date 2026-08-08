using System.Numerics;
using Puck.AdvancedGamingBrick;
using Puck.Hosting;
using Puck.Snapshots;

namespace Puck.Demo.AgbDebug;

/// <summary>The AGB debug scene's complete recorded time-travel input image — the full held-input the rewind ring
/// replays and the runahead lookahead predicts on, so a solar/tilt-sensor cart's replayed and predicted branches match
/// the live one. Carries the active-low KEYINPUT halfword AND the recorded solar light level AND the tilt reading; a
/// plain ROM leaves the sensor channels at their reset values and pays nothing.</summary>
/// <param name="Keys">The active-low KEYINPUT image (clear bit = pressed).</param>
/// <param name="Light">The recorded solar light level, 0 (darkest) to 255 (brightest).</param>
/// <param name="Tilt">The recorded tilt/accelerometer sample, -1..1 per axis, centered by default.</param>
internal readonly record struct AgbSceneInput(ushort Keys, byte Light = 0, Vector2 Tilt = default);

/// <summary>
/// A bare ARM7TDMI lookahead sibling for the AGB debug scene's machine-neutral runahead — a headless forked
/// <see cref="AgbMachineInstance"/> (no <see cref="Audio.CabinetAudioOutput"/>, no GPU) the shared
/// <see cref="MachineTimeTravel{TInput}"/> layer rebases from the real machine and advances on the predicted (held)
/// full input image. It never drives audio, so only the real machine ever opens a speaker; disposing it returns the fork
/// to the core's bounded instance pool.
/// </summary>
internal sealed class AgbSceneLookahead : ITimeTravelLookahead<AgbSceneInput> {
    private readonly AgbMachineFork m_instance;
    private readonly AdvancedGamingBrickMachine m_machine;
    private readonly AgbCartridge m_cartridge;

    public AgbSceneLookahead(AgbMachineFork instance) {
        m_instance = instance;
        m_machine = instance.Machine;
        m_cartridge = instance.GetRequiredService<AgbCartridge>();
    }

    /// <inheritdoc/>
    public long NativeFrameIndex => (m_machine.Cycles / AdvancedGamingBrickMachine.CyclesPerFrame);

    /// <inheritdoc/>
    public ReadOnlySpan<uint> Framebuffer => m_machine.Framebuffer;

    /// <inheritdoc/>
    public void RestoreState(byte[] buffer, int length) =>
        m_machine.RestoreState(reader: new StateReader(buffer: buffer, start: 0, length: length));

    /// <inheritdoc/>
    public void ApplyInput(in AgbSceneInput input) {
        m_machine.SetKeyInput(keys: input.Keys);

        // Predict on the SAME full image the authority applies (keys AND the recorded solar light AND tilt) so a
        // sensor-bearing cart's predicted branch matches the real machine's — a no-op on a cart with no such sensor.
        m_cartridge.SetLightLevel(level: input.Light);
        m_cartridge.SetTilt(x: input.Tilt.X, y: input.Tilt.Y);
    }

    /// <inheritdoc/>
    public void RunFrame() => _ = m_machine.RunFrame();

    /// <inheritdoc/>
    public void Dispose() => m_instance.Dispose();
}
