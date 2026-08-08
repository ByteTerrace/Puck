using Puck.Commands;
using Puck.Hosting;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The headless fixed-step shell (<c>host.presentation: none</c>) — the authoritative <see cref="WorldServer"/> alone,
/// WITHOUT <c>Puck.World.Client.WorldClient</c>, screens, or the editor session. It shares
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

    /// <summary>The number of fixed ticks completed.</summary>
    public ulong Tick { get; private set; }

    /// <summary>The exact engine time completed by the authoritative simulation.</summary>
    public ulong ElapsedTicks { get; private set; }

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) {
        // Same fixed point as the windowed shape (WorldSimulation.Step) — see its own remarks: scan for portal-entry
        // edges, THEN drain, BEFORE any instance steps this tick, so a transfer lands and is advanced exactly once,
        // this same tick.
        m_instances.ScanPortalTriggers();
        m_instances.DrainPendingTransfers();

        var stepTick = WorldServerStepShell.Step(server: m_server, tape: m_replayTape, waitGate: m_waitGate, context: in context, tcpHost: m_tcpHost);

        // Every world instance running beside the boot one (world.instance.start) steps once per boot tick too —
        // folded into the SAME fixed-step call rather than a second pump (see WorldInstanceHost's own remarks).
        m_instances.StepInstancesBesideBoot(stepTicks: context.StepTicks);

        ElapsedTicks = context.ElapsedTicks;
        Tick = stepTick;
    }
}
