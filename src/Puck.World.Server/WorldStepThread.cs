using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Puck.Abstractions.Waiting;
using Puck.Hosting;
using Puck.Hosting.Jobs;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// A host's step thread: the one thread that executes the middle part of every authority tick the host steps
/// (<see cref="WorldServerStepShell.Execute"/>), a wave at a time. A wave is the slices of one or more authorities'
/// ticks whose inputs the preparing thread has closed (<see cref="Prepare"/>), handed over together
/// (<see cref="HandOver"/>) and collected together (<see cref="TryCollect"/>), so the preparing thread visits the step
/// thread once a wave, never once a tick. The preparing thread stays the owner of a tick held across wait slices
/// (<see cref="WorldTick.SliceOwner"/>), so a console line it runs against a held tick refuses or queues at once instead
/// of waiting for a boundary only it can bring.
/// <para>
/// A wave of several fresh ticks runs its ticks in parallel on the host's job pool, one partition each, at
/// <see cref="JobClass.Tick"/>; a tick's own forks then run inline in its partition. Each tick touches only its own
/// authority and the services that authority's tick already reaches from the step thread (the outbox records its
/// deliveries, narration locks), and a tick in a parallel partition that enters another authority's gate is refused
/// (<see cref="WorldServer.EnterAtTickBoundary"/>), so no two partitions share an authority. A wave holding a slice that
/// resumes a held tick, a wave of one tick, and every wave of a host with no pool or a serial one run on the step
/// thread in the order prepared. Which thread runs a tick never changes what it computes: its input was closed before
/// the hand-off, and the preparing thread collects the wave in the order it prepared it.
/// </para>
/// <para>
/// The wait is the preparing thread's own work, taken through the wait door's delegated form: it is not charged to a
/// pump's allowance, and the stall budget (<see cref="WaitDeadline.SynchronousBudget"/>), counted from the hand-off,
/// bounds it. A slice's own waits end sooner, by its slice deadline or by the tick's stall deadline, so a wave still
/// executing when the budget passes is computing. It stays in flight and nothing prepares another meanwhile.
/// </para>
/// <para>
/// The thread starts with the first wave posted. Each tick is measured in a <see cref="HostFrameWork"/> of its own,
/// folded in wave order into the step thread's, which the preparing thread folds into its iteration when it collects
/// the wave. What a tick throws is kept with its slice and rethrown when the preparing thread takes that slice's result
/// (<see cref="Result"/>).
/// </para>
/// </summary>
/// <param name="narration">The hub a wave that outlasts the stall budget is reported on, or <see langword="null"/> to
/// report nothing.</param>
/// <param name="boundary">The host's boundary window, closed to the handing thread from each hand-off until it
/// collects the wave (<see cref="WorldLinkBoundary.HandedOver"/>), or <see langword="null"/> for a step thread no host
/// owns, which keeps one of its own. An authority no host admitted takes the step thread's boundary with its first
/// slice, so every authority a step thread executes refuses the thread that left its tick in flight.</param>
/// <param name="jobs">The job pool a wave of several fresh ticks forks onto, or <see langword="null"/> to run every wave
/// on the step thread. Shared, not owned.</param>
public sealed class WorldStepThread(WorldOutputHub? narration = null, WorldLinkBoundary? boundary = null, JobPool? jobs = null) : IJobLaneUnit, IDisposable {
    /// <summary>What a thread waits for while the step thread executes the wave it handed over.</summary>
    public static readonly WaitReason ExecuteWait = new(Subsystem: "authority", WaitsFor: "the step thread's tick");

    private readonly WorldLinkBoundary m_boundary = (boundary ?? new WorldLinkBoundary());
    private readonly JobLane m_lane = new(name: "Puck world step");
    private readonly HostFrameWork m_work = new();
    // The wave's slices, in the order prepared; reused from wave to wave.
    private readonly List<Slot> m_slots = [];

    private int m_count;
    // Whether the wave's results are collected and readable; the next Prepare starts a new wave.
    private bool m_collected;
    private bool m_reportedLate;
    // The execution context the wave was handed over from, which each tick runs in on whichever thread runs it.
    private ExecutionContext? m_context;
    // The fork's body for a parallel wave, made with the first one.
    private WavePartitions? m_partitions;

    // Runs one slice per partition. A tick may replace its authority's arena, which the lane unit that forks declares
    // (IJobLaneUnit.Run); the fork contract stays unmarked, so the partition reaches the slice through a delegate.
    private sealed class WavePartitions(Action<int> run) : IJobPartitions {
        public void Run(int partition) => run(partition);
    }

    private sealed class Slot {
        public readonly HostFrameWork Work = new();
        public WorldPreparedTick Prepared;
        public WorldExecutedTick Executed;
        public ExceptionDispatchInfo? Fault;
        public string? Narration;
        public bool Parallel;

        // Runs on the thread that runs the slice, inside the wave's execution context.
        public void Execute() {
            using var binding = Work.Bind();
            using var wave = (Parallel ? WorldServer.ExecuteInParallelWave(server: Prepared.Server!) : default);

            if (Narration is not null) { WorldNarrationScope.Current = Narration; }
            try {
                Executed = WorldServerStepShell.Execute(prepared: in Prepared);
            } catch (Exception error) {
                Fault = ExceptionDispatchInfo.Capture(source: error);
            }
        }
    }

    /// <summary>Gets whether a wave handed to the step thread has not been collected yet. While it is, the authorities
    /// it holds are the step thread's, and the thread that handed it over reads none of them.</summary>
    public bool InFlight => m_lane.InFlight;
    /// <summary>Gets the slices of the wave being prepared or in flight; zero once it is collected, until the next
    /// slice is prepared.</summary>
    public int Count => (m_collected ? 0 : m_count);
    /// <summary>Gets or sets what the step thread calls, on its own thread, each time a wave finishes and is ready to
    /// collect: the wake of a host loop that went on without waiting for the wave. It must not throw.</summary>
    public Action? Progressed {
        get => m_lane.Finished;
        set => m_lane.Finished = value;
    }

    /// <summary>Stops the step thread once it has finished the wave it holds.</summary>
    /// <param name="deadline">How long to wait for the thread to end.</param>
    /// <returns><see langword="false"/> when a wave is still executing at the deadline: its authorities are still in
    /// use and must not be disposed.</returns>
    public bool Stop(WaitDeadline deadline) {
        if (!m_lane.Stop(deadline: deadline)) {
            return false;
        }

        // Nothing executes any more: a wave handed over and never collected no longer keeps its handing thread out.
        m_boundary.Collected();

        return true;
    }
    /// <summary>Stops the step thread under the synchronous budget.</summary>
    public void Dispose() => _ = Stop(deadline: WaitDeadline.Synchronous());

    // Prepares one slice of server's tick on the calling thread into the wave being prepared, starting a new wave when
    // the last one was collected, and answers its index; -1 when preparing cancelled a replay drive and left nothing to
    // execute. The calling thread must hold no authority gate: the slice takes its server's gate where it runs, so a
    // caller that held one would wait for a tick that waits for it.
    [Replaces(root: typeof(object))]
    internal int Prepare(WorldServer server, WorldReplayTape? tape, in FixedStepContext context, WaitDeadline deadline, string? narration = null) {
        if (server.GateHeldByCallingThread) {
            throw new InvalidOperationException(message: "A thread that holds an authority gate cannot hand a tick to the step thread: the tick takes the gate to run.");
        }
        if (m_lane.InFlight) {
            throw new InvalidOperationException(message: "The step thread already holds a wave; collect it before preparing the next.");
        }
        if (server.Boundary is null) {
            server.Boundary = m_boundary;
        } else if (!ReferenceEquals(objA: server.Boundary, objB: m_boundary)) {
            throw new InvalidOperationException(message: $"Authority '{server.InstanceIdentity}' belongs to another host's boundary window than the step thread it was handed to.");
        }
        if (m_collected) {
            Clear();
        }
        for (var index = 0; (index < m_count); index++) {
            if (ReferenceEquals(objA: m_slots[index].Prepared.Server, objB: server)) {
                throw new InvalidOperationException(message: $"Authority '{server.InstanceIdentity}' already has a slice in the wave being prepared.");
            }
        }

        var prepared = WorldServerStepShell.Prepare(server, tape, in context, deadline);

        if (prepared.Cancelled) {
            return -1;
        }
        if (m_count == m_slots.Count) {
            m_slots.Add(item: new Slot());
        }

        var slot = m_slots[m_count];

        slot.Prepared = prepared;
        slot.Executed = default;
        slot.Fault = null;
        slot.Narration = narration;

        return m_count++;
    }
    // Hands the wave prepared to the step thread and returns at once. From here until the collect every authority in
    // the wave is the step thread's: the window is closed to this thread.
    internal void HandOver() {
        if (m_count == 0) {
            throw new InvalidOperationException(message: "The step thread is handed a wave only once a slice is prepared.");
        }
        if (m_collected) {
            throw new InvalidOperationException(message: "The wave was already executed; prepare the next one first.");
        }

        var parallel = ((m_count > 1) && (jobs is { Serial: false }));

        for (var index = 0; (index < m_count); index++) {
            parallel &= !m_slots[index].Prepared.Resuming;
        }
        for (var index = 0; (index < m_count); index++) {
            m_slots[index].Parallel = parallel;
        }
        m_context = ExecutionContext.Capture();
        try {
            m_lane.Post(unit: this);
        } catch {
            // The ticks' inputs are closed and nothing will execute them: let the threads their holds keep out back in.
            for (var index = 0; (index < m_count); index++) {
                m_slots[index].Prepared.Server!.Tick.AbandonPrepared();
            }
            Clear();
            throw;
        }
        WorldTickWork.Process.Add(kind: WorldTickWork.Waves, amount: 1L);
        WorldTickWork.Process.Add(kind: WorldTickWork.WaveTicks, amount: m_count);
        m_boundary.HandedOver();
    }
    // Waits for the wave in flight until collectBy or the stall budget from its hand-off, whichever is sooner, and
    // takes it; its slices' results are then read with Result. A collectBy already passed polls. False while the wave
    // is still executing.
    [Replaces(root: typeof(object))]
    internal bool TryCollect(WaitDeadline collectBy) {
        if (!m_lane.InFlight) {
            throw new InvalidOperationException(message: "The step thread holds no wave to collect.");
        }

        var stall = m_slots[0].Prepared.Stall;
        var deadline = collectBy.Earliest(other: stall);

        if (!deadline.HasPassed && !m_lane.Ready) {
            WorldTickWork.Process.Add(kind: WorldTickWork.PumpWaits, amount: 1L);
        }

        WaitOutcome outcome;

        try {
            outcome = m_lane.Collect(deadline: deadline, reason: in ExecuteWait);
        } catch {
            Settle();
            Clear();
            throw;
        }

        if (outcome == WaitOutcome.DeadlinePassed) {
            if (stall.HasPassed && !m_reportedLate) {
                m_reportedLate = true;
                narration?.Narrate("world.tick", $"[world.tick: the step thread has executed a wave holding a tick of '{m_slots[0].Prepared.Server!.InstanceIdentity}' for longer than the stall budget; the host waits for it]");
            }

            return false;
        }

        Settle();
        m_collected = true;

        return true;
    }
    // Whether the wave in flight, or the one last collected, holds exactly one slice, of server.
    internal bool Holds(WorldServer server) => ((m_count == 1) && ReferenceEquals(objA: m_slots[0].Prepared.Server, objB: server));
    // The slice prepared at index.
    internal WorldPreparedTick PreparedAt(int index) => m_slots[index].Prepared;
    /// <summary>Reads how one slice of the wave last collected ended, rethrowing on the calling thread what its tick
    /// threw.</summary>
    /// <param name="index">The slice's index, as <c>Prepare</c> answered it.</param>
    /// <returns>How the slice ended.</returns>
    /// <exception cref="InvalidOperationException">No wave has been collected since the last one was prepared.</exception>
    public WorldExecutedTick Result(int index) {
        if (!m_collected) {
            throw new InvalidOperationException(message: "A wave's results are read once it is collected.");
        }

        var slot = m_slots[index];

        if (slot.Fault is { } fault) {
            slot.Fault = null;
            fault.Throw();
        }

        return slot.Executed;
    }

    // Folds what the step thread measured into the collecting thread's iteration.
    private void Settle() {
        m_boundary.Collected();
        HostFrameWork.Fold(source: m_work);
        m_context = null;
        m_reportedLate = false;
    }
    // Frees the slices' records for the next wave.
    private void Clear() {
        for (var index = 0; (index < m_count); index++) {
            var slot = m_slots[index];

            slot.Prepared = default;
            slot.Executed = default;
            slot.Fault = null;
            slot.Narration = null;
        }
        m_count = 0;
        m_collected = false;
    }

    void IJobLaneUnit.Run() {
        using var binding = m_work.Bind();
        var started = Stopwatch.GetTimestamp();

        try {
            if (m_slots[0].Parallel) {
                jobs!.Fork(body: (m_partitions ??= new WavePartitions(run: partition => RunSlot(slot: m_slots[partition]))), jobClass: JobClass.Tick, partitions: m_count);
            } else {
                for (var index = 0; (index < m_count); index++) {
                    RunSlot(slot: m_slots[index]);
                }
            }
        } finally {
            for (var index = 0; (index < m_count); index++) {
                HostFrameWork.Fold(source: m_slots[index].Work);
            }
            HostFrameWork.NoteExecuted(ticks: (Stopwatch.GetTimestamp() - started));
        }
    }

    // A partition runs on a worker whose execution context is its own: the slice runs in the one it was handed over
    // from, as it does on the step thread, so every value that flows with a logical call reads as it did there.
    private void RunSlot(Slot slot) {
        if (m_context is { } context) {
            ExecutionContext.Run(callback: static state => ((Slot)state!).Execute(), executionContext: context, state: slot);
        } else {
            slot.Execute();
        }
    }
}
