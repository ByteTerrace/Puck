namespace Puck.World.Server;

/// <summary>
/// The persistence facade of <see cref="WorldServer"/>: journal undo and its depth horizon, the authority
/// checkpoint's capture and restore, the simulation state hash, and the replay timeline gate.
/// </summary>
/// <remarks>It owns no state of its own — every section it captures and restores belongs to one of the other
/// facades, which is exactly why it is the one place that walks them all.</remarks>
public sealed partial class WorldPersistence {
    private readonly WorldServer m_host;

    /// <summary>Gets the server whose document, tick, grants, rule host, population and machine host this facade
    /// captures and restores.</summary>
    private WorldServer Host => m_host;

    // The sections whose owning facade serves them through IWorldPersistedSection alone.
    private IWorldPersistedSection<WorldBoardEnforcementCheckpoint> BoardEnforcement => m_host.Tick;
    private IWorldPersistedDecisions Decisions => m_host.RuleHost;

    /// <summary>Initializes the facade over the server it persists.</summary>
    /// <param name="host">The owning server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    internal WorldPersistence(WorldServer host) {
        ArgumentNullException.ThrowIfNull(argument: host);

        m_host = host;
    }
}
