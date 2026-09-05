using Puck.Abstractions.Machines;
using Puck.Hosting;

namespace Puck.GamingBricks;

/// <summary>
/// The machine-neutral cable-link substrate: an <see cref="IMachineLink"/> that OWNS its members' cores for its
/// lifetime. Forming the link quiesces each member's <see cref="QueuedMachineWorker"/> at a frame boundary and lends
/// its core to one paired execution thread, which advances the whole group through the medium's own deterministic
/// interleave (an <see cref="IMachineGroupCore"/>) and then publishes each member's framebuffer, audio, and feedback
/// through that member's existing worker — so a host and a world see the same objects a linked machine had before the
/// cable went in. Per-seat pads route by cable order.
/// <para>
/// The bounded queue semantics of the single-core worker apply to the group as ONE unit: <see cref="Submit"/> accepts
/// exact tick/seat-input segments up to a finite pending window and applies producer backpressure at capacity rather
/// than dropping or coalescing authoritative history, and <see cref="Step"/> is the synchronous submit-and-drain path
/// <see cref="IMachineLink"/> exposes. While the link is live, a member's own <see cref="IScreenMachine.Step"/> is a
/// no-op and its <see cref="IQueuedScreenMachine.Submit"/> is rejected: the host steps the link instead.
/// </para>
/// <para>
/// Time travel is coupled. One <see cref="MachineTimeTravel{TInput}"/> rides the group core, whose state image carries
/// every member's snapshot AND the medium's pacing state, so a rewind lands every member and the interleave itself on
/// the recorded instant and the resumed future matches the un-rewound run. Fast-forward repeats the exact segment for
/// the whole group, keeping the members in lockstep. Runahead is refused: a lookahead would have to fork every member
/// and the medium, and a peer's future is not a function of held input.
/// </para>
/// <para>
/// Severing is what unplugging a cable does: <see cref="Dispose"/> stops the group thread, disconnects the medium at
/// once — an unfinished externally-clocked transfer is left pending on its port, exactly as an unplugged console's is —
/// and returns each core to its own worker along with the group's tick-to-cycle accumulator phase.
/// </para>
/// </summary>
/// <remarks>A cross-process transport is deliberately out of scope; the seam it would carry is the group core's
/// serializable state image plus each submitted (tick budget, seat inputs) segment.</remarks>
public sealed class LinkedMachineGroup : IMachineLink, IMachineCoreLender {
    private readonly IMachineGroupCore m_core;
    private readonly IScreenMachine[] m_machines;
    private readonly int m_maximumPendingSteps;
    private readonly MachineTimeTravel<MachineLinkPads> m_timeTravel;
    private readonly Queue<GroupWorkItem> m_work;
    private readonly QueuedMachineWorker[] m_workers;
    private readonly string m_workerName;

    private bool m_acceptingWork;
    private long m_backpressureEvents;
    private long m_completedSteps;
    private ulong m_cycleRemainder;
    private int m_disposed;
    private long m_submittedSteps;
    private Thread? m_worker;
    private Exception? m_workerFault;

    // Condition variable, not a plain gate: Monitor.Wait/Pulse require an object monitor, which System.Threading.Lock
    // refuses (CS9216).
    private readonly object m_workLock = new();
    private readonly Lock m_lifecycleLock = new();

    /// <summary>Forms a link over two or more queued machines: each member's core is lent to this group, the medium is
    /// built over the lent cores, and the group's execution thread starts. A failure at any point returns every core
    /// already lent, so a refused link leaves its members exactly as it found them.</summary>
    /// <param name="machines">The members, in cable order — the same instances a host holds.</param>
    /// <param name="workers">Each member's queued worker, in the same order as <paramref name="machines"/>.</param>
    /// <param name="createCore">Builds the medium over the lent cores, in cable order.</param>
    /// <param name="maximumPendingSteps">The finite number of accepted-but-incomplete group segments before the
    /// producer backpressures.</param>
    /// <param name="workerName">The group execution thread's diagnostic name.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Fewer than two members were supplied, or the two lists differ in
    /// length.</exception>
    /// <exception cref="InvalidOperationException">A member carries no content, or its core is already lent to another
    /// link.</exception>
    public LinkedMachineGroup(
        IReadOnlyList<IScreenMachine> machines,
        IReadOnlyList<QueuedMachineWorker> workers,
        Func<IReadOnlyList<IQueuedMachineCore>, IMachineGroupCore> createCore,
        int maximumPendingSteps = 8,
        string workerName = "machine-link"
    ) {
        ArgumentNullException.ThrowIfNull(argument: createCore);
        ArgumentNullException.ThrowIfNull(argument: machines);
        ArgumentNullException.ThrowIfNull(argument: workerName);
        ArgumentNullException.ThrowIfNull(argument: workers);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: maximumPendingSteps,
            other: 1
        );

        if (machines.Count < 2) {
            throw new ArgumentException(
                message: "A cable link needs two or more machines.",
                paramName: nameof(machines)
            );
        }

        if (workers.Count != machines.Count) {
            throw new ArgumentException(
                message: "Every member must supply its own queued worker.",
                paramName: nameof(workers)
            );
        }

        m_machines = [.. machines];
        m_maximumPendingSteps = maximumPendingSteps;
        m_work = new Queue<GroupWorkItem>(capacity: (maximumPendingSteps + 1));
        m_workerName = workerName;
        m_workers = [.. workers];

        var lent = new IQueuedMachineCore[m_workers.Length];
        var lentCount = 0;

        try {
            for (; (lentCount < m_workers.Length); ++lentCount) {
                lent[lentCount] = m_workers[lentCount].LendCore(lender: this)
                    ?? throw new InvalidOperationException(message: $"Member {lentCount} carries no content, so there is nothing to link.");
            }

            m_core = createCore(arg: lent);
            m_timeTravel = new MachineTimeTravel<MachineLinkPads>(
                core: m_core,
                cyclesPerSecond: m_core.CyclesPerSecond
            );
        } catch {
            for (var index = 0; (index < lentCount); ++index) {
                m_workers[index].ReturnCore(hostAccumulator: 0UL);
            }

            throw;
        }

        StartWorker();
    }

    /// <summary>Gets the number of submissions that encountered a full pending-segment window and waited for
    /// capacity.</summary>
    public long BackpressureEvents {
        get {
            lock (m_workLock) {
                return m_backpressureEvents;
            }
        }
    }
    /// <inheritdoc/>
    public long CompletedTransfers =>
        m_core.CompletedTransfers;
    /// <summary>Gets the number of accepted group segments whose emulation has completed.</summary>
    public long CompletedSteps {
        get {
            lock (m_workLock) {
                return m_completedSteps;
            }
        }
    }
    /// <summary>Gets the group's current shared cycle count, read on the group's execution thread — the monotonic stamp
    /// a captured instant carries, and the coordinate a rewind lands on. Zero when the link is severed.</summary>
    public long CycleCount {
        get {
            var cycles = 0L;

            _ = RunOnLinkThread(work: () => cycles = m_core.CycleCount);

            return cycles;
        }
    }
    /// <inheritdoc/>
    public IReadOnlyList<IScreenMachine> Machines =>
        m_machines;
    /// <summary>Gets the maximum number of accepted group segments that may remain incomplete.</summary>
    public int MaximumPendingSteps =>
        m_maximumPendingSteps;
    /// <summary>Gets the number of accepted group segments not yet completed, including one currently
    /// executing.</summary>
    public long PendingSteps {
        get {
            lock (m_workLock) {
                return Math.Max(
                    val1: 0L,
                    val2: (m_submittedSteps - m_completedSteps)
                );
            }
        }
    }
    /// <summary>Gets a group-thread fault description, or <see langword="null"/> while the link is healthy.</summary>
    public string? QueueFault {
        get {
            lock (m_workLock) {
                return ((m_workerFault is { } fault)
                    ? $"{fault.GetType().Name}: {fault.Message}"
                    : null
                );
            }
        }
    }
    /// <summary>Gets a fingerprint folding every byte the medium has carried, in order — the traffic signal two runs of
    /// the same linked script must agree on.</summary>
    public ulong TrafficFingerprint =>
        m_core.TrafficFingerprint;

    /// <summary>Captures the group's whole state image — every member's snapshot plus the medium's pacing state — on
    /// the group's execution thread, so it observes a coherent inter-instruction boundary.</summary>
    /// <returns>The state image, or an empty array when the link is severed.</returns>
    public byte[] CaptureState() {
        var image = Array.Empty<byte>();

        _ = RunOnLinkThread(work: () => {
            var buffer = Array.Empty<byte>();
            var length = m_core.CaptureState(buffer: ref buffer);

            image = buffer[..length];
        });

        return image;
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (0 != Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        )) {
            return;
        }

        lock (m_lifecycleLock) {
            StopWorker();
            m_timeTravel.Dispose();
            m_core.Dispose();

            foreach (var worker in m_workers) {
                worker.ReturnCore(hostAccumulator: m_cycleRemainder);
            }
        }
    }
    /// <summary>Rewinds the whole group to the oldest captured instant inside the requested native-frame window, or the
    /// nearest older instant when that window is empty. Every member and the medium's pacing land together.</summary>
    /// <param name="frames">The number of native frames to move backward.</param>
    /// <returns>The number of native frames actually rewound.</returns>
    public int RewindBy(int frames) {
        var rewound = 0;

        _ = RunOnLinkThread(work: () => {
            rewound = m_timeTravel.RewindBy(
                frames: frames,
                hostAccumulator: out var landedAccumulator
            );

            if (rewound > 0) {
                // The group jumped to a past instant: restore the tick-to-cycle accumulator phase that instant was
                // produced under, atomically with the members, so identical future ticks buy identical budgets.
                m_cycleRemainder = landedAccumulator;

                foreach (var worker in m_workers) {
                    worker.RestageLentFrame();
                }
            }
        });

        return rewound;
    }
    /// <inheritdoc/>
    public void InvalidateLinkHistory() =>
        m_timeTravel.Reset();
    /// <inheritdoc/>
    public bool RunOnLinkThread(Action work) {
        ArgumentNullException.ThrowIfNull(argument: work);

        if (
            (0 != Volatile.Read(location: ref m_disposed)) ||
            (m_worker is null)
        ) {
            return false;
        }

        using var completion = new ManualResetEventSlim(initialState: false);
        var queued = false;

        lock (m_workLock) {
            if (
                m_acceptingWork &&
                (m_workerFault is null)
            ) {
                m_work.Enqueue(item: GroupWorkItem.ForInvoke(
                    completion: completion,
                    work: work
                ));
                Monitor.Pulse(obj: m_workLock);
                queued = true;
            }
        }

        if (queued) {
            completion.Wait();
        }

        return queued;
    }
    /// <inheritdoc/>
    public void SeverLink() =>
        Dispose();
    /// <summary>Sets the group's fast-forward factor — the number of exact tick/seat-input segments run per submission,
    /// clamped to at least 1. Every member advances the same number of segments, so the lockstep holds.</summary>
    /// <param name="factor">The exact-segment repeat count (1 = realtime).</param>
    public void SetFastForward(int factor) =>
        _ = RunOnLinkThread(work: () => m_timeTravel.SetFastForward(factor: factor));
    /// <summary>Arms or disarms the group's rewind ring. While armed, each stepped group frame is captured; disarming
    /// clears the captured history.</summary>
    /// <param name="enabled">Whether to capture rewind history.</param>
    public void SetRewindEnabled(bool enabled) =>
        _ = RunOnLinkThread(work: () => m_timeTravel.SetRewindEnabled(enabled: enabled));
    /// <inheritdoc/>
    public void Step(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs) {
        if (EnqueueStep(
            deltaTicks: deltaTicks,
            forceStage: true,
            inputs: inputs
        ) == QueuedMachineSubmission.Rejected) {
            ThrowIfFaulted();

            return;
        }

        Drain();
    }
    /// <summary>Accepts one exact tick/seat-input segment for ordered execution, applying producer backpressure at the
    /// group's pending-segment capacity.</summary>
    /// <param name="deltaTicks">The segment's fixed-step tick budget, shared by every member.</param>
    /// <param name="inputs">Each member's controller image, in cable order.</param>
    /// <returns>The observable submission outcome.</returns>
    public QueuedMachineSubmission Submit(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs) =>
        EnqueueStep(
            deltaTicks: deltaTicks,
            forceStage: false,
            inputs: inputs
        );

    private void Drain() {
        using var completion = new ManualResetEventSlim(initialState: false);

        lock (m_workLock) {
            if (
                (m_workerFault is not null) ||
                !m_acceptingWork
            ) {
                ThrowIfFaultedLocked();

                return;
            }

            m_work.Enqueue(item: GroupWorkItem.Barrier(completion: completion));
            Monitor.Pulse(obj: m_workLock);
        }

        completion.Wait();
        ThrowIfFaulted();
    }
    private QueuedMachineSubmission EnqueueStep(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs, bool forceStage) {
        if (
            (0 != Volatile.Read(location: ref m_disposed)) ||
            (m_worker is null) ||
            (0UL == deltaTicks)
        ) {
            return QueuedMachineSubmission.Rejected;
        }

        if (inputs.Length != m_machines.Length) {
            throw new ArgumentException(
                message: $"A link over {m_machines.Length} machines needs one controller image per member; {inputs.Length} were supplied.",
                paramName: nameof(inputs)
            );
        }

        var pads = MachineLinkPads.From(inputs: inputs);
        var backpressured = false;

        lock (m_workLock) {
            while (
                m_acceptingWork &&
                (m_workerFault is null) &&
                ((m_submittedSteps - m_completedSteps) >= m_maximumPendingSteps)
            ) {
                if (!backpressured) {
                    backpressured = true;

                    if (m_backpressureEvents < long.MaxValue) {
                        ++m_backpressureEvents;
                    }
                }

                Monitor.Wait(obj: m_workLock);
            }

            if (
                !m_acceptingWork ||
                (m_workerFault is not null)
            ) {
                return QueuedMachineSubmission.Rejected;
            }

            m_work.Enqueue(item: GroupWorkItem.Step(
                deltaTicks: deltaTicks,
                forceStage: forceStage,
                inputs: pads
            ));
            ++m_submittedSteps;
            Monitor.Pulse(obj: m_workLock);
        }

        return (backpressured
            ? QueuedMachineSubmission.AcceptedAfterBackpressure
            : QueuedMachineSubmission.Accepted
        );
    }
    // Each member publishes through its OWN worker's surfaces, so a linked machine's framebuffer, audio ring, feedback,
    // and step count stay the same objects a host already reads.
    private void PublishMembers(bool forceStage) {
        foreach (var worker in m_workers) {
            worker.PublishLentStep(forceStage: forceStage);
        }
    }
    private void StartWorker() {
        lock (m_workLock) {
            m_work.Clear();
            m_acceptingWork = true;
            m_workerFault = null;
        }

        m_worker = new Thread(start: WorkerLoop) {
            IsBackground = true,
            Name = m_workerName,
        };
        m_worker.Start();
    }
    private void StopWorker() {
        var worker = m_worker;

        if (worker is null) {
            return;
        }

        using var completion = new ManualResetEventSlim(initialState: false);
        var queued = false;

        lock (m_workLock) {
            m_acceptingWork = false;
            Monitor.PulseAll(obj: m_workLock);

            if (m_workerFault is null) {
                m_work.Enqueue(item: GroupWorkItem.Stop(completion: completion));
                Monitor.Pulse(obj: m_workLock);
                queued = true;
            }
        }

        if (queued) {
            completion.Wait();
        }

        worker.Join();
        m_worker = null;
    }
    // Consume a tick budget against the exact integer accumulator and return the cycle budget it buys under the medium's
    // current rate — ONE conversion for the whole group, so every member advances by identical wall time.
    private ulong TakeCycleBudget(ulong ticks) {
        var scaled = checked(((ticks * m_core.CyclesPerSecond) + m_cycleRemainder));

        m_cycleRemainder = (scaled % EngineTicks.PerSecond);

        return (scaled / EngineTicks.PerSecond);
    }
    private GroupWorkItem TakeWork() {
        lock (m_workLock) {
            while (m_work.Count == 0) {
                Monitor.Wait(obj: m_workLock);
            }

            return m_work.Dequeue();
        }
    }
    private void ThrowIfFaulted() {
        lock (m_workLock) {
            ThrowIfFaultedLocked();
        }
    }
    private void ThrowIfFaultedLocked() {
        if (m_workerFault is { } fault) {
            throw new InvalidOperationException(
                innerException: fault,
                message: $"The {m_workerName} link thread faulted."
            );
        }
    }
    private void WorkerLoop() {
        var current = default(GroupWorkItem);

        try {
            while (true) {
                current = TakeWork();

                switch (current.Kind) {
                    case GroupWorkKind.Step:
                        var inputs = current.Inputs;
                        var factor = m_timeTravel.FastForwardFactor;

                        for (var repeat = 0; (repeat < factor); ++repeat) {
                            var budget = checked((long)TakeCycleBudget(ticks: current.DeltaTicks));

                            m_core.ApplyInput(input: in inputs);
                            m_core.RunCycles(cycles: budget);
                            m_timeTravel.Record(
                                budget: budget,
                                hostAccumulator: m_cycleRemainder,
                                input: in inputs
                            );
                        }

                        PublishMembers(forceStage: current.ForceStage);

                        lock (m_workLock) {
                            ++m_completedSteps;
                            Monitor.PulseAll(obj: m_workLock);
                        }

                        break;
                    case GroupWorkKind.Invoke:
                        current.Invoke!();
                        current.Completion!.Set();

                        break;
                    case GroupWorkKind.Barrier:
                        current.Completion!.Set();

                        break;
                    case GroupWorkKind.Stop:
                        current.Completion!.Set();

                        return;
                }
            }
        } catch (Exception exception) {
            current.Completion?.Set();

            lock (m_workLock) {
                m_workerFault = exception;
                m_acceptingWork = false;

                while (m_work.TryDequeue(result: out var abandoned)) {
                    abandoned.Completion?.Set();
                }

                Monitor.PulseAll(obj: m_workLock);
            }

            Console.Error.WriteLine(value: $"[{m_workerName}] link thread stopped ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private enum GroupWorkKind {
        Step,
        Invoke,
        Barrier,
        Stop,
    }
    private readonly record struct GroupWorkItem(
        GroupWorkKind Kind,
        ulong DeltaTicks,
        MachineLinkPads Inputs,
        bool ForceStage,
        Action? Invoke,
        ManualResetEventSlim? Completion
    ) {
        public static GroupWorkItem Barrier(ManualResetEventSlim completion) =>
            new(
                Completion: completion,
                DeltaTicks: 0UL,
                ForceStage: false,
                Inputs: default,
                Invoke: null,
                Kind: GroupWorkKind.Barrier
            );
        public static GroupWorkItem ForInvoke(Action work, ManualResetEventSlim completion) =>
            new(
                Completion: completion,
                DeltaTicks: 0UL,
                ForceStage: false,
                Inputs: default,
                Invoke: work,
                Kind: GroupWorkKind.Invoke
            );
        public static GroupWorkItem Step(ulong deltaTicks, in MachineLinkPads inputs, bool forceStage) =>
            new(
                Completion: null,
                DeltaTicks: deltaTicks,
                ForceStage: forceStage,
                Inputs: inputs,
                Invoke: null,
                Kind: GroupWorkKind.Step
            );
        public static GroupWorkItem Stop(ManualResetEventSlim completion) =>
            new(
                Completion: completion,
                DeltaTicks: 0UL,
                ForceStage: false,
                Inputs: default,
                Invoke: null,
                Kind: GroupWorkKind.Stop
            );
    }
}
