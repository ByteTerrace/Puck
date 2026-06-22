using Puck.Commands;

namespace Puck.Demo.Replay;

/// <summary>
/// An <see cref="ISnapshotSource"/> whose snapshots come from a pure function of the tick — no capture, no clock.
/// It is the scripted input a determinism/replay check feeds through the same record/replay seams the live router
/// uses, so the proof exercises the real path rather than a bespoke shortcut.
/// </summary>
public sealed class ScriptedSnapshotSource : ISnapshotSource {
    private readonly Func<ulong, CommandSnapshot> m_script;

    /// <summary>Initializes the source from a deterministic script mapping a tick to its snapshot.</summary>
    /// <param name="script">The per-tick snapshot script.</param>
    /// <exception cref="ArgumentNullException"><paramref name="script"/> is <see langword="null"/>.</exception>
    public ScriptedSnapshotSource(Func<ulong, CommandSnapshot> script) {
        ArgumentNullException.ThrowIfNull(argument: script);

        m_script = script;
    }

    /// <inheritdoc/>
    public CommandSnapshot SnapshotForTick(ulong tick, ulong windowEndTick) {
        return m_script(arg: tick);
    }
}
