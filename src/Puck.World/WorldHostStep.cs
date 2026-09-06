using System.Diagnostics;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The one fixed step every boot shape runs, in the one order — the windowed <see cref="WorldSimulation"/> and the
/// headless/offscreen <see cref="HeadlessWorldSimulation"/> hold this and contribute nothing to the sequence itself:
/// decide whether boot is due, submit its seats' intents, drain the host's pending transfers, step boot through
/// <see cref="WorldServerStepShell"/> (or drain an administrative mutation and release a stalled console wait when
/// it is paused), step every other instance, run the shell's post-step, then finish the seat intents. The host-work
/// clock a <c>world.wait</c> counts in lives here, as does the per-phase timing <c>world.timing</c> arms. Also the
/// <see cref="IWorldSimulationClock"/> a frame producer reads, in every shape.
/// </summary>
internal sealed class WorldHostStep(WorldServer server, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldCaptureScheduler captureScheduler, WorldPeerHost peerHost, WorldInstanceHost instances) : IWorldSimulationClock {
    private const ulong TimingReportInterval = 60UL;

    private readonly WorldServer m_server = server;
    private readonly WorldReplayTape m_replayTape = replayTape;
    private readonly WorldConsoleWaitGate m_waitGate = waitGate;
    private readonly WorldPeerHost m_peerHost = peerHost;
    private readonly WorldInstanceHost m_instances = instances;

    // Monotonic host work coordinates keep console waits independent of an authority timeline restored by replay.
    private ulong m_completedHostSteps;
    private ulong m_completedHostEngineTicks;
    private readonly Action<ulong> m_publishStep = tick => {
        waitGate.PublishTick(tick);
        captureScheduler.PublishTick(server.NextInputTick - 1UL);
    };

    private ulong m_timingSamples;
    private SimulationTiming m_timingWorst;

    /// <summary>The exact engine time completed on the current authority timeline.</summary>
    public ulong ElapsedTicks => m_server.CompletedEngineTicks;
    /// <summary>The authored simulation rate the fixed-step pump paces this world at.</summary>
    public uint RatePerSecond => ((uint)m_server.Definition.SimulationRateHz);
    /// <summary>The number of fixed ticks completed on the current authority timeline.</summary>
    public ulong Tick => m_server.NextInputTick - 1UL;

    /// <summary>Runs one fixed step.</summary>
    /// <param name="context">The pump's tick context; only its step width reaches the server.</param>
    /// <param name="afterInstances">The shell's own post-step, run after every instance has stepped and before the
    /// seat intents finish — the windowed shell syncs seat bindings and publishes seat context here; a headless
    /// shell passes <see langword="null"/>.</param>
    public void Step(in FixedStepContext context, Action? afterInstances) {
        var timingEnabled = GpuTimingControl.Shared.Armed;

        // Computed first, before intents are submitted below: whether boot will actually step THIS call decides
        // whether its seats' input is consumed now or held untouched. context.StepTicks is threaded in so
        // ShouldStepBoot can refuse a call whose pump-supplied width no longer matches boot's current rate.
        var stepsBoot = m_instances.ShouldStepBoot(stepTicks: context.StepTicks);
        var phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);

        // Seat intents are simulation input, submitted only when boot will actually consume them this call. A
        // paused/rate-0 boot world behaves as if no ticks existed: held seat input is never buffered into the
        // server's intent queue, so a resume never drains an accumulated backlog into one step. tick: (Tick + 1UL)
        // is this stepper's own contiguous next-tick coordinate (frozen while boot does not step), matching what
        // WorldServerStepShell.Step is about to report as the completed tick — never context.Tick + 1, the pump's
        // raw un-frozen cursor.
        m_instances.PrepareBootSeatIntents(
            stepsBoot: stepsBoot,
            tick: (Tick + 1UL),
            stepTicks: context.StepTicks
        );

        var rosterTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);
        phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);

        // The host-level pending-transfer FIFO's one fixed drain point — before either the boot instance or any
        // other instance steps this tick, mirroring where WorldServer.DrainPendingOps sits relative to the rest of
        // WorldServer.Step. A transfer is a host act, so it settles at this host's one fixed point rather than
        // inline with whichever instance's step produced it; a transfer drained here was enqueued by a per-step
        // portal scan during the PREVIOUS call, so it lands and is advanced exactly once, this same tick.
        m_instances.DrainPendingTransfers();
        m_instances.SubmitExternallyClockedSeatIntents();
        StepBoot(
            context: in context,
            stepsBoot: stepsBoot
        );

        // Every world instance running beside the boot one (world.instance.start) advances on its own authored
        // schedule, folded into the same fixed-step call rather than a second pump. masterDeltaTicks is the host's
        // own per-call engine-time advance, never a second clock. Each instance's own portal faces are scanned
        // per-step inside this call — once per actual Server.Step, which can run several times here for a fast
        // instance, or zero times for a paused/rate-0 one — never once for the whole call regardless of how many
        // times an instance actually advanced.
        m_instances.StepInstances(masterDeltaTicks: context.StepTicks);
        afterInstances?.Invoke();

        // Machine stepping runs INSIDE WorldServerStepShell.Step (Server.WorldMachineHost.Advance, called from
        // WorldServer.Step right after WorldEngagement.FoldTick), so its cost is already folded into this phase;
        // there is no separate phase to time.
        var populationTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);
        phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);
        m_instances.FinishSeatIntents();

        var finishTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

        if (timingEnabled) {
            ReportTiming(sample: new SimulationTiming(
                Tick: Tick,
                PopulationTicks: populationTicks,
                RosterTicks: rosterTicks,
                FinishTicks: finishTicks
            ));
        }
    }

    // The boot instance steps by the same per-instance pause/rate-0 rule as every other instance. When it is not
    // due, the tape/wait-gate/socket bookkeeping is skipped right along with the step, but a buffered document
    // mutation must still be able to apply, hence the administrative drain. The diegetic portal trigger's scan for
    // boot runs immediately after its own step, reading boot's own just-settled state — never when boot did not
    // step at all, so a paused/stopped boot's latched "inside" occupancy neither fires nor is re-evaluated until a
    // genuine resume produces a new edge.
    private void StepBoot(in FixedStepContext context, bool stepsBoot) {
        if (stepsBoot) {
            // Host work advances only when this authority steps. The server independently advances from its
            // checkpointed clock; restoring a replay timeline rewinds that clock without rewinding console waits.
            var bootContext = new FixedStepContext(
                ElapsedTicks: (m_completedHostEngineTicks + context.StepTicks),
                StepTicks: context.StepTicks,
                Tick: m_completedHostSteps
            );

            var stepTick = WorldServerStepShell.Step(
                context: in bootContext,
                publishTick: m_publishStep,
                server: m_server,
                tape: m_replayTape,
                peerHost: m_peerHost
            );
            m_instances.ScanBootBoundaryTriggers();
            // Count actual host work, including fast-forward bursts, but never paused pump calls.
            m_completedHostEngineTicks += ((stepTick - m_completedHostSteps) * context.StepTicks);
            m_completedHostSteps = stepTick;
        } else {
            _ = m_server.DrainAdministrative();

            // A world.wait armed before this pause landed can never see its release tick now that boot's own clock
            // is frozen (WorldConsoleWaitGate.PublishTick only ever fires from a step that actually ran) — release
            // it here rather than leave the held console stream, including the very world.rate resume that would
            // lift the pause, wedged behind a hold that will never clear on its own. A no-op (returns false) on
            // every ordinary paused/stopped tick once already released, so this costs nothing beyond one flag check.
            if (m_waitGate.ReleaseStalled()) {
                Console.Error.WriteLine(value: "[world.wait: released — the boot world stopped stepping (paused, rateHz 0, or a rate change the fixed-step pump has not caught up to yet) before its requested tick count was reached; resume it (world.rate resume) before arming a new wait]");
            }
        }
    }
    private void ReportTiming(SimulationTiming sample) {
        m_timingSamples++;

        if (sample.TotalTicks >= m_timingWorst.TotalTicks) {
            m_timingWorst = sample;
        }

        if (0UL != (m_timingSamples % TimingReportInterval)) {
            return;
        }

        var worst = m_timingWorst;
        var frequency = Stopwatch.Frequency;

        static double ToMs(long ticks, long frequency) =>
            ((((double)ticks) * 1000.0) / frequency);

        Console.Error.WriteLine(value: $"[frame-timing] world-simulation worst-of-{TimingReportInterval} tick {worst.Tick} total {ToMs(
            ticks: worst.TotalTicks,
            frequency: frequency
        ):0.000}ms | population {ToMs(
            ticks: worst.PopulationTicks,
            frequency: frequency
        ):0.000} | roster {ToMs(
            ticks: worst.RosterTicks,
            frequency: frequency
        ):0.000} | finish {ToMs(
            ticks: worst.FinishTicks,
            frequency: frequency
        ):0.000}");

        m_timingWorst = default;
    }

    private readonly record struct SimulationTiming(
        ulong Tick,
        long PopulationTicks,
        long RosterTicks,
        long FinishTicks
    ) {
        public long TotalTicks => ((PopulationTicks + RosterTicks) + FinishTicks);
    }
}
