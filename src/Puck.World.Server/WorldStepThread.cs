using System.Diagnostics;
using Puck.Abstractions.Waiting;
using Puck.Hosting;
using Puck.Hosting.Jobs;

namespace Puck.World.Server;

/// <summary>
/// A host's step thread: the one thread that executes the middle part of every authority tick the host steps
/// (<see cref="WorldServerStepShell.Execute"/>), a slice at a time, while the thread that prepared the slice waits for
/// it. The preparing thread stays the owner of a tick held across wait slices
/// (<see cref="WorldTick.SliceOwner"/>), so a console line it runs against a held tick refuses or queues at once
/// instead of waiting for a boundary only it can bring.
/// <para>
/// The wait is the preparing thread's own work, taken through the wait door's delegated form: it is not charged to a
/// pump's allowance, and the stall budget (<see cref="WaitDeadline.SynchronousBudget"/>), counted from the hand-off,
/// bounds it. A slice's own waits end sooner, by its slice deadline or by the tick's stall deadline, so a slice still
/// executing when the budget passes is computing. It stays in flight: <see cref="WorldServerStepShell.TryStep"/>
/// answers <see cref="ExecuteWait"/> as the pending dependency and collects the same slice on its next call, and
/// nothing prepares another meanwhile.
/// </para>
/// <para>
/// The thread starts with the first slice posted. It measures the tick's parts and its whole execution in a
/// <see cref="HostFrameWork"/> of its own, which the preparing thread folds into its iteration when it collects the
/// slice.
/// </para>
/// </summary>
/// <param name="narration">The hub a slice that outlasts the stall budget is reported on, or
/// <see langword="null"/> to report nothing.</param>
public sealed class WorldStepThread(WorldOutputHub? narration = null) : IJobLaneUnit, IDisposable {
    /// <summary>What a thread waits for while the step thread executes the slice it handed over.</summary>
    public static readonly WaitReason ExecuteWait = new(Subsystem: "authority", WaitsFor: "the step thread's tick");

    private readonly JobLane m_lane = new(name: "Puck world step");
    private readonly HostFrameWork m_work = new();

    private WorldExecutedTick m_executed;
    private WorldPreparedTick m_prepared;
    private bool m_reportedLate;

    /// <summary>Gets whether a slice handed to the step thread has not been collected yet. While it is, the authority
    /// it belongs to is the step thread's, and the thread that handed it over reads none of it.</summary>
    public bool InFlight => m_lane.InFlight;
    /// <summary>Gets or sets what the step thread calls, on its own thread, each time a slice finishes and is ready to
    /// collect: the wake of a host loop that went on without waiting for the slice. It must not throw.</summary>
    public Action? Progressed {
        get => m_lane.Finished;
        set => m_lane.Finished = value;
    }

    /// <summary>Stops the step thread once it has finished the slice it holds.</summary>
    /// <param name="deadline">How long to wait for the thread to end.</param>
    /// <returns><see langword="false"/> when a slice is still executing at the deadline: its authority is still in use
    /// and must not be disposed.</returns>
    public bool Stop(WaitDeadline deadline) => m_lane.Stop(deadline: deadline);
    /// <summary>Stops the step thread under the synchronous budget.</summary>
    public void Dispose() => m_lane.Dispose();

    // Prepares one slice of server's tick on the calling thread and hands it to the step thread; false when preparing
    // cancelled a replay drive and left nothing to execute. The calling thread must hold no authority gate: the slice
    // takes its server's gate on the step thread, and may enter another server's, so a caller that held one would wait
    // for a tick that waits for it.
    internal bool HandOver(WorldServer server, WorldReplayTape? tape, in FixedStepContext context, WaitDeadline deadline) {
        if (server.GateHeldByCallingThread) {
            throw new InvalidOperationException(message: "A thread that holds an authority gate cannot hand a tick to the step thread: the tick takes the gate to run.");
        }
        if (m_lane.InFlight) {
            throw new InvalidOperationException(message: "The step thread already holds a tick slice; collect it before handing over the next.");
        }

        var prepared = WorldServerStepShell.Prepare(server, tape, in context, deadline);

        if (prepared.Cancelled) {
            return false;
        }

        m_prepared = prepared;
        m_executed = default;
        server.Tick.NoteHandOver(handedOver: true);
        try {
            m_lane.Post(unit: this);
        } catch {
            // The tick's input is closed and nothing will execute it: let the threads its hold keeps out back in.
            server.Tick.NoteHandOver(handedOver: false);
            server.Tick.AbandonPrepared();
            m_prepared = default;
            throw;
        }

        return true;
    }
    // Waits for the slice in flight, which must be server's, until collectBy or the stall budget from its hand-off,
    // whichever is sooner, and takes it. A collectBy already passed polls. False while the slice is still executing.
    // What the slice threw is rethrown here.
    internal bool TryCollect(WorldServer server, WaitDeadline collectBy, out WorldPreparedTick prepared, out WorldExecutedTick executed) {
        if (!ReferenceEquals(objA: m_prepared.Server, objB: server)) {
            throw new InvalidOperationException(message: "The step thread holds another authority's tick slice.");
        }

        WaitOutcome outcome;

        try {
            outcome = m_lane.Collect(deadline: collectBy.Earliest(other: m_prepared.Stall), reason: in ExecuteWait);
        } catch {
            Settle();
            throw;
        }

        if (outcome == WaitOutcome.DeadlinePassed) {
            if (m_prepared.Stall.HasPassed && !m_reportedLate) {
                m_reportedLate = true;
                narration?.Narrate("world.tick", $"[world.tick: the step thread has executed one slice of a tick of '{server.InstanceIdentity}' for longer than the stall budget; the host waits for it]");
            }

            prepared = default;
            executed = default;

            return false;
        }

        prepared = m_prepared;
        executed = m_executed;
        Settle();

        return true;
    }

    // Folds what the step thread measured into the collecting thread's iteration and frees the slice's record.
    private void Settle() {
        m_prepared.Server?.Tick.NoteHandOver(handedOver: false);
        HostFrameWork.Fold(source: m_work);
        m_prepared = default;
        m_executed = default;
        m_reportedLate = false;
    }

    void IJobLaneUnit.Run() {
        using var binding = m_work.Bind();
        var started = Stopwatch.GetTimestamp();

        try {
            m_executed = WorldServerStepShell.Execute(prepared: in m_prepared);
        } finally {
            HostFrameWork.NoteExecuted(ticks: (Stopwatch.GetTimestamp() - started));
        }
    }
}
