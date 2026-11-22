namespace Puck.World;

/// <summary>
/// The values the authority publishes for the frame: the boot world's tick record, every running world's machine
/// presentation and the diagnostic copy. The composition root's collect copies them on the pump inside the boundary
/// window, once a host step, and publishes all three together by reference (<see cref="Publish"/>); a frame reads only
/// what was published, so it never reads a value a tick in flight is writing. Each value is immutable, so a reader on
/// another thread sees one collect's values.
/// </summary>
public sealed class WorldPresentationFeed {
    private volatile WorldTickRecord m_tick = WorldTickRecord.Empty;
    private volatile WorldMachineTable m_machines = WorldMachineTable.Empty;
    private volatile WorldDiagnosticCopy m_diagnostics = WorldDiagnosticCopy.Off;

    /// <summary>Gets the boot world's values as the last collect copied them.</summary>
    public WorldTickRecord Tick => m_tick;
    /// <summary>Gets every running world's machine presentation as the last collect captured it.</summary>
    public WorldMachineTable Machines => m_machines;
    /// <summary>Gets the diagnostic copy the last collect made, or <see cref="WorldDiagnosticCopy.Off"/> while no
    /// diagnostic view is on.</summary>
    public WorldDiagnosticCopy Diagnostics => m_diagnostics;

    /// <summary>Publishes one collect's values. Called by the composition root's collect on the pump, inside the boundary
    /// window, and once as the World boots so the first frame reads a record.</summary>
    /// <param name="tick">The boot world's values.</param>
    /// <param name="machines">Every running world's machine presentation.</param>
    /// <param name="diagnostics">The diagnostic copy.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void Publish(WorldTickRecord tick, WorldMachineTable machines, WorldDiagnosticCopy diagnostics) {
        ArgumentNullException.ThrowIfNull(argument: tick);
        ArgumentNullException.ThrowIfNull(argument: machines);
        ArgumentNullException.ThrowIfNull(argument: diagnostics);
        m_tick = tick;
        m_machines = machines;
        m_diagnostics = diagnostics;
    }
}
