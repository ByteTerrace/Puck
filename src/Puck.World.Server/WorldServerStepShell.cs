using Puck.Abstractions.Waiting;
using Puck.Hosting;

namespace Puck.World.Server;

/// <summary>
/// The server-side half of a fixed step, shared by every boot shape that owns a <see cref="WorldServer"/>: the
/// windowed desktop boot wraps it with the client/screens/editor post-step work, the headless simulation calls it
/// alone, and a hosted grain activation (<c>Puck.World.Silo</c>) calls it from its own tick thread. Steps the
/// authoritative <see cref="WorldServer"/>, publishes the completed host work count to the caller's tick-clock
/// sink, then closes the tick's replay-tape input group — the same two calls a boot shape used to make itself,
/// now made ONCE so no boot shape can let them drift. A tape in <see cref="WorldReplayMode.Replaying"/> is also
/// served here: its recorded tick is fed into the server's doors immediately before the step, and a fast-forwarding
/// drive steps again inside the same call, up to its burst, unless the host steps one tick per produced frame
/// (<see cref="HostPacing.StepsOneTickPerFrame"/>), whose every driven tick gets its own step and frame.
/// </summary>
public static class WorldServerStepShell {
    /// <summary>Steps the server for one fixed tick — or, while a fast-forwarding replay drive wants more, for a
    /// burst of consecutive ticks — and closes out each tick's tape/tick-clock bookkeeping.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <param name="tape">The replay tape (a no-op close while <see cref="WorldReplayMode.Idle"/>; the drive
    /// injection and burst while <see cref="WorldReplayMode.Replaying"/>), or <see langword="null"/> for a row
    /// nothing records.</param>
    /// <param name="publishTick">Called after each server step and tape close, with the host work count — the
    /// caller's own tick-clock sink (a console wait gate's <c>PublishTick</c>, or a no-op for a caller with nothing
    /// subscribed to publish yet). This project cannot name <c>WorldConsoleWaitGate</c>, which sits above this
    /// project's exact-equality closure (<c>build/Architecture.props</c>), so the shell takes the one member it calls,
    /// as a delegate, rather than the type.</param>
    /// <param name="pacing">The pacing of the host stepping the server: a replay drive fast-forwards several ticks in
    /// one call only under a host that does not step one tick per produced frame.</param>
    /// <param name="context">Host pacing coordinates and step width. Only the width advances simulation state:
    /// <see cref="WorldServer.Advance"/> uses the authority's checkpointed tick and engine-time coordinates.
    /// <see cref="FixedStepContext.Tick"/> supplies the independent count returned and published to the host.</param>
    /// <returns>The completed tick count — <c>context.Tick + 1</c>, or further along by the number of extra ticks a
    /// fast-forwarding drive stepped; a caller derives its own elapsed engine time from the difference times
    /// <see cref="FixedStepContext.StepTicks"/>.</returns>
    public static ulong Step(WorldServer server, WorldReplayTape? tape, Action<ulong> publishTick, in FixedStepContext context, HostPacing pacing) {
        var tick = TryStep(server, tape, publishTick, in context, pacing, WaitDeadline.Standalone(), out var pending);
        if (pending is { } reason) { throw new TimeoutException(reason.ToString()); }
        return tick;
    }

    /// <summary>Steps complete ticks, retaining the current tick when a dependency exceeds the slice.</summary>
    public static ulong TryStep(WorldServer server, WorldReplayTape? tape, Action<ulong> publishTick, in FixedStepContext context, HostPacing pacing,
        WaitDeadline deadline, out WaitReason? pending) {
        pending = null;
        var current = context;
        var burst = 0;

        while (true) {
            using var work = HostFrameWork.MeasureWorld(server.InstanceIdentity);
            var resuming = server.Tick.PendingReason is not null;
            if (!resuming) { server.DrainMailbox(); }
            // Keep the simulation step and tape close atomic with other authority readers and writers.
            lock (server.AuthorityGate) {
                try {
                    if (!resuming) { server.PendingReplayInputs = tape?.InjectDriveTick(); }
                    pending = server.Tick.TryAdvance(current.StepTicks, deadline);
                    if (pending is not null) { return current.Tick; }
                } catch (InvalidDataException error) when (tape?.Mode == WorldReplayMode.Replaying &&
                    ReplayRefusalExtensions.ReasonOf(error) == ReplayRefusal.MissingPrivateDraw) {
                    var name = tape.CancelDrive();
                    server.Output.Narrate("replay.drive", $"[replay.drive {name}: {error.Message}]");
                    return current.Tick;
                } finally {
                    if (pending is null) {
                        server.PendingReplayInputs?.Dispose();
                        server.PendingReplayInputs = null;
                    }
                }
                server.FlushMutationJournal?.Invoke();
                server.EnforceJournalDepth();
                // Close this tick's captured server-input group for an armed recording or an in-session history, or
                // compare and advance a drive (a no-op otherwise): the whole tick's intent and command stream has
                // reached the loopback taps by now, submitted during ApplySnapshot before this step.
                tape?.NoteTick();
            }

            var tick = (current.Tick + 1UL);

            // The caller's own tick clock: a queued script held by world.wait resumes off THIS count, so the barrier
            // measures completed simulation ticks rather than frames or wall time.
            publishTick(tick);
            work.Complete();

            if (
                resuming || pacing.StepsOneTickPerFrame ||
                (tape is not { WantsFastForwardStep: true }) ||
                (++burst >= tape.FastForwardBurst)
            ) {
                return tick;
            }

            current = new FixedStepContext(
                ElapsedTicks: (current.ElapsedTicks + current.StepTicks),
                StepTicks: current.StepTicks,
                Tick: tick
            );
        }
    }
}
