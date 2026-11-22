using System.Runtime.ExceptionServices;
using Puck.Abstractions.Waiting;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    // Where the host step's instance waves stand between calls of TryStepInstances.
    private enum WavePhase : byte {
        // No host step is open: the next call banks the master delta and composes the first wave.
        Idle,
        // The next wave is composed from the rows that still owe a tick.
        Compose,
        // A wave is on the step thread.
        InFlight,
        // A wave is collected and its rows are settled in name order from m_waveNext.
        Collecting,
        // The wave's settling stopped at a held tick, which the next call hands back with that call's deadline.
        Held,
    }

    // One row's place in the open host step: whether it still owes a tick in it, and its tick in the current wave.
    private sealed class WaveRow {
        public WorldInstance Instance = null!;
        public int RateHz;
        public ulong StepWidth;
        // A paused or stopped row, which takes no tick and is settled once, in the first wave's name order.
        public bool Administrative;
        // The row owes no further tick this host step.
        public bool Done;
        // How many ticks a fast-forwarding replay drive has taken in this host step beyond the scheduled one.
        public int Burst;
        // Whether the row's tick in the current wave is a drive's fast-forward tick, which the schedule does not pay for.
        public bool FastForward;
        public FixedStepContext Context;
        // The row's slice in the step thread's wave, and how it ended once collected.
        public int Slot = -1;
        public WorldExecutedTick Executed;
        public ExceptionDispatchInfo? Fault;
    }

    // Every row the open host step considered, in ordinal name order, reused across host steps.
    private readonly List<WaveRow> m_waveRows = [];
    private int m_waveRowCount;
    // The rows of the current wave, in ordinal name order.
    private readonly List<WaveRow> m_wave = [];
    // The next row of the current wave to settle.
    private int m_waveNext;
    private bool m_firstWave;
    private WavePhase m_wavePhase;

    /// <summary>Advances every row but a desktop's boot row on the master tick, in waves: each wave prepares one tick of
    /// every row that still owes one, in ordinal name order, hands them to the step thread together, and settles them in
    /// that order once the wave is collected, each tick's portal scan and screen-session settle following its collect. A
    /// row owed several ticks this host step (a rate above the master's, or a fast-forwarding drive) takes them a wave
    /// each, so its next seat intents are composed after its last tick was collected.</summary>
    /// <remarks><para>The step thread may run a wave's ticks in parallel; each reads only input this thread closed
    /// before the hand-off, and a row reads its neighbours as of the previous collect, so nothing depends on which
    /// tick finishes first. Within a host step a row's tick runs before any earlier row is settled, so a row an earlier
    /// row's settle stops has already taken its tick (nothing collects it), and input one row forwards to another from
    /// its settle reaches that row's tick in the next host step.</para>
    /// <para>A tick held on a dependency stops the settling at its row, in name order; the next call hands the held
    /// ticks back to the step thread and settles on from there once they complete.</para></remarks>
    /// <param name="masterDeltaTicks">The host's master timeline advance for this host step.</param>
    /// <param name="deadline">How long each tick's own waits may block in this call.</param>
    /// <param name="collectBy">How long the calling thread waits for the step thread before it leaves a wave in
    /// flight and answers <see cref="WorldStepThread.ExecuteWait"/>; <see langword="null"/> waits for each wave.</param>
    /// <returns>The dependency the host step still waits for, or <see langword="null"/> once every row has
    /// stepped.</returns>
    public WaitReason? TryStepInstances(ulong masterDeltaTicks, WaitDeadline deadline, WaitDeadline? collectBy = null) {
        if (m_wavePhase == WavePhase.Idle) {
            BeginInstanceWaves(masterDeltaTicks: masterDeltaTicks);
        }

        while (true) {
            if (m_wavePhase == WavePhase.Held) {
                m_wavePhase = (ResumeHeld(deadline: deadline) ? WavePhase.InFlight : WavePhase.Collecting);
            }
            if (m_wavePhase == WavePhase.Compose) {
                if (!ComposeWave(deadline: deadline)) {
                    EndInstanceWaves();

                    return null;
                }
                m_wavePhase = WavePhase.InFlight;
            }
            if (m_wavePhase == WavePhase.InFlight) {
                if (!m_stepThread.TryCollect(collectBy: (collectBy ?? WaitDeadline.Synchronous()))) {
                    return WorldStepThread.ExecuteWait;
                }
                TakeWaveResults();
                m_wavePhase = WavePhase.Collecting;
            }
            if (SettleWave() is { } held) {
                // The held ticks resume in the next call, with that call's deadline for their waits.
                m_wavePhase = WavePhase.Held;

                return held;
            }
            m_firstWave = false;
            m_wavePhase = WavePhase.Compose;
        }
    }

    // Opens a host step: banks the master delta into every steppable row and lists the rows in name order.
    private void BeginInstanceWaves(ulong masterDeltaTicks) {
        m_waveRowCount = 0;
        foreach (var name in Names) {
            if (
                string.Equals(a: name, b: BootInstanceName, comparisonType: StringComparison.Ordinal) ||
                !m_instances.TryGetValue(key: name, value: out var instance) ||
                // A restored row held pending its adjacency mirrors is not yet part of the stepping engine; retirement is
                // a permanent hold, and a frozen authority steps no further.
                instance.AwaitingMirrors || instance.Server.IsRetiring || instance.Server.Tick.ExecutionInterrupted
            ) {
                continue;
            }

            var rateHz = instance.Server.Configuration.SimulationRateHz;

            if (m_waveRowCount == m_waveRows.Count) {
                m_waveRows.Add(item: new WaveRow());
            }

            var row = m_waveRows[m_waveRowCount++];

            row.Instance = instance;
            row.RateHz = rateHz;
            row.Administrative = ((rateHz <= 0) || instance.IsPaused);
            row.StepWidth = (row.Administrative ? 0UL : FixedTickConversion.PerRate(ratePerSecond: ((uint)rateHz)));
            row.Done = row.Administrative;
            row.Burst = 0;
            row.FastForward = false;
            row.Slot = -1;
            row.Executed = default;
            row.Fault = null;
            // A paused or stopped row banks nothing, so a resume continues on the identical schedule.
            if (!row.Administrative) { instance.ScheduleAccumulatorTicks += masterDeltaTicks; }
        }
        m_firstWave = true;
        m_wavePhase = WavePhase.Compose;
    }
    private void EndInstanceWaves() {
        for (var index = 0; (index < m_waveRowCount); index++) {
            m_waveRows[index].Instance = null!;
            m_waveRows[index].Fault = null;
        }
        m_waveRowCount = 0;
        m_wave.Clear();
        m_waveNext = 0;
        m_wavePhase = WavePhase.Idle;
    }
    // Whether the row's instance is still the one running under its name, and may still step.
    private bool StillSteps(WaveRow row) => (
        m_instances.TryGetValue(key: row.Instance.Name, value: out var current) &&
        ReferenceEquals(objA: current, objB: row.Instance) &&
        !row.Instance.Server.IsRetiring &&
        !row.Instance.Server.Tick.ExecutionInterrupted
    );

    // Prepares one tick of every row that still owes one, in name order, and hands them over; false when none does. The
    // first wave also lists the paused and stopped rows, which are settled in their name order without a tick.
    private bool ComposeWave(WaitDeadline deadline) {
        m_wave.Clear();
        m_waveNext = 0;
        for (var index = 0; (index < m_waveRowCount); index++) {
            var row = m_waveRows[index];

            row.Slot = -1;
            row.Executed = default;
            row.Fault = null;
            if (row.Administrative) {
                if (m_firstWave) { m_wave.Add(item: row); }

                continue;
            }
            if (row.Done) {
                continue;
            }

            var instance = row.Instance;

            if (!StillSteps(row: row) || (!row.FastForward && (instance.ScheduleAccumulatorTicks < row.StepWidth))) {
                row.Done = true;

                continue;
            }

            // Elapsed time is accumulated, never re-derived from (tick + 1) * width, which breaks the instant this row's
            // own rate changes (WorldInstance.ElapsedEngineTicks).
            row.Context = new FixedStepContext(
                ElapsedTicks: (instance.ElapsedEngineTicks + row.StepWidth),
                StepTicks: row.StepWidth,
                Tick: instance.CompletedTicks
            );
            // A drive's fast-forward tick replays recorded input; the seats submit theirs once a scheduled tick.
            if (!row.FastForward) {
                m_seats.SubmitAuthorityIntents(endpoint: EndpointFor(instance: instance), tick: (row.Context.Tick + 1UL));
            }
            WorldNarrationScope.Current = instance.Name;
            try {
                row.Slot = m_stepThread.Prepare(context: in row.Context, deadline: deadline, narration: instance.Name, server: instance.Server, tape: instance.Tape);
            } finally {
                WorldNarrationScope.Current = null;
            }
            if (row.Slot < 0) {
                // Preparing cancelled the row's replay drive: the scheduled tick is spent, and the row steps no further.
                if (!row.FastForward) { instance.ScheduleAccumulatorTicks -= row.StepWidth; }
                row.Done = true;

                continue;
            }
            m_wave.Add(item: row);
        }

        if (m_stepThread.Count == 0) {
            // Nothing to execute: a first wave of paused rows alone still settles them.
            if (m_wave.Count == 0) {
                return false;
            }
            SettleAdministrative();

            return ComposeWave(deadline: deadline);
        }
        m_stepThread.HandOver();

        return true;
    }
    // Settles the paused and stopped rows of a first wave that executed nothing.
    private void SettleAdministrative() {
        foreach (var row in m_wave) {
            if (row.Administrative && StillRuns(row: row)) {
                SettleIdle(row: row);
            }
        }
        m_firstWave = false;
    }
    private bool StillRuns(WaveRow row) => (m_instances.TryGetValue(key: row.Instance.Name, value: out var current) && ReferenceEquals(objA: current, objB: row.Instance));
    // A paused or stopped row still applies its buffered document mutations, and its sessions still follow its
    // definition but forward nothing.
    private void SettleIdle(WaveRow row) {
        _ = row.Instance.Server.DrainAdministrative();
        SettleScreenSessions(instance: row.Instance, stepped: false);
    }
    // Copies how each slice of the collected wave ended into its row, so a row settled after a held one keeps it.
    private void TakeWaveResults() {
        foreach (var row in m_wave) {
            if (row.Slot < 0) {
                continue;
            }
            try {
                row.Executed = m_stepThread.Result(index: row.Slot);
                row.Fault = null;
            } catch (Exception error) {
                row.Executed = default;
                row.Fault = ExceptionDispatchInfo.Capture(source: error);
            }
            row.Slot = -1;
        }
    }
    // Settles the collected wave's rows in name order from m_waveNext; the dependency of the first row whose tick is
    // held, or null once every row is settled.
    private WaitReason? SettleWave() {
        for (; (m_waveNext < m_wave.Count); m_waveNext++) {
            var row = m_wave[m_waveNext];
            var instance = row.Instance;

            if (row.Administrative) {
                if (StillRuns(row: row)) { SettleIdle(row: row); }

                continue;
            }

            WorldNarrationScope.Current = instance.Name;
            try {
                if (row.Fault is { } fault) {
                    row.Fault = null;
                    row.Done = true;
                    try {
                        fault.Throw();
                    } catch (TimeoutException error) when (instance.Server.Tick.ExecutionInterrupted) {
                        instance.Server.Output.Narrate("world.instance", $"[world.instance: '{instance.Name}' execution interrupted; recover from its last checkpoint: {error.Message}]");
                    }

                    continue;
                }
                if (row.Executed.Pending is { } reason) {
                    if (instance.Server.Tick.ExecutionInterrupted || !StillRuns(row: row)) {
                        // A held tick that froze, or whose row an earlier settle stopped, never completes.
                        row.Done = true;

                        continue;
                    }

                    return reason;
                }
                // A row an earlier row's settle stopped already took its tick; nothing collects it.
                if (!StillRuns(row: row)) {
                    row.Done = true;

                    continue;
                }
                if (!row.FastForward) { instance.ScheduleAccumulatorTicks -= row.StepWidth; }
                if (!row.Executed.Completed) {
                    // A drive cancelled while its tick ran stepped nothing.
                    row.Done = true;

                    continue;
                }

                WorldServerStepShell.Collect(completedAt: row.Executed.CompletedAt, publishTick: instance.PublishTick, server: instance.Server, tick: (row.Context.Tick + 1UL));
                // The tick's poses are delivered: a seat routed to this row presents the look step the tick carried.
                if (m_authorityEndpoints.TryGetValue(key: instance.Server.InstanceIdentity, value: out var endpoint)) {
                    m_seats.PresentSeatLook(endpoint: endpoint);
                }
                instance.ElapsedEngineTicks = row.Context.ElapsedTicks;
                row.FastForward = (
                    (instance.Tape is { WantsFastForwardStep: true } tape) &&
                    !m_pacing.StepsOneTickPerFrame &&
                    (++row.Burst < tape.FastForwardBurst)
                );
                ScanInstanceBoundaries(instance: instance);
                SettleScreenSessions(instance: instance, stepped: true);

                // An install (world.load/.reset/.reload) that changed the row's rate makes its width stale, and closing an
                // unreachable cycle of screen sessions can reap the row: either ends its ticks this host step, and the
                // leftover accumulator carries over to the next, which reads the fresh rate.
                if (instance.Server.IsRetiring || (instance.Server.Configuration.SimulationRateHz != row.RateHz)) {
                    row.Done = true;
                }
            } finally {
                WorldNarrationScope.Current = null;
            }
        }

        return null;
    }
    // Hands the held ticks of the current wave from m_waveNext back to the step thread; false when none is left to hand.
    private bool ResumeHeld(WaitDeadline deadline) {
        for (var index = m_waveNext; (index < m_wave.Count); index++) {
            var row = m_wave[index];

            if (row.Administrative || (row.Executed.Pending is null) || !StillRuns(row: row)) {
                continue;
            }
            WorldNarrationScope.Current = row.Instance.Name;
            try {
                row.Slot = m_stepThread.Prepare(context: in row.Context, deadline: deadline, narration: row.Instance.Name, server: row.Instance.Server, tape: row.Instance.Tape);
            } finally {
                WorldNarrationScope.Current = null;
            }
            if (row.Slot < 0) {
                row.Executed = default;
                row.Done = true;
            }
        }
        if (m_stepThread.Count == 0) {
            return false;
        }
        m_stepThread.HandOver();

        return true;
    }
}
