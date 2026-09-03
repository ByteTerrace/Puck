using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The headless fixed-step shell (<c>host.presentation: none</c>) — the authoritative <see cref="WorldServer"/> plus
/// the same seat/authority input lifecycle as the presented host, but without screens or the editor session. It shares
/// <see cref="WorldServerStepShell"/> with the windowed <see cref="WorldSimulation"/> so tape/wait-gate semantics can
/// never fork by boot shape — the same server-side step, driven by the same
/// <c>Puck.Launcher.FixedStepPump</c> (snapshot → apply → step, in that exact order) either shape uses. Also serves as
/// the offscreen shape's (<c>host.presentation: offscreen</c>) <see cref="IWorldSimulationClock"/> — offscreen steps
/// the server exactly like <c>none</c> and drives the frame producer separately, off the same fixed-step pump.
/// </summary>
internal sealed class HeadlessWorldSimulation(WorldServer server, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldCaptureScheduler captureScheduler, WorldPeerHost peerHost, WorldInstanceHost instances) : IFixedStepSimulation, IWorldSimulationClock {
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
    /// <inheritdoc/>
    public uint RatePerSecond => ((uint)m_server.Definition.SimulationRateHz);
    /// <summary>The number of fixed ticks completed on the current authority timeline.</summary>
    public ulong Tick => m_server.NextInputTick - 1UL;

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) {
        var stepsBoot = m_instances.ShouldStepBoot(stepTicks: context.StepTicks);

        // View-relative controls are client-side simulation composition, not rendering. The shared host lifecycle
        // keeps a headless authority equivalent to the presented shape for the same command snapshot.
        m_instances.PrepareBootSeatIntents(
            stepsBoot: stepsBoot,
            tick: (Tick + 1UL),
            stepTicks: context.StepTicks
        );

        // Same fixed point as the windowed shape (WorldSimulation.Step) — see its own remarks: drain BEFORE any
        // instance steps this tick (a transfer drained here was enqueued by a per-step portal scan during the
        // PREVIOUS master call), so a transfer lands and is advanced exactly once, this same tick.
        m_instances.DrainPendingTransfers();
        m_instances.SubmitExternallyClockedSeatIntents();

        // See WorldSimulation.Step's identical gate for the full remarks: boot steps by the same pause/rate-0 rule
        // as every other instance, trivially due almost always; when it is not, the tape/wait-gate/socket
        // bookkeeping is skipped along with the step, but an administrative drain still lets a buffered document
        // mutation apply. The portal scan for boot runs immediately after its own step, never when it did not step
        // at all. context.StepTicks is threaded in so ShouldStepBoot can refuse a call whose pump-supplied width no
        // longer matches boot's current rate.
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

            // See WorldSimulation.Step's identical release — a world.wait armed before this pause landed can never
            // see its release tick now that boot's own clock is frozen; release it rather than leave the held
            // console stream (including the very world.rate resume that would lift the pause) wedged forever.
            if (m_waitGate.ReleaseStalled()) {
                Console.Error.WriteLine(value: "[world.wait: released — the boot world stopped stepping (paused, rateHz 0, or a rate change the fixed-step pump has not caught up to yet) before its requested tick count was reached; resume it (world.rate resume) before arming a new wait]");
            }
        }

        // Every world instance running beside the boot one (world.instance.start) advances on its OWN authored
        // schedule — folded into the SAME fixed-step call rather than a second pump (see WorldInstanceHost's own
        // remarks). masterDeltaTicks is the host's own per-call engine-time advance, never a second clock.
        m_instances.StepInstances(masterDeltaTicks: context.StepTicks);
        m_instances.FinishSeatIntents();
    }
}
