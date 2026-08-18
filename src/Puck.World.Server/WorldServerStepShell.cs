using Puck.Hosting;

namespace Puck.World.Server;

/// <summary>
/// The server-side half of a fixed step, shared by every boot shape that owns a <see cref="WorldServer"/>: the
/// windowed desktop boot wraps it with the client/screens/editor post-step work, the headless simulation calls it
/// alone, and a hosted grain activation (<c>Puck.World.Silo</c>) calls it from its own tick thread. Steps the
/// authoritative <see cref="WorldServer"/>, then closes the tick's replay-tape input group and publishes the
/// completed tick to the caller's own tick-clock sink — the same two calls a boot shape used to make itself,
/// now made ONCE so no boot shape can let them drift.
/// </summary>
public static class WorldServerStepShell {
    /// <summary>Steps the server for one fixed tick and closes out the tick's tape/tick-clock bookkeeping.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <param name="tape">The replay tape (a no-op close while <see cref="WorldReplayMode.Idle"/>), or
    /// <see langword="null"/> for a row nothing records.</param>
    /// <param name="publishTick">Called with the completed tick count once the step and tape close finish — the
    /// caller's own tick-clock sink (a console wait gate's <c>PublishTick</c>, or a no-op for a caller with nothing
    /// subscribed to publish yet). This project cannot name <c>Puck.World</c>'s <c>WorldConsoleWaitGate</c> or
    /// <c>Puck.Launcher</c>'s <c>ITextCommandHoldGate</c> it implements — both sit above this project's
    /// exact-equality closure (<c>build/Architecture.props</c>) — so the shell takes the one member either shape
    /// actually calls, as a delegate, rather than the type.</param>
    /// <param name="context">The fixed-step context.</param>
    /// <param name="tcpHost">The socket door, or <see langword="null"/> in a shape that never constructs one.
    /// Drained BEFORE <see cref="WorldServer.Step"/> — the deterministic fair-merge window: every
    /// admission/submission/disconnect a connection's background reader marshaled since the last tick applies here,
    /// on the tick thread, before this tick's bodies advance.</param>
    /// <returns>The completed tick count (<c>context.Tick + 1</c>) — what every boot shape reports as its own
    /// <c>Tick</c>/<c>ElapsedTicks</c>.</returns>
    public static ulong Step(WorldServer server, WorldReplayTape? tape, Action<ulong> publishTick, in FixedStepContext context, WorldTcpHost? tcpHost = null) {
        tcpHost?.DrainPending();
        server.Step(context: in context);

        var tick = (context.Tick + 1UL);

        // The caller's own tick clock: a queued script held by world.wait resumes off THIS count, so the barrier
        // measures completed simulation ticks rather than frames or wall time.
        publishTick(tick);

        // Close this tick's captured server-input group while a recording is armed (a no-op otherwise) — the whole
        // tick's intent/command stream has reached the loopback taps by now (submitted during ApplySnapshot, before
        // this Step call), so the group is complete regardless of what a presentation-side post-step does next.
        if (tape is { Mode: not WorldReplayMode.Idle }) {
            tape.NoteTick();
        }

        return tick;
    }
}
