using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The world's fixed-step shell composing the client and server halves over the loopback. Launcher owns time
/// and snapshots; this type only consumes one exact tick at a time: the client submits its seats' device intents, the
/// authoritative <see cref="WorldServer"/> steps (the mounted addon guests at its three pinned points → buffered
/// protocol traffic → every body, INCLUDING every booted screen machine (<c>Server.WorldMachineHost.Advance</c>)
/// → the tick's snapshot, delivered to the client synchronously), then
/// the client-side post-step (the per-tick analog clear).</summary>
internal sealed class WorldSimulation(WorldServer server, WorldClient client, WorldAddonRuntime addons, WorldSeatBindings seatBindings, WorldEditorSession editor, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldTcpHost tcpHost, WorldPerceptionAnchor anchor, WorldInstanceHost instances) : IFixedStepSimulation {
    private const ulong TimingReportInterval = 60UL;

    private readonly WorldServer m_server = server;
    private readonly WorldClient m_client = client;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly WorldEditorSession m_editor = editor;
    private readonly WorldReplayTape m_replayTape = replayTape;
    private readonly WorldConsoleWaitGate m_waitGate = waitGate;
    private readonly WorldTcpHost m_tcpHost = tcpHost;
    private readonly WorldPerceptionAnchor m_anchor = anchor;
    private readonly WorldInstanceHost m_instances = instances;
    private SimulationTiming m_timingWorst;
    private ulong m_timingSamples;

    /// <summary>The mounted addon runtime this shell holds — never called from here: the addon principals tick INSIDE
    /// <see cref="WorldServer.Step"/>, at its own three pinned points. It is a CONSTRUCTOR DEPENDENCY so that DI
    /// materializes it, because constructing it is what mounts every guest and attaches it to the server (see
    /// <see cref="WorldAddonRuntime.Create"/>) — dropping the parameter would leave the singleton unresolved and the
    /// world silently addon-less.</summary>
    public WorldAddonRuntime Addons { get; } = addons;

    /// <summary>The number of fixed ticks completed.</summary>
    public ulong Tick { get; private set; }

    /// <summary>The exact engine time completed by the authoritative simulation.</summary>
    public ulong ElapsedTicks { get; private set; }

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) {
        var timingEnabled = GpuTimingControl.Shared.Armed;
        var phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);

        m_client.SubmitSeatIntents(tick: (context.Tick + 1UL));

        var rosterTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

        phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);

        // The diegetic portal trigger's ONE fixed scan point — SAME tick, immediately BEFORE the drain below, so a
        // body that just crossed into a portal's enterable volume (as of the positions THIS tick's Step is about to
        // advance from) has its transfer queued and applied before either instance steps again. See
        // WorldInstanceHost.ScanPortalTriggers for the edge-detection and why this ordering is replay-stable.
        m_instances.ScanPortalTriggers();

        // The host-level pending-transfer FIFO's ONE fixed drain point — BEFORE either the boot instance or any
        // other instance steps this tick, mirroring where WorldServer.DrainPendingOps sits relative to the rest of
        // WorldServer.Step. Draining here (rather than after) means a transfer that lands this tick is advanced
        // exactly once THIS tick, by whichever instance now holds it — never by the source (already left) and never
        // skipped by the destination (already joined before it steps below).
        m_instances.DrainPendingTransfers();

        var stepTick = WorldServerStepShell.Step(server: m_server, tape: m_replayTape, waitGate: m_waitGate, context: in context, tcpHost: m_tcpHost);

        // Every world instance running beside the boot one (world.instance.start) steps once per boot tick too —
        // folded into the SAME fixed-step call rather than a second pump (see WorldInstanceHost's own remarks).
        m_instances.StepInstancesBesideBoot(stepTicks: context.StepTicks);

        // Reflect any applied world-binding-overlay mutation into the per-seat resolvers (reference-equal check
        // short-circuits when the definition did not change — one comparison on an ordinary tick, no per-frame work),
        // then publish the seats' context-family states AND the perception anchor so a state change this tick
        // applied (an engage, a possession, a roster move) flips the context-derived binding group and swaps the
        // seat's perceived body the same tick — a world.wait-fenced read-back observes both deterministically.
        m_seatBindings.SyncDefinition(definition: m_client.Definition);
        WorldSeatContextSync.Publish(seatBindings: m_seatBindings, roster: m_client.Roster, grants: m_server.Grants, anchor: m_anchor);

        // Machine stepping runs INSIDE WorldServerStepShell.Step (Server.WorldMachineHost.Advance, called from
        // WorldServer.Step right after WorldEngagement.FoldTick), so its cost is already folded into
        // populationTicks below; there is no separate phase to time here.
        var populationTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

        phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);
        m_client.Roster.ClearAnalog();
        // Promote this tick's staged editor-stick samples to the frame-visible latch (the editor camera's per-frame
        // integration cadence), beside the seats' own analog clear.
        m_editor.LatchTick();

        var finishTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

        ElapsedTicks = context.ElapsedTicks;
        Tick = stepTick;

        if (timingEnabled) {
            ReportTiming(sample: new SimulationTiming(
                Tick: Tick,
                PopulationTicks: populationTicks,
                RosterTicks: rosterTicks,
                FinishTicks: finishTicks
            ));
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
            (((double)ticks * 1000.0) / frequency);

        Console.Error.WriteLine(value: $"[frame-timing] world-simulation worst-of-{TimingReportInterval} tick {worst.Tick} total {ToMs(ticks: worst.TotalTicks, frequency: frequency):0.000}ms | population {ToMs(ticks: worst.PopulationTicks, frequency: frequency):0.000} | roster {ToMs(ticks: worst.RosterTicks, frequency: frequency):0.000} | finish {ToMs(ticks: worst.FinishTicks, frequency: frequency):0.000}");

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
