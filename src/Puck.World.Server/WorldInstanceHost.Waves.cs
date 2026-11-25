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
        // A row that stands below the attention its document steps at (Lapses): it takes no tick, banks the master
        // delta like a stepping row, and is settled once, in the first wave's name order, where its clocks pass over
        // the ticks it banked.
        public bool Lapsing;
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
        ReadAttention();
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
            var idleClientRow = ((Boot is { } boot) && IsIdleClientBoot(boot: boot) && !IsOccupied(instance: instance) && !PresentsScreens(instance: instance));
            row.Administrative = ((rateHz <= 0) || instance.IsPaused || idleClientRow);
            row.StepWidth = (row.Administrative ? 0UL : FixedTickConversion.PerRate(ratePerSecond: ((uint)rateHz)));
            row.Lapsing = (!row.Administrative && Lapses(instance: instance));
            row.Done = (row.Administrative || row.Lapsing);
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
    // Whether the row's instance is still the one running under its name: an earlier row's settle can stop it.
    // Whether every authority this host steps stands between ticks: none holds a tick that is prepared, executing or
    // held on a dependency. Only then may one authority read another's live definition and observation door.
    private bool NoTickPending() {
        if (m_stepThread.InFlight) {
            return false;
        }
        foreach (var instance in m_instances.Values) {
            if (instance.Server.Tick.PendingReason is not null) {
                return false;
            }
        }

        return true;
    }
    private bool StillRuns(WaveRow row) => (m_instances.TryGetValue(key: row.Instance.Name, value: out var current) && ReferenceEquals(objA: current, objB: row.Instance));
    // Whether the row still runs and may take another tick.
    private bool StillSteps(WaveRow row) => (StillRuns(row: row) && !row.Instance.Server.IsRetiring && !row.Instance.Server.Tick.ExecutionInterrupted);

    // Prepares one tick of every row that still owes one, in name order, and hands them over; false when none does. The
    // first wave also lists the paused and stopped rows, which are settled in their name order without a tick.
    private bool ComposeWave(WaitDeadline deadline) {
        m_wave.Clear();
        m_waveNext = 0;
        // No row holds a prepared tick yet, so each may read its neighbours' authorities here; once the first row below
        // is prepared, a later row's prepare may not (WorldAdjacencyFields).
        if (NoTickPending()) {
            for (var index = 0; (index < m_waveRowCount); index++) {
                var row = m_waveRows[index];

                // A lapsing row adopts its followed rows here too, in the first wave: the adoption is input, which its
                // settle applies and answers with a step.
                if (!row.Administrative && (!row.Done || (row.Lapsing && m_firstWave)) && StillSteps(row: row)) {
                    row.Instance.Server.ResolveAdjacencyNeighbours();
                }
            }
        }
        for (var index = 0; (index < m_waveRowCount); index++) {
            var row = m_waveRows[index];

            row.Slot = -1;
            row.Executed = default;
            row.Fault = null;
            if (row.Administrative || row.Lapsing) {
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
            _ = SettleWave();
            m_firstWave = false;

            return ComposeWave(deadline: deadline);
        }
        m_stepThread.HandOver();

        return true;
    }
    // A paused or stopped row still applies its buffered document mutations, and its sessions still follow its
    // definition but forward nothing.
    private void SettleIdle(WaveRow row) {
        _ = row.Instance.Server.DrainAdministrative();
        SettleScreenSessions(instance: row.Instance, stepped: false);
    }
    // A lapsing row's host step: its clocks pass over every whole tick it has banked (WorldTick.Lapse), leaving the
    // remainder banked, so it keeps its phase against the master timeline and steps on its own schedule the moment
    // attention returns; then it settles as a paused row does. Anything that settle applied, and any input queued for
    // the row, owes it one step (WorldInstance.StepOwed), so its rules see what entered while nobody looked.
    private void SettleLapsed(WaveRow row) {
        var instance = row.Instance;
        var ticks = (instance.ScheduleAccumulatorTicks / row.StepWidth);

        if (ticks != 0UL) {
            var engineTicks = instance.Server.Lapse(ticks: ticks);

            if (engineTicks != 0UL) {
                instance.ScheduleAccumulatorTicks -= engineTicks;
                instance.ElapsedEngineTicks += engineTicks;
                instance.LapsedTicks += ticks;
                instance.PublishTick(obj: instance.CompletedTicks);
            }
        }
        if (instance.Server.DrainAdministrative() || instance.Server.HasQueuedIntents) { instance.StepOwed = true; }
        SettleScreenSessions(instance: instance, stepped: false);
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
            if (row.Lapsing) {
                if (StillSteps(row: row)) { SettleLapsed(row: row); }

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
                instance.StepOwed = false;
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
