using Puck.Hosting;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The server-side half of a fixed step, shared by BOTH boot shapes: the windowed <see cref="WorldSimulation"/> wraps
/// it with the client/screens/editor post-step work, and the headless simulation calls it alone. Steps the
/// authoritative <see cref="WorldServer"/>, then closes the tick's replay-tape input group and publishes the
/// completed tick to the console wait gate — the same two calls <see cref="WorldSimulation"/> used to make itself,
/// now made ONCE so the two boot shapes can never let them drift (see <c>docs/verification/headless-boot</c>'s
/// cross-shape determinism control).
/// </summary>
internal static class WorldServerStepShell {
    /// <summary>Steps the server for one fixed tick and closes out the tick's tape/wait-gate bookkeeping.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <param name="tape">The replay tape (a no-op close while <see cref="WorldReplayMode.Idle"/>).</param>
    /// <param name="waitGate">The console wire's tick barrier.</param>
    /// <param name="context">The fixed-step context.</param>
    /// <param name="tcpHost">The socket door, or <see langword="null"/> in a shape that never constructs one.
    /// Drained BEFORE <see cref="WorldServer.Step"/> — the deterministic fair-merge window: every
    /// admission/submission/disconnect a connection's background reader marshaled since the last tick applies here,
    /// on the tick thread, before this tick's bodies advance.</param>
    /// <returns>The completed tick count (<c>context.Tick + 1</c>) — what both boot shapes report as their own
    /// <c>Tick</c>/<c>ElapsedTicks</c>.</returns>
    public static ulong Step(WorldServer server, WorldReplayTape tape, WorldConsoleWaitGate waitGate, in FixedStepContext context, WorldTcpHost? tcpHost = null) {
        tcpHost?.DrainPending();
        server.Step(context: in context);

        var tick = (context.Tick + 1UL);

        // The console wire's tick clock: a queued script held by world.wait resumes off THIS count, so the barrier
        // measures completed simulation ticks rather than frames or wall time.
        waitGate.PublishTick(tick: tick);

        // Close this tick's captured server-input group while a recording is armed (a no-op otherwise) — the whole
        // tick's intent/command stream has reached the loopback taps by now (submitted during ApplySnapshot, before
        // this Step call), so the group is complete regardless of what a presentation-side post-step does next.
        if (tape.Mode != WorldReplayMode.Idle) {
            tape.NoteTick();
        }

        return tick;
    }
}
