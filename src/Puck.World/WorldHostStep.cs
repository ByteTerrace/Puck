using Puck.Abstractions.Waiting;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The fixed step every boot shape runs: seat route changes, boot scheduling, authority and instance steps,
/// the composition's post-step, then seat intent completion. The host-work clock a world.wait counts lives here.
/// </summary>
internal sealed class WorldHostStep(WorldServer server, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldCaptureScheduler captureScheduler, WorldTestRun scheduleRunner, WorldTickScript tickScript, WorldInstanceHost instances, WorldServiceExtensions extensions, WorldSeatAuthorityRouter seatRouter, PlayerRoster roster, WorldSourceWatch sourceWatch, WorldReload reload, HostPacing pacing) {
    private readonly WorldCaptureScheduler m_captureScheduler = captureScheduler;
    private readonly WorldServer m_server = server;
    private readonly WorldReplayTape m_replayTape = replayTape;
    private readonly WorldConsoleWaitGate m_waitGate = waitGate;
    private readonly WorldInstanceHost m_instances = instances;
    private readonly WorldSeatAuthorityRouter m_seatRouter = seatRouter;
    // Test exports publish LAST, before world.at drains this tick's lines: captures and exports both describe
    // the completed state, never a half-applied mixture of this tick's submissions.
    private readonly Action<ulong> m_publishStep = tick => {
        waitGate.PublishTick(tick: tick);
        captureScheduler.PublishTick(tick: (server.NextInputTick - 1UL));
        tickScript.PublishTick(tick: (server.NextInputTick - 1UL));
        scheduleRunner.PublishTick(tick: (server.NextInputTick - 1UL));
    };

    private ulong m_completedHostEngineTicks;
    // Monotonic host work coordinates keep console waits independent of an authority timeline restored by replay.
    private ulong m_completedHostSteps;

    /// <summary>Gets whether a capture armed at the tick just completed still waits for the frame that shows it,
    /// so the host composes one before its next step.</summary>
    public bool AwaitsFrame => m_captureScheduler.AwaitsFrame;

    /// <summary>Whether a host that holds its clock withholds its next step: while a capture armed at the tick just
    /// completed is neither served nor refused (<see cref="WorldCaptureScheduler.HoldsClock"/>).</summary>
    /// <param name="withheldTicks">The host time the host withholds when the answer is <see langword="true"/>.</param>
    /// <returns><see langword="true"/> to withhold the step.</returns>
    public bool HoldsClock(ulong withheldTicks) => m_captureScheduler.HoldsClock(withheldTicks: withheldTicks);
    /// <summary>Decides the capture still owed a frame as the run ends, before the host disposes its render chain
    /// (<see cref="WorldCaptureScheduler.Drain"/>).</summary>
    public void SettleOwedFrames() => m_captureScheduler.Drain();

    // What the host loop reads of the boot authority outside its boundary window, where it may not read the authority:
    // published each time the window closes with no tick in flight (WorldLinkBoundary.PublishOnClose), so the loop
    // reads the values of the boundary it last stood at. A reader that may read the authority (a thread no pump is
    // registered on, or the pump inside its window) refreshes them as it reads.
    private uint m_publishedRate;
    private bool m_publishedInterrupted;
    private bool m_publishes;

    private void Publish() {
        if (!m_publishes) {
            m_publishes = true;
            m_instances.Boundary.PublishOnClose(publish: Publish);
        }
        if ((m_instances.Boundary.Refusal != WorldBoundaryRefusal.None) || m_instances.StepInFlight) { return; }
        m_publishedRate = ((uint)m_server.Configuration.SimulationRateHz);
        m_publishedInterrupted = ((m_phase == StepPhase.Boot) && m_server.Tick.ExecutionInterrupted);
    }

    /// <summary>The authored simulation rate the fixed-step pump paces this world at: the boot authority's own where the
    /// caller may read it, and otherwise the rate published as the host's boundary window last closed.</summary>
    public uint RatePerSecond {
        get {
            Publish();
            return m_publishedRate;
        }
    }
    /// <summary>Gets whether the next step's tick arms a scheduled capture, which only a frame produced once the tick
    /// is collected can serve: the host waits for that tick instead of producing beside it.</summary>
    public bool NextStepOwesFrame => m_captureScheduler.ArmsAt(tick: m_server.NextInputTick);
    /// <summary>The number of fixed ticks completed on the current authority timeline.</summary>
    public ulong Tick => (m_server.NextInputTick - 1UL);

    // The boot instance steps by the same per-instance pause/rate-0 rule as every other instance. When it is not
    // due, the tape/wait-gate/socket bookkeeping is skipped right along with the step, but a buffered document
    // mutation must still be able to apply, hence the administrative drain. The diegetic portal trigger's scan for
    // boot runs immediately after its own step, reading boot's own just-settled state — never when boot did not
    // step at all, so a paused/stopped boot's latched "inside" occupancy neither fires nor is re-evaluated until a
    // genuine resume produces a new edge.
    private bool StepBoot(in FixedStepContext context, bool stepsBoot, WaitDeadline deadline, WaitDeadline? collectBy) {
        if (stepsBoot) {
            // Host work advances only when this authority steps. The server independently advances from its
            // checkpointed clock; restoring a replay timeline rewinds that clock without rewinding console waits.
            var bootContext = new FixedStepContext(
                ElapsedTicks: (m_completedHostEngineTicks + context.StepTicks),
                StepTicks: context.StepTicks,
                Tick: m_completedHostSteps
            );

            var stepTick = m_instances.TryStepAuthority(
                context: in bootContext,
                pacing: pacing,
                publishTick: m_publishStep,
                server: m_server,
                tape: m_replayTape,
                deadline: deadline,
                collectBy: collectBy,
                pending: out var pending
            );

            m_completedHostEngineTicks += ((stepTick - m_completedHostSteps) * context.StepTicks);
            m_completedHostSteps = stepTick;
            if (pending is not null) { return false; }

            m_instances.ScanBootBoundaryTriggers();
            m_instances.SettleBootScreenSessions(stepped: true);
        } else {
            _ = m_server.DrainAdministrative();
            m_instances.SettleBootScreenSessions(stepped: false);

            // A world.wait armed before this pause landed can never see its release tick now that boot's own clock
            // is frozen (WorldConsoleWaitGate.PublishTick only ever fires from a step that actually ran) — release
            // it here rather than leave the held console stream, including the very world.rate resume that would
            // lift the pause, wedged behind a hold that will never clear on its own. A no-op (returns false) on
            // every ordinary paused/stopped tick once already released, so this costs nothing beyond one flag check.
            if (m_waitGate.ReleaseStalled()) {
                Console.Error.WriteLine(value: "[world.wait: released — the boot world stopped stepping (paused, rateHz 0, or a rate change the fixed-step pump has not caught up to yet) before its requested tick count was reached; resume it (world.rate resume) before arming a new wait]");
            }
        }
        return true;
    }

    private enum StepPhase { Prepare, Boot, Rows }
    private StepPhase m_phase;

    /// <summary>Gets whether a step has begun and not finished: its tick may be executing, so nothing reads the
    /// authority for the frame until it is collected.</summary>
    public bool StepInProgress => (m_phase != StepPhase.Prepare);
    private bool m_stepsBoot;

    /// <summary>Gets whether the step in progress can no longer finish: the boot authority froze at a stall while its
    /// tick was held. A row that freezes is skipped, so only the boot phase can hold the step for good. Outside the
    /// host's boundary window, and while a tick slice is in flight on the step thread, the answer is the one last
    /// published: a slice still executing has not frozen its authority.</summary>
    public bool StepInterrupted {
        get {
            Publish();
            return m_publishedInterrupted;
        }
    }

    /// <summary>Registers the host loop's wake with the step thread, which calls it each time a tick slice it executes
    /// finishes.</summary>
    /// <param name="wake">The wake, or <see langword="null"/> to register none.</param>
    public void WakeOnStepProgress(Action? wake) => m_instances.StepProgressed = wake;

    /// <summary>Runs one fixed step, resuming on a later call if a dependency exceeds the slice or the caller stops
    /// waiting for the step thread. A call that resumes prepares nothing again: it collects the tick in flight and
    /// goes on from there, so the step's order of events is the same however many calls it takes.</summary>
    /// <param name="context">The pump's tick context; only its step width reaches the server.</param>
    /// <param name="afterInstances">The shell's own post-step, run after every instance has stepped and before the
    /// seat intents finish: every shape syncs seat bindings and publishes seat context here, and a headless or
    /// offscreen shell then publishes logical wheel state.</param>
    /// <param name="collectBy">How long the caller waits for a tick the step thread executes before it answers
    /// <see langword="false"/> and leaves the tick in flight; <see langword="null"/> waits for each tick.</param>
    /// <returns><see langword="false"/> while the step is pending.</returns>
    public bool TryStep(in FixedStepContext context, Action? afterInstances, WaitDeadline? collectBy) {
        var deadline = WaitDeadline.In(HostWaits.PumpAllowance ?? TimeSpan.FromSeconds(30));
        if (m_phase == StepPhase.Prepare) {
            PrepareStep(in context);
            m_phase = StepPhase.Boot;
        }
        if (m_phase == StepPhase.Boot) {
            if (!StepBoot(in context, m_stepsBoot, deadline, collectBy)) { return false; }
            m_phase = StepPhase.Rows;
        }
        if (m_instances.TryStepInstances(context.StepTicks, deadline, collectBy) is not null) { return false; }
        roster.PublishProfiles(m_server.CaptureRosterProfiles());
        _ = m_seatRouter.DeliverRouteChanges();
        afterInstances?.Invoke();
        m_instances.FinishSeatIntents();
        extensions.Pump(tick: Tick);
        m_phase = StepPhase.Prepare;
        return true;
    }

    /// <summary>Replays deliveries recorded off the pump while the boundary window was closed and delivers any seat
    /// route changes they seeded. Call on the pump inside a settled boundary window.</summary>
    public void CollectDeliveries() {
        m_instances.CollectDeliveries();
        _ = m_seatRouter.DeliverRouteChanges();
    }

    private void PrepareStep(in FixedStepContext context) {
        sourceWatch.Poll();
        reload.Poll();
        m_instances.CollectDeliveries();
        // A seat claim can be published off this thread (a federated observer reporting an onward handoff) or by a
        // console verb in the drain just run. Its subscribers change seat state and bind state mirrors, so the edge is
        // delivered here, on the pump thread, at each point of the step after which a claim can have moved: before
        // this step's intents, after the transfer drain commits, and after the other instances step.
        _ = m_seatRouter.DeliverRouteChanges();

        // Computed first, before intents are submitted below: whether boot will actually step THIS call decides
        // whether its seats' input is consumed now or held untouched. context.StepTicks is threaded in so
        // ShouldStepBoot can refuse a call whose pump-supplied width no longer matches boot's current rate.
        m_stepsBoot = m_instances.ShouldStepBoot(stepTicks: context.StepTicks);

        // Seat intents are simulation input, submitted only when boot will actually consume them this call. A
        // paused/rate-0 boot world behaves as if no ticks existed: held seat input is never buffered into the
        // server's intent queue, so a resume never drains an accumulated backlog into one step. tick: (Tick + 1UL)
        // is this stepper's own contiguous next-tick coordinate (frozen while boot does not step), matching what
        // WorldServerStepShell.Step is about to report as the completed tick — never context.Tick + 1, the pump's
        // raw un-frozen cursor.
        m_instances.PrepareBootSeatIntents(
            stepsBoot: m_stepsBoot,
            tick: (Tick + 1UL),
            stepTicks: context.StepTicks
        );

        // The host-level pending-transfer FIFO's one fixed drain point — before either the boot instance or any
        // other instance steps this tick, mirroring where WorldServer.DrainPendingOps sits relative to the rest of
        // WorldServer.Step. A transfer is a host act, so it settles at this host's one fixed point rather than
        // inline with whichever instance's step produced it; a transfer drained here was enqueued by a per-step
        // portal scan during the PREVIOUS call, so it lands and is advanced exactly once, this same tick.
        m_instances.DrainPendingTransfers();
        _ = m_seatRouter.DeliverRouteChanges();
        m_instances.SubmitExternallyClockedSeatIntents();
    }
}
