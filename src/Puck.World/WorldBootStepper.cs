using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The boot instance's own step and host clock, shared by every fixed-step shell that owns a <see cref="WorldServer"/>
/// (the windowed <see cref="WorldSimulation"/> and the headless/offscreen <see cref="HeadlessWorldSimulation"/>):
/// steps the authority through <see cref="WorldServerStepShell"/> when boot is due, drains an administrative
/// mutation and releases a stalled console wait when it is not, and keeps the monotonic host-work coordinates a
/// console wait counts in — ONE implementation, so the tape/wait-gate/capture semantics can never fork by boot shape.
/// Also the <see cref="IWorldSimulationClock"/> a frame producer reads, in every shape.
/// </summary>
internal sealed class WorldBootStepper(WorldServer server, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldCaptureScheduler captureScheduler, WorldPeerHost peerHost, WorldInstanceHost instances) : IWorldSimulationClock {
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

    /// <summary>The exact engine time completed on the current authority timeline.</summary>
    public ulong ElapsedTicks => m_server.CompletedEngineTicks;
    /// <summary>The authored simulation rate the fixed-step pump paces this world at.</summary>
    public uint RatePerSecond => ((uint)m_server.Definition.SimulationRateHz);
    /// <summary>The number of fixed ticks completed on the current authority timeline.</summary>
    public ulong Tick => m_server.NextInputTick - 1UL;

    /// <summary>Steps the boot instance for one fixed-step call — or, when boot is not due, applies only what a
    /// paused world still owes. The boot instance steps by the same per-instance pause/rate-0 rule as every other
    /// instance (<paramref name="stepsBoot"/> is <see cref="WorldInstanceHost.ShouldStepBoot"/>'s answer, computed
    /// by the shell BEFORE it submits seat intents, because it decides whether those intents are consumed now or
    /// held untouched). When it is not due, the tape/wait-gate/socket bookkeeping is skipped right along with the
    /// step, but a buffered document mutation must still be able to apply, hence the administrative drain. The
    /// diegetic portal trigger's scan for boot runs immediately after its own step, reading boot's own just-settled
    /// state — never when boot did not step at all, so a paused/stopped boot's latched "inside" occupancy neither
    /// fires nor is re-evaluated until a genuine resume produces a new edge.</summary>
    /// <param name="context">The pump's tick context; only its step width reaches the server, and it is threaded
    /// through so a call whose pump-supplied width no longer matches boot's current rate was already refused by
    /// <see cref="WorldInstanceHost.ShouldStepBoot"/>.</param>
    /// <param name="stepsBoot">Whether boot is due this call.</param>
    public void StepBoot(in FixedStepContext context, bool stepsBoot) {
        if (stepsBoot) {
            // Host work advances only when this authority steps. The server independently advances from its
            // checkpointed clock; restoring a replay timeline rewinds that clock without rewinding console waits.
            // tick: (Tick + 1UL) on the shell's PrepareBootSeatIntents call is this stepper's own contiguous
            // next-tick coordinate (frozen while boot does not step), matching what WorldServerStepShell.Step is
            // about to report as the completed tick — never context.Tick + 1, the pump's raw un-frozen cursor.
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
}
