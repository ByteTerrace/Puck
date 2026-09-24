using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The headless fixed-step shell (<c>host.presentation: none</c>) — the authoritative <see cref="WorldServer"/> plus
/// the same seat/authority input lifecycle as the presented host, but without screens or the editor session. It is
/// <see cref="WorldHostStep"/> with no post-step, so nothing about the tick can fork by boot shape — the same
/// sequence, driven by the same <c>Puck.Launcher.FixedStepPump</c> (snapshot → apply → step, in that exact order)
/// either shape uses. Also serves as the offscreen shape's (<c>host.presentation: offscreen</c>)
/// <see cref="IWorldSimulationClock"/> — offscreen steps the server exactly like <c>none</c> and drives the frame
/// producer separately, off the same fixed-step pump.
/// </summary>
internal sealed class HeadlessWorldSimulation(WorldServer server, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldCaptureScheduler captureScheduler, WorldScheduleRunner scheduleRunner, WorldPeerHost peerHost, WorldInstanceHost instances, WorldServiceExtensions extensions, WorldSeatAuthorityRouter seatRouter) : IFixedStepSimulation, IWorldSimulationClock {
    private readonly WorldHostStep m_step = new(
        captureScheduler: captureScheduler,
        extensions: extensions,
        instances: instances,
        peerHost: peerHost,
        replayTape: replayTape,
        scheduleRunner: scheduleRunner,
        seatRouter: seatRouter,
        server: server,
        waitGate: waitGate
    );

    /// <inheritdoc/>
    public bool AwaitsFrame => m_step.AwaitsFrame;

    /// <inheritdoc/>
    public bool HoldsClock(ulong withheldTicks) => m_step.HoldsClock(withheldTicks: withheldTicks);
    /// <inheritdoc/>
    public void SettleOwedFrames() => m_step.SettleOwedFrames();

    /// <inheritdoc/>
    public ulong ElapsedTicks => m_step.ElapsedTicks;
    /// <inheritdoc/>
    public uint RatePerSecond => m_step.RatePerSecond;
    /// <inheritdoc/>
    public ulong Tick => m_step.Tick;

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) => m_step.Step(
        afterInstances: null,
        context: in context
    );
}
