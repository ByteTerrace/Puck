using Puck.Abstractions.Waiting;
using Puck.Hosting;

namespace Puck.World.Server;

/// <summary>What <see cref="WorldServerStepShell.Prepare"/> closed for one slice of one authority tick, and the only
/// thing <see cref="WorldServerStepShell.Execute"/> runs. Only <see cref="WorldServerStepShell.Prepare"/> mints one, so
/// every executed slice names the thread that prepared it as the tick's owner.</summary>
public readonly struct WorldPreparedTick {
    internal WorldPreparedTick(WorldServer server, WorldReplayTape? tape, in WorldTick.PreparedAdvance advance, WaitDeadline slice, bool resuming, bool cancelled) {
        Server = server;
        Tape = tape;
        Advance = advance;
        Slice = slice;
        Resuming = resuming;
        Cancelled = cancelled;
        Stall = WaitDeadline.Synchronous();
        Owner = WorldTick.SliceOwner;
    }

    internal WorldServer? Server { get; }
    internal WorldReplayTape? Tape { get; }
    // The tick's coordinates and the neighbour projections sampled for it on the preparing thread.
    internal WorldTick.PreparedAdvance Advance { get; }
    // How long the tick's own waits (a machine's drain, a fork's join) may block in this slice.
    internal WaitDeadline Slice { get; }
    // Whether the slice resumes a tick held across wait slices rather than starting one.
    internal bool Resuming { get; }
    // Whether preparing cancelled a replay drive, so there is nothing to execute.
    internal bool Cancelled { get; }
    // When the preparing thread stops waiting for another thread to execute the slice: the stall budget from the
    // hand-off. The slice's own waits end by the earlier of Slice and the tick's stall deadline, which freezes the
    // tick, so a slice that has not answered by this is computing, not waiting.
    internal WaitDeadline Stall { get; }
    // The managed id of the thread the slice was prepared for (WorldTick.SliceOwner, the preparing thread itself unless
    // it prepared inside a slice it runs for another): the held tick's owner, whichever thread executes it.
    internal int Owner { get; }
}

/// <summary>How one executed slice of an authority tick ended.</summary>
public readonly struct WorldExecutedTick {
    private WorldExecutedTick(bool completed, WaitReason? pending) {
        Completed = completed;
        Pending = pending;
    }

    /// <summary>Gets whether the tick finished: its journal is folded and its tape group closed, and it is ready to
    /// publish.</summary>
    public bool Completed { get; }
    /// <summary>Gets the dependency the tick is held on across wait slices, or <see langword="null"/> when the slice
    /// left none. A slice that neither completed nor is pending stepped nothing: a replay drive was cancelled.</summary>
    public WaitReason? Pending { get; }

    internal static WorldExecutedTick Finished => new(completed: true, pending: null);
    internal static WorldExecutedTick Nothing => default;
    internal static WorldExecutedTick Held(WaitReason reason) => new(completed: false, pending: reason);
}

/// <summary>
/// The server-side half of a fixed step, shared by every boot shape that owns a <see cref="WorldServer"/>: the
/// windowed desktop boot wraps it with the client/screens/editor post-step work, the headless simulation calls it
/// alone, and a hosted row (<c>Puck.World.Silo</c>) steps through it too. One authority tick is three parts, each a
/// function here, so no boot shape can let them drift.
/// <para><see cref="Prepare"/>, on the thread that owns the host's tables (the pump), closes the tick's input: it
/// drains the server's mailbox, feeds a replaying tape's recorded tick into the server's doors, applies the ordered
/// submissions and samples the neighbour projections the tick reads. <see cref="Execute"/>, on whichever thread runs
/// the tick, takes the authority gate, advances the tick over that closed input, folds its journal and closes its tape
/// group. <see cref="Collect"/>, on the preparing thread again, publishes the completed tick to the caller's
/// hook.</para>
/// <para>Between a prepare and its execute the tick is held for its preparing thread
/// (<see cref="WorldTick.HoldPrepared"/>), so no other thread applies anything to the authority after the tick's input
/// closed and before the tick runs.</para>
/// <para><see cref="TryStep"/> runs the three in order: all on the calling thread for a caller with no host (a law, a
/// replay's shadow server), or with the execute handed to a host's <see cref="WorldStepThread"/> while the caller
/// waits for it. A tape in <see cref="WorldReplayMode.Replaying"/> that fast-forwards steps again inside the same call,
/// up to its burst, unless the host steps one tick per produced frame (<see cref="HostPacing.StepsOneTickPerFrame"/>),
/// whose every driven tick gets its own step and frame.</para>
/// </summary>
public static class WorldServerStepShell {
    /// <summary>Steps the server for one fixed tick — or, while a fast-forwarding replay drive wants more, for a
    /// burst of consecutive ticks — and closes out each tick's tape/tick-clock bookkeeping, all on the calling
    /// thread.</summary>
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
        var tick = TryStep(server, tape, publishTick, in context, pacing, WaitDeadline.Synchronous(), out var pending);
        HostWaits.ThrowIfPending(pending);
        return tick;
    }

    /// <summary>Steps complete ticks, retaining the current tick when a dependency exceeds the slice.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <param name="tape">The replay tape, or <see langword="null"/> for a row nothing records.</param>
    /// <param name="publishTick">Called on the calling thread after each completed tick, with the host work
    /// count.</param>
    /// <param name="context">Host pacing coordinates and step width; a call that resumes a pending tick passes the
    /// coordinates of the tick it resumes.</param>
    /// <param name="pacing">The pacing of the host stepping the server.</param>
    /// <param name="deadline">How long the tick's own waits may block in this call.</param>
    /// <param name="pending">The dependency the current tick still waits for, or <see langword="null"/> when every
    /// tick the call began has completed.</param>
    /// <param name="thread">The host's step thread, which executes each tick while the calling thread waits for it;
    /// <see langword="null"/> executes on the calling thread.</param>
    /// <param name="collectBy">How long the calling thread waits for a tick the step thread executes before it answers
    /// <see cref="WorldStepThread.ExecuteWait"/> and leaves the tick in flight for its next call: a deadline already
    /// passed polls, and <see langword="null"/> waits for the tick, up to the stall budget.</param>
    /// <returns>The completed tick count: <paramref name="context"/>'s tick when the first tick is pending or a
    /// replay drive was cancelled, otherwise further along by each tick completed.</returns>
    public static ulong TryStep(WorldServer server, WorldReplayTape? tape, Action<ulong> publishTick, in FixedStepContext context, HostPacing pacing,
        WaitDeadline deadline, out WaitReason? pending, WorldStepThread? thread = null, WaitDeadline? collectBy = null) {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(publishTick);

        pending = null;
        var current = context;
        var burst = 0;
        // A slice this thread handed over and stopped waiting for is collected before anything else is prepared, and
        // ends the call as a resumed tick does.
        var collecting = (thread is { InFlight: true });
        var wait = (collectBy ?? WaitDeadline.Synchronous());

        while (true) {
            WorldExecutedTick executed;
            WorldPreparedTick prepared;

            if (thread is null) {
                prepared = Prepare(server, tape, in current, deadline);
                executed = (prepared.Cancelled ? WorldExecutedTick.Nothing : Execute(in prepared));
            } else {
                if (!thread.InFlight) {
                    // A fast-forwarding drive steps its burst inside one call, so the caller waits for each of its
                    // ticks whatever it was told: only how many recorded ticks a host step replays depends on it.
                    if ((tape is { WantsFastForwardStep: true }) && !pacing.StepsOneTickPerFrame) { wait = WaitDeadline.Synchronous(); }
                    if (!thread.HandOver(server, tape, in current, deadline)) { return current.Tick; }
                }
                if (!thread.TryCollect(server, wait, out prepared, out executed)) {
                    pending = WorldStepThread.ExecuteWait;
                    return current.Tick;
                }
            }

            if (executed.Pending is { } reason) {
                pending = reason;
                return current.Tick;
            }
            if (!executed.Completed) {
                return current.Tick;
            }

            var tick = (current.Tick + 1UL);

            Collect(server, tick, publishTick);

            if (
                collecting || prepared.Resuming || pacing.StepsOneTickPerFrame ||
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

    /// <summary>The first part of a tick, on the thread that owns the host's tables: closes the tick's input. For a
    /// tick that starts it drains the server's mailbox, then under the authority gate feeds a replaying tape's recorded
    /// tick into the server's doors, applies the ordered submissions and samples the neighbour projections
    /// (<see cref="WorldTick.PrepareAdvance(ulong)"/>, which resolves neighbours through the host). It leaves the tick held for
    /// the calling thread until its execute takes the gate (<see cref="WorldTick.HoldPrepared"/>). A slice that resumes
    /// a tick held across wait slices prepares nothing.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <param name="tape">The replay tape, or <see langword="null"/> for a row nothing records.</param>
    /// <param name="context">The tick's coordinates; only the step width reaches the server.</param>
    /// <param name="deadline">How long the tick's own waits may block in this slice.</param>
    /// <returns>The slice to execute, or one marked cancelled when a replay drive could not supply the tick and was
    /// cancelled. The calling thread is the owner a held tick names.</returns>
    public static WorldPreparedTick Prepare(WorldServer server, WorldReplayTape? tape, in FixedStepContext context, WaitDeadline deadline) {
        ArgumentNullException.ThrowIfNull(server);

        using var work = HostFrameWork.MeasureWorld(server.InstanceIdentity);
        var resuming = server.Tick.PendingReason is not null;

        if (!resuming) { server.DrainMailbox(); }

        lock (server.AuthorityGate) {
            var injected = false;

            try {
                if (!resuming) {
                    server.PendingReplayInputs = tape?.InjectDriveTick();
                    injected = true;
                }

                var advance = server.Tick.PrepareAdvance(context.StepTicks);

                if (!resuming) { server.Tick.HoldPrepared(); }

                return new WorldPreparedTick(advance: in advance, cancelled: false, resuming: resuming, server: server, slice: deadline, tape: tape);
            } catch (Exception error) {
                // The inputs a drive injected live until the tick they feed completes; a tick that never starts frees
                // them here.
                if (injected) {
                    server.PendingReplayInputs?.Dispose();
                    server.PendingReplayInputs = null;
                }
                if (!LacksPrivateDraw(tape, error)) { throw; }
                CancelDrive(server, tape!, error);

                return new WorldPreparedTick(advance: default, cancelled: true, resuming: resuming, server: server, slice: deadline, tape: tape);
            }
        }
    }

    /// <summary>The second part of a tick, on whichever thread runs it: takes the authority gate, releases the hold its
    /// prepare left, advances the tick over the input that prepare closed, and for a tick that completes flushes and
    /// folds its journal and closes its tape group. A tick held on a dependency names the thread that prepared the
    /// slice as its owner (<see cref="WorldTick.SliceOwner"/>), not the thread that executed it.</summary>
    /// <param name="prepared">The slice <see cref="Prepare"/> closed.</param>
    /// <returns>How the slice ended.</returns>
    /// <exception cref="ArgumentException"><paramref name="prepared"/> is a default value no <see cref="Prepare"/>
    /// minted, or one whose prepare cancelled its drive.</exception>
    public static WorldExecutedTick Execute(in WorldPreparedTick prepared) {
        if ((prepared.Server is not { } server) || prepared.Cancelled) {
            throw new ArgumentException(message: "A tick slice executes only what Prepare closed.", paramName: nameof(prepared));
        }

        var tape = prepared.Tape;

        using var owner = WorldTick.ExecuteFor(owner: prepared.Owner);
        using var work = HostFrameWork.MeasureWorld(server.InstanceIdentity);

        // Keep the simulation step and tape close atomic with other authority readers and writers.
        lock (server.AuthorityGate) {
            WaitReason? pending = null;

            try {
                var advance = prepared.Advance;

                pending = server.Tick.TryAdvance(in advance, prepared.Slice);
                if (pending is { } reason) { return WorldExecutedTick.Held(reason: reason); }
            } catch (Exception error) when (LacksPrivateDraw(tape, error)) {
                CancelDrive(server, tape!, error);
                return WorldExecutedTick.Nothing;
            } finally {
                if (pending is null) {
                    server.PendingReplayInputs?.Dispose();
                    server.PendingReplayInputs = null;
                }
            }
            server.FlushMutationJournal?.Invoke();
            server.FoldJournal();
            // Close this tick's captured server-input group for an armed recording or an in-session history, or
            // compare and advance a drive (a no-op otherwise): the whole tick's intent and command stream has
            // reached the loopback taps by now, submitted during ApplySnapshot before this step.
            tape?.NoteTick();
        }

        return WorldExecutedTick.Finished;
    }

    /// <summary>The third part of a tick, on the thread that prepared it: publishes the completed tick to the caller's
    /// own tick clock. A queued script held by <c>world.wait</c> resumes off this count, so the barrier measures
    /// completed simulation ticks rather than frames or wall time.</summary>
    /// <param name="server">The server whose tick completed.</param>
    /// <param name="tick">The completed host work count.</param>
    /// <param name="publishTick">The caller's tick-clock sink.</param>
    public static void Collect(WorldServer server, ulong tick, Action<ulong> publishTick) {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(publishTick);

        using var work = HostFrameWork.MeasureWorld(server.InstanceIdentity);

        publishTick(tick);
        work.Complete();
    }

    // Whether a replaying drive's tick needs a private draw the tape does not carry: the drive cannot go on.
    private static bool LacksPrivateDraw(WorldReplayTape? tape, Exception error) => (
        (error is InvalidDataException) &&
        (tape?.Mode == WorldReplayMode.Replaying) &&
        (ReplayRefusalExtensions.ReasonOf(error) == ReplayRefusal.MissingPrivateDraw)
    );
    // Cancels the drive and narrates why; the tick it could not supply is not stepped.
    private static void CancelDrive(WorldServer server, WorldReplayTape tape, Exception error) {
        var name = tape.CancelDrive();

        server.Output.Narrate("replay.drive", $"[replay.drive {name}: {error.Message}]");
    }
}
