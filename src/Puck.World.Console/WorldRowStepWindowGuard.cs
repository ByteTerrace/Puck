using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One world's tick window for <see cref="WorldRowStepWindowGuard"/>: the world's authority identity, the
/// activation of the server running it, and the tick every pre-drain submission to it targets. Two worlds, or a world
/// and the one recreated under its name, never share a window.</summary>
/// <param name="Authority">The world's authority identity.</param>
/// <param name="Activation">The activation of the server running it (<see cref="WorldDocumentVersion.Activation"/>).</param>
/// <param name="Tick">The tick every pre-drain submission targets (<see cref="WorldServer.NextInputTick"/>).</param>
public readonly record struct WorldRowStepWindow(string Authority, Guid Activation, ulong Tick) {
    /// <summary>Returns the window a server's next pre-drain submissions land in.</summary>
    /// <param name="server">The server.</param>
    /// <returns>The window.</returns>
    public static WorldRowStepWindow Of(WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        return new WorldRowStepWindow(
            Activation: server.DocumentVersion.Activation,
            Authority: server.AuthorityIdentity,
            Tick: server.NextInputTick
        );
    }
}
/// <summary>
/// The read-your-writes guard for <c>world.row.step</c> within one tick window of one world. A step reads a WHOLE row
/// off the live definition, mutates one field, and submits a whole-row upsert; two steps to the SAME row inside one
/// window (before the buffered mutations drain) both compose from the same pre-drain base and drain FIFO, so the later
/// upsert reverts the earlier's field — both would echo success. The guard remembers, per world authority, which rows
/// have been claimed in that world's current window (<see cref="WorldRowStepWindow"/>) and refuses a second claim on
/// one of them; a window that moves on (the next tick, or a recreated world under the same authority) starts empty.
/// Steps in DIFFERENT windows (a held chord repeating once per tick) never collide, and neither do steps in different
/// worlds. Not thread-safe by design: the command pump is single-threaded, and this is console-side control state off
/// every hashed simulation path.
/// </summary>
public sealed class WorldRowStepWindowGuard {
    private readonly Dictionary<string, (WorldRowStepWindow Window, HashSet<string> Rows)> m_worlds = new(comparer: StringComparer.Ordinal);

    // The claimed rows of a window, emptied first when the window has moved on from the one its world last had.
    private HashSet<string> RowsOf(WorldRowStepWindow window) {
        if (m_worlds.TryGetValue(key: window.Authority, value: out var current) && (current.Window == window)) {
            return current.Rows;
        }

        var rows = (current.Rows ?? new HashSet<string>(comparer: StringComparer.Ordinal));

        rows.Clear();
        m_worlds[window.Authority] = (window, rows);

        return rows;
    }

    /// <summary>Records <paramref name="rowIdentity"/> as buffered in <paramref name="window"/> — called only once a
    /// step's upsert is genuinely submitted, so a step refused for any other reason never blocks a retry.</summary>
    /// <param name="window">The window the submitted step lands in.</param>
    /// <param name="rowIdentity">The row the submitted step addresses.</param>
    public void Claim(WorldRowStepWindow window, string rowIdentity) => _ = RowsOf(window: window).Add(item: rowIdentity);
    /// <summary>Gets a value indicating whether a step to <paramref name="rowIdentity"/> collides with one already
    /// buffered in <paramref name="window"/>. The whole-row upsert stomps at the row grain, so the addressed field is not
    /// part of the identity.</summary>
    /// <param name="window">The window the step would land in.</param>
    /// <param name="rowIdentity">The row a step addresses (a section path, or a section path plus row key).</param>
    /// <returns><see langword="true"/> when the row already has a step buffered in this window; otherwise
    /// <see langword="false"/>.</returns>
    public bool IsClaimed(WorldRowStepWindow window, string rowIdentity) => RowsOf(window: window).Contains(item: rowIdentity);
}
