using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The headless fixed-step shell (<c>host.presentation: none</c>) — the authoritative <see cref="WorldServer"/> plus
/// the same seat/authority input lifecycle as the presented host, but without screens or the editor session. It shares
/// <see cref="WorldBootStepper"/> with the windowed <see cref="WorldSimulation"/> so tape/wait-gate semantics can
/// never fork by boot shape — the same server-side step, driven by the same
/// <c>Puck.Launcher.FixedStepPump</c> (snapshot → apply → step, in that exact order) either shape uses. Also serves as
/// the offscreen shape's (<c>host.presentation: offscreen</c>) <see cref="IWorldSimulationClock"/> — offscreen steps
/// the server exactly like <c>none</c> and drives the frame producer separately, off the same fixed-step pump.
/// </summary>
internal sealed class HeadlessWorldSimulation(WorldServer server, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldCaptureScheduler captureScheduler, WorldPeerHost peerHost, WorldInstanceHost instances) : IFixedStepSimulation, IWorldSimulationClock {
    private readonly WorldInstanceHost m_instances = instances;
    private readonly WorldBootStepper m_boot = new(
        captureScheduler: captureScheduler,
        instances: instances,
        peerHost: peerHost,
        replayTape: replayTape,
        server: server,
        waitGate: waitGate
    );

    /// <inheritdoc/>
    public ulong ElapsedTicks => m_boot.ElapsedTicks;
    /// <inheritdoc/>
    public uint RatePerSecond => m_boot.RatePerSecond;
    /// <inheritdoc/>
    public ulong Tick => m_boot.Tick;

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
        m_boot.StepBoot(
            context: in context,
            stepsBoot: stepsBoot
        );

        // Every world instance running beside the boot one (world.instance.start) advances on its OWN authored
        // schedule — folded into the SAME fixed-step call rather than a second pump (see WorldInstanceHost's own
        // remarks). masterDeltaTicks is the host's own per-call engine-time advance, never a second clock.
        m_instances.StepInstances(masterDeltaTicks: context.StepTicks);
        m_instances.FinishSeatIntents();
    }
}
