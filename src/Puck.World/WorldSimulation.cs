using System.Diagnostics;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Addons;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The world's fixed-step shell composing the client and server halves over the loopback. Launcher owns time
/// and snapshots; this type only consumes one exact tick at a time: the client submits its seats' device intents, the
/// authoritative <see cref="WorldServer"/> steps (the mounted addon guests at its three pinned points → buffered
/// protocol traffic → every body, INCLUDING every booted screen machine (<c>Server.WorldMachineHost.Advance</c>)
/// → the tick's snapshot, delivered to the client synchronously), then
/// the client-side post-step (the per-tick analog clear).</summary>
internal sealed class WorldSimulation(WorldServer server, WorldClient client, WorldAddonRuntime addons, WorldSeatBindings seatBindings, WorldSeatAuthorityRouter seatRouter, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldCaptureScheduler captureScheduler, WorldPeerHost peerHost, WorldPerceptionAnchor anchor, WorldInstanceHost instances, WorldViewComposer composer) : IFixedStepSimulation, IWorldSimulationClock {
    private const ulong TimingReportInterval = 60UL;

    private ulong m_timingSamples;
    private SimulationTiming m_timingWorst;

    private readonly WorldServer m_server = server;
    private readonly WorldClient m_client = client;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly WorldSeatAuthorityRouter m_seatRouter = seatRouter;
    private readonly WorldReplayTape m_replayTape = replayTape;
    private readonly WorldConsoleWaitGate m_waitGate = waitGate;
    private readonly WorldPeerHost m_peerHost = peerHost;
    private readonly WorldPerceptionAnchor m_anchor = anchor;
    private readonly WorldInstanceHost m_instances = instances;
    private readonly WorldViewComposer m_composer = composer;

    /// <summary>The mounted addon runtime this shell holds — never called from here: the addon principals tick INSIDE
    /// <see cref="WorldServer.Step"/>, at its own three pinned points. It is a CONSTRUCTOR DEPENDENCY so that DI
    /// materializes it, because constructing it is what mounts every guest and attaches it to the server (see
    /// <see cref="WorldAddonRuntime.Create"/>) — dropping the parameter would leave the singleton unresolved and the
    /// world silently addon-less.</summary>
    public WorldAddonRuntime Addons { get; } = addons;

    // Monotonic host work coordinates keep console waits independent of an authority timeline restored by replay.
    private ulong m_completedHostSteps;
    private ulong m_completedHostEngineTicks;
    private readonly Action<ulong> m_publishStep = tick => {
        waitGate.PublishTick(tick);
        captureScheduler.PublishTick(server.NextInputTick - 1UL);
    };

    /// <summary>The exact engine time completed on the current authority timeline.</summary>
    public ulong ElapsedTicks => m_server.CompletedEngineTicks;
    /// <inheritdoc/>
    public uint RatePerSecond => ((uint)m_server.Definition.SimulationRateHz);
    /// <summary>The number of fixed ticks completed on the current authority timeline.</summary>
    public ulong Tick => m_server.NextInputTick - 1UL;

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

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) {
        var timingEnabled = GpuTimingControl.Shared.Armed;

        // Computed first, before intents are submitted below: whether boot will actually step THIS call decides
        // whether its seats' input is consumed now or held untouched. context.StepTicks is threaded in so
        // ShouldStepBoot can refuse a call whose pump-supplied width no longer matches boot's current rate.
        var stepsBoot = m_instances.ShouldStepBoot(stepTicks: context.StepTicks);

        var phaseStart = (timingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );

        // Seat intents are simulation input, submitted only when boot will actually consume them this call. A
        // paused/rate-0 boot world behaves as if no ticks existed: held seat input is never buffered into the
        // server's intent queue, so a resume never drains an accumulated backlog into one step. tick: (Tick + 1UL)
        // uses this shell's own contiguous next-tick coordinate (frozen below while boot does not step), matching
        // what WorldServerStepShell.Step is about to report as the completed tick — never context.Tick + 1, the
        // pump's raw un-frozen cursor (see the bootContext remarks below).
        m_instances.PrepareBootSeatIntents(
            stepsBoot: stepsBoot,
            tick: (Tick + 1UL),
            stepTicks: context.StepTicks
        );

        var rosterTicks = (timingEnabled
            ? (Stopwatch.GetTimestamp() - phaseStart)
            : 0L
        );

        phaseStart = (timingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );

        // The host-level pending-transfer FIFO's one fixed drain point — before either the boot instance or any
        // other instance steps this tick, mirroring where WorldServer.DrainPendingOps sits relative to the rest of
        // WorldServer.Step. A transfer is a host act, so it settles at this host's one fixed point rather than
        // inline with whichever instance's step produced it.
        m_instances.DrainPendingTransfers();
        m_instances.SubmitExternallyClockedSeatIntents();

        // The boot instance steps by the same per-instance pause/rate-0 rule as every other instance. When it is
        // not due (a live world.rate pause, or an authored rateHz of 0), the tape/wait-gate/socket bookkeeping this
        // shell also carries is skipped right along with the step, but a buffered document mutation must still be
        // able to apply, hence the administrative drain on the else branch. The diegetic portal trigger's scan for
        // boot runs immediately after its own step, reading boot's own just-settled state — never when boot did not
        // step at all, so a paused/stopped boot's latched "inside" occupancy neither fires nor is re-evaluated
        // until a genuine resume produces a new edge.
        var stepTick = m_completedHostSteps;

        if (stepsBoot) {
            // Host work advances only when this authority steps. The server independently advances from its
            // checkpointed clock; restoring a replay timeline rewinds that clock without rewinding console waits.
            var bootContext = new FixedStepContext(
                ElapsedTicks: (m_completedHostEngineTicks + context.StepTicks),
                StepTicks: context.StepTicks,
                Tick: m_completedHostSteps
            );

            stepTick = WorldServerStepShell.Step(
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

        // Every world instance running beside the boot one (world.instance.start) advances on its own authored
        // schedule, folded into the same fixed-step call rather than a second pump. masterDeltaTicks is the host's
        // own per-call engine-time advance, never a second clock. Each instance's own portal faces are scanned
        // per-step inside this call — once per actual Server.Step, which can run several times here for a fast
        // instance, or zero times for a paused/rate-0 one — never once for the whole call regardless of how many
        // times an instance actually advanced.
        m_instances.StepInstances(masterDeltaTicks: context.StepTicks);

        // Reflect any applied world-binding-overlay mutation into the per-seat resolvers, then publish the seats'
        // context-family states and the perception anchor so a state change this tick applied (an engage, a
        // possession, a roster move) flips the context-derived binding group and swaps the seat's perceived body
        // the same tick. Each seat's binding vocabulary and state-backed control contexts resolve from its complete
        // authority route, never uniformly from boot. Cheap on an ordinary tick: SyncSeat compares the delivered
        // state, overlay, and channel-list references before doing any work.

        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            if (m_seatRouter.TryRoute(slot: slot) is { } route) {
                m_seatBindings.SyncSeat(
                    slot: slot,
                    definition: route.Endpoint.Definition,
                    entityIndex: route.EntityIndex,
                    nextInputTick: route.Endpoint.NextInputTick
                );
            }
        }

        WorldSeatContextSync.Publish(
            seatBindings: m_seatBindings,
            roster: m_client.Roster,
            grants: m_server.Grants,
            anchor: m_anchor,
            activeLayout: m_composer.ActiveLayoutName
        );

        // Machine stepping runs INSIDE WorldServerStepShell.Step (Server.WorldMachineHost.Advance, called from
        // WorldServer.Step right after WorldEngagement.FoldTick), so its cost is already folded into
        // populationTicks below; there is no separate phase to time here.
        var populationTicks = (timingEnabled
            ? (Stopwatch.GetTimestamp() - phaseStart)
            : 0L
        );

        phaseStart = (timingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );
        m_instances.FinishSeatIntents();

        var finishTicks = (timingEnabled
            ? (Stopwatch.GetTimestamp() - phaseStart)
            : 0L
        );

        if (timingEnabled) {
            ReportTiming(sample: new SimulationTiming(
                Tick: Tick,
                PopulationTicks: populationTicks,
                RosterTicks: rosterTicks,
                FinishTicks: finishTicks
            ));
        }
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
