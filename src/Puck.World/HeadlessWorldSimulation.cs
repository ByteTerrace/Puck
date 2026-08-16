using Puck.Commands;
using Puck.Hosting;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The headless fixed-step shell (<c>host.presentation: none</c>) — the authoritative <see cref="WorldServer"/> plus
/// the same seat/authority input lifecycle as the presented host, but without screens or the editor session. It shares
/// <see cref="WorldServerStepShell"/> with the windowed <see cref="WorldSimulation"/> so tape/wait-gate semantics can
/// never fork by boot shape — the same server-side step, driven by the same
/// <c>Puck.Launcher.FixedStepPump</c> (snapshot → apply → step, in that exact order) either shape uses.
/// </summary>
internal sealed class HeadlessWorldSimulation(WorldServer server, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldTcpHost tcpHost, WorldInstanceHost instances) : IFixedStepSimulation {
    private readonly WorldServer m_server = server;
    private readonly WorldReplayTape m_replayTape = replayTape;
    private readonly WorldConsoleWaitGate m_waitGate = waitGate;
    private readonly WorldTcpHost m_tcpHost = tcpHost;
    private readonly WorldInstanceHost m_instances = instances;

    /// <summary>The exact engine time completed by the authoritative simulation.</summary>
    public ulong ElapsedTicks { get; private set; }
    /// <inheritdoc/>
    public uint RatePerSecond => ((uint)m_server.Definition.SimulationRateHz);
    /// <summary>The number of fixed ticks completed.</summary>
    public ulong Tick { get; private set; }

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
        var stepTick = Tick;

        if (stepsBoot) {
            // The boot world's OWN contiguous tick coordinate — see WorldSimulation.Step's identical remark for the
            // full reasoning: the pump's raw context.Tick/ElapsedTicks keep advancing every fixed-step call
            // regardless of whether boot actually stepped, so passing it straight through here would hand
            // WorldServerStepShell a jumped coordinate on the first step after a resume. Built from Tick/ElapsedTicks
            // as THIS shell already holds them (frozen below while boot does not step); only context.StepTicks — the
            // pump's own per-call step width — is read from the raw context.
            var bootContext = new FixedStepContext(
                ElapsedTicks: (ElapsedTicks + context.StepTicks),
                StepTicks: context.StepTicks,
                Tick: Tick
            );

            stepTick = WorldServerStepShell.Step(
                context: in bootContext,
                server: m_server,
                tape: m_replayTape,
                tcpHost: m_tcpHost,
                waitGate: m_waitGate
            );
            m_instances.ScanBootBoundaryTriggers();
            // Frozen — not merely unchanged — while boot did not step; see WorldSimulation.Step's identical remark.
            // Written HERE, from bootContext's own values, never from the raw pump context.
            ElapsedTicks = bootContext.ElapsedTicks;
            Tick = stepTick;
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
        m_instances.StepInstancesBesideBoot(masterDeltaTicks: context.StepTicks);
        m_instances.FinishSeatIntents();
    }
}
