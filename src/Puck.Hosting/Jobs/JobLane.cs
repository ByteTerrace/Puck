using System.Runtime.ExceptionServices;
using Puck.Abstractions.Waiting;

namespace Puck.Hosting.Jobs;

/// <summary>The work a <see cref="JobLane"/> runs: one unit, on the lane's thread, while the thread that posted it
/// either waits for it or goes on with work that touches nothing the unit does.</summary>
public interface IJobLaneUnit {
    /// <summary>Runs the unit on the lane's thread. What it throws is rethrown to the posting thread when that thread
    /// collects the unit.</summary>
    [Replaces(root: typeof(object))]
    void Run();
}

/// <summary>
/// A thread that runs one unit at a time for one owner: the owner posts a unit (<see cref="Post"/>), the lane's thread
/// runs it, and the owner collects it (<see cref="Collect"/>) before it posts the next. Nothing queues: a lane holds
/// at most one unit, so what the unit reads and writes is the owner's to arrange around that one hand-off.
/// <para>
/// The unit runs in the execution context its owner posted it from, so every ambient value that flows with a logical
/// call (an <see cref="AsyncLocal{T}"/>) reads on the lane's thread as it did on the owner's. State bound to the
/// thread itself (a thread-static, the wait door's pump registration) does not cross, and is the unit's to carry.
/// </para>
/// <para>
/// The lane's thread starts with the first unit posted, idles between units as a queue's consumer
/// (<see cref="HostWaits.ClaimConsumer"/>), and never spins. The owner's wait for its unit is the wait door's
/// delegated form (<see cref="HostWaits.OnDelegatedWork"/>): the unit is the owner's own work, so the wait is not
/// charged to a pump's allowance and the deadline the owner passes, the unit's own stall deadline, is its only bound.
/// </para>
/// </summary>
/// <remarks>Post and collect are one owner's, in turn; the lane's gate makes each of them safe against the lane's
/// thread, not against a second owner.</remarks>
public sealed class JobLane : IDisposable {
    private static readonly WaitReason IdleWait = new(Subsystem: "jobs", WaitsFor: "a lane's next unit");
    private static readonly WaitReason StartWait = new(Subsystem: "jobs", WaitsFor: "a lane's thread to start");
    private static readonly WaitReason StopWait = new(Subsystem: "jobs", WaitsFor: "a lane's thread to end");

    private enum Stage {
        Idle,
        Posted,
        Running,
        Finished,
    }

    // A monitor rather than a System.Threading.Lock, which has no Monitor.Wait.
    private readonly object m_gate = new();
    private readonly string m_name;

    private WaitConsumer? m_consumer;
    private ExecutionContext? m_context;
    private ExceptionDispatchInfo? m_fault;
    private Stage m_stage;
    private bool m_stopping;
    private Thread? m_thread;
    private IJobLaneUnit? m_unit;

    /// <summary>Initializes a new instance of the <see cref="JobLane"/> class. No thread starts until a unit is
    /// posted.</summary>
    /// <param name="name">The lane thread's diagnostic name.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    public JobLane(string name) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);

        m_name = name;
    }

    /// <summary>Gets whether the lane holds a unit its owner has not collected: posted, running, or finished and
    /// waiting for <see cref="Collect"/>.</summary>
    public bool InFlight {
        get {
            lock (m_gate) {
                return (m_stage != Stage.Idle);
            }
        }
    }

    /// <summary>Hands <paramref name="unit"/> to the lane's thread and returns at once. The unit is in flight until
    /// <see cref="Collect"/> answers <see cref="WaitOutcome.Completed"/> or rethrows what the unit threw.</summary>
    /// <param name="unit">The unit to run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="unit"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The lane already holds a unit.</exception>
    /// <exception cref="ObjectDisposedException">The lane has been stopped.</exception>
    /// <exception cref="TimeoutException">The lane's thread did not start within the synchronous budget.</exception>
    [Replaces(root: typeof(object))]
    public void Post(IJobLaneUnit unit) {
        ArgumentNullException.ThrowIfNull(argument: unit);

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_stopping, instance: this);

            if (m_stage != Stage.Idle) {
                throw new InvalidOperationException(message: $"The lane '{m_name}' already holds a unit; collect it before posting the next.");
            }
            if (m_thread is null) {
                m_thread = new Thread(start: Work) {
                    IsBackground = true,
                    Name = m_name,
                };
                m_thread.Start();
            }

            // The first post waits for the lane's thread to publish its consumer role, once in the lane's life, so
            // every wait for a unit is the delegated form.
            var started = WaitDeadline.Synchronous();

            while (m_consumer is null) {
                if (HostWaits.OnCondition(deadline: started, monitor: m_gate, reason: in StartWait) == WaitOutcome.DeadlinePassed) {
                    HostWaits.ThrowIfPending(pending: ((m_consumer is null) ? StartWait : null));
                }
            }

            m_context = ExecutionContext.Capture();
            m_unit = unit;
            m_stage = Stage.Posted;
            Monitor.PulseAll(obj: m_gate);
        }
    }
    /// <summary>Waits for the unit in flight to finish and takes it from the lane, which is then free for the next.
    /// The wait is the owner's own work on the lane's thread, so it is not charged to a pump's allowance.</summary>
    /// <param name="reason">Why the owner waits.</param>
    /// <param name="deadline">When the wait gives up: the unit's own stall deadline. A deadline already passed
    /// polls.</param>
    /// <returns><see cref="WaitOutcome.Completed"/> once the unit has run; <see cref="WaitOutcome.DeadlinePassed"/>
    /// while it is still in flight, when the owner calls again later.</returns>
    /// <exception cref="InvalidOperationException">The lane holds no unit.</exception>
    /// <remarks>What the unit threw is rethrown here, on the owner's thread, with its original stack; the lane is free
    /// for the next unit either way.</remarks>
    [Replaces(root: typeof(object))]
    public WaitOutcome Collect(in WaitReason reason, WaitDeadline deadline) {
        ExceptionDispatchInfo? fault;

        lock (m_gate) {
            if (m_stage == Stage.Idle) {
                throw new InvalidOperationException(message: $"The lane '{m_name}' holds no unit to collect.");
            }

            while (m_stage != Stage.Finished) {
                if (
                    (HostWaits.OnDelegatedWork(deadline: deadline, monitor: m_gate, reason: in reason, worker: m_consumer!) == WaitOutcome.DeadlinePassed) &&
                    (m_stage != Stage.Finished)
                ) {
                    return WaitOutcome.DeadlinePassed;
                }
            }

            fault = m_fault;
            m_fault = null;
            m_stage = Stage.Idle;
        }

        fault?.Throw();

        return WaitOutcome.Completed;
    }
    /// <summary>Stops the lane: it accepts no further unit, its thread finishes the unit it holds and ends, and the
    /// call waits for that.</summary>
    /// <param name="deadline">How long to wait for the lane's thread to end.</param>
    /// <returns><see langword="true"/> when the lane's thread has ended or never started;
    /// <see langword="false"/> when a unit is still running at the deadline, in which case whatever the unit uses must
    /// outlive it.</returns>
    public bool Stop(WaitDeadline deadline) {
        Thread? thread;

        lock (m_gate) {
            m_stopping = true;
            thread = m_thread;
            Monitor.PulseAll(obj: m_gate);
        }

        return ((thread is null) || (HostWaits.Join(deadline: deadline, reason: in StopWait, thread: thread) == WaitOutcome.Completed));
    }
    /// <summary>Stops the lane (<see cref="Stop"/>) under the synchronous budget. A unit still running then is left
    /// with its thread.</summary>
    public void Dispose() => _ = Stop(deadline: WaitDeadline.Synchronous());

    private void Work() {
        var consumer = HostWaits.ClaimConsumer(reason: in IdleWait);

        lock (m_gate) {
            m_consumer = consumer;
            Monitor.PulseAll(obj: m_gate);
        }

        while (true) {
            ExecutionContext? context;
            IJobLaneUnit unit;

            lock (m_gate) {
                while ((m_stage != Stage.Posted) && !m_stopping) {
                    HostWaits.IdleOnCondition(consumer: consumer, monitor: m_gate);
                }
                if (m_stage != Stage.Posted) {
                    return;
                }

                context = m_context;
                unit = m_unit!;
                m_context = null;
                m_unit = null;
                m_stage = Stage.Running;
            }

            ExceptionDispatchInfo? fault = null;

            try {
                if (context is null) {
                    unit.Run();
                } else {
                    ExecutionContext.Run(callback: static state => ((IJobLaneUnit)state!).Run(), executionContext: context, state: unit);
                }
            } catch (Exception exception) {
                fault = ExceptionDispatchInfo.Capture(source: exception);
            }

            lock (m_gate) {
                m_fault = fault;
                m_stage = Stage.Finished;
                Monitor.PulseAll(obj: m_gate);
            }
        }
    }
}
