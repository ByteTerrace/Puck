namespace Puck.HumbleGamingBrick;

/// <summary>
/// The single owner of the machine's CURRENTLY-EMULATED console model — the one mutable copy that a live device swap
/// (<see cref="Machine.SwitchModel"/>) retargets and that the snapshot carries, so a restore or fork resumes the
/// model the machine was actually running rather than the model it booted from. Every capability gate around the
/// machine (SystemBus / PPU / APU / CPU / OAM-DMA) still caches its own fast boolean for the hot path; this component
/// is the authority those caches are re-derived from on a swap and after a restore. Seeded from
/// <see cref="MachineConfiguration.Model"/> at construction — the boot model — and thereafter the model the machine
/// runs as.
/// </summary>
public sealed class ModelState : ISnapshotable {
    private ConsoleModel m_model;

    /// <summary>Seeds the current model from the boot configuration.</summary>
    /// <param name="configuration">The machine configuration whose <see cref="MachineConfiguration.Model"/> is the boot model.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public ModelState(MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_model = configuration.Model;
    }

    /// <summary>Gets the model the machine is CURRENTLY emulating (the boot model until a live swap retargets it).</summary>
    public ConsoleModel Model =>
        m_model;

    /// <summary>Retargets the current model — called only from <see cref="Machine.SwitchModel"/>, which also re-pushes
    /// the capability gates and applies the hardware-transition side effects.</summary>
    /// <param name="model">The new model.</param>
    internal void Set(ConsoleModel model) =>
        m_model = model;

    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        writer.WriteByte(value: (byte)m_model);
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        m_model = (ConsoleModel)reader.ReadByte();
}
