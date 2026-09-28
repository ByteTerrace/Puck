using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One world's tick window for <see cref="WorldRowStepWindowGuard"/>: the world's authority identity, the
/// activation of the server running it, and the tick every pre-drain submission to it targets. Two worlds, or two
/// activations sharing an authority (a world recreated under its name, instances declaring one authority), never share a
/// window.</summary>
/// <param name="Authority">The world's authority identity.</param>
/// <param name="Activation">The activation of the server running it (<see cref="WorldDocumentVersion.Activation"/>).</param>
/// <param name="Tick">The tick every pre-drain submission targets (<see cref="WorldServer.NextInputTick"/>).</param>
/// <param name="Retired">Cancelled when that activation stops (<see cref="WorldServer.Stopped"/>), which drops its
/// claims; <see langword="default"/> for one that never does.</param>
public readonly record struct WorldRowStepWindow(string Authority, Guid Activation, ulong Tick, CancellationToken Retired = default) {
    /// <summary>Returns the window a server's next pre-drain submissions land in.</summary>
    /// <param name="server">The server.</param>
    /// <returns>The window.</returns>
    public static WorldRowStepWindow Of(WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        return new WorldRowStepWindow(
            Activation: server.DocumentVersion.Activation,
            Authority: server.AuthorityIdentity,
            Retired: server.Stopped,
            Tick: server.NextInputTick
        );
    }
}
/// <summary>
/// The read-your-writes guard for <c>world.row.step</c> within one tick window of one world. A step reads a WHOLE row
/// off the live definition, mutates one field, and submits a whole-row upsert; two steps to the SAME row inside one
/// window (before the buffered mutations drain) both compose from the same pre-drain base and drain FIFO, so the later
/// upsert reverts the earlier's field — both would echo success. The guard keeps one set of claimed rows per world
/// activation (<see cref="WorldRowStepWindow"/>'s authority and activation) and refuses a second claim on one of them in
/// that activation's current tick; the set empties when its tick moves on, and is dropped when its activation stops.
/// Steps in DIFFERENT windows (a held chord repeating once per tick) never collide, and neither do steps in different
/// worlds or activations. Console-side control state off every hashed simulation path; locked, since an activation's
/// stop drops its set from whichever thread stops it.
/// </summary>
public sealed class WorldRowStepWindowGuard {
    private readonly Lock m_gate = new();
    private readonly Dictionary<(string Authority, Guid Activation), Claims> m_worlds = [];

    private sealed class Claims {
        public CancellationTokenRegistration Retired { get; set; }
        public HashSet<string> Rows { get; } = new(comparer: StringComparer.Ordinal);
        public ulong Tick { get; set; }
    }

    /// <summary>Gets how many world activations hold a claim set.</summary>
    public int Worlds {
        get {
            lock (m_gate) {
                return m_worlds.Count;
            }
        }
    }

    // The claimed rows of a window, emptied first when its activation's tick has moved on. An activation's set is opened
    // on first use and dropped when that activation stops.
    private HashSet<string> RowsOf(WorldRowStepWindow window) {
        var key = (window.Authority, window.Activation);

        if (!m_worlds.TryGetValue(key: key, value: out var claims)) {
            claims = new Claims { Tick = window.Tick };
            m_worlds[key] = claims;
            claims.Retired = window.Retired.Register(callback: () => {
                lock (m_gate) {
                    _ = m_worlds.Remove(key: key);
                }
            });
        } else if (claims.Tick != window.Tick) {
            claims.Tick = window.Tick;
            claims.Rows.Clear();
        }

        return claims.Rows;
    }

    /// <summary>Records <paramref name="rowIdentity"/> as buffered in <paramref name="window"/> — called only once a
    /// step's upsert is genuinely submitted, so a step refused for any other reason never blocks a retry.</summary>
    /// <param name="window">The window the submitted step lands in.</param>
    /// <param name="rowIdentity">The row the submitted step addresses.</param>
    public void Claim(WorldRowStepWindow window, string rowIdentity) {
        lock (m_gate) {
            _ = RowsOf(window: window).Add(item: rowIdentity);
        }
    }
    /// <summary>Gets a value indicating whether a step to <paramref name="rowIdentity"/> collides with one already
    /// buffered in <paramref name="window"/>. The whole-row upsert stomps at the row grain, so the addressed field is not
    /// part of the identity.</summary>
    /// <param name="window">The window the step would land in.</param>
    /// <param name="rowIdentity">The row a step addresses (a section path, or a section path plus row key).</param>
    /// <returns><see langword="true"/> when the row already has a step buffered in this window; otherwise
    /// <see langword="false"/>.</returns>
    public bool IsClaimed(WorldRowStepWindow window, string rowIdentity) {
        lock (m_gate) {
            return RowsOf(window: window).Contains(item: rowIdentity);
        }
    }
}
