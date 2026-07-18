using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The world's fixed-step shell composing the client and server halves over the loopback. Launcher owns time
/// and snapshots; this type only consumes one exact tick at a time: the client submits its seats' device intents, the
/// authoritative <see cref="WorldServer"/> steps (buffered protocol traffic → every body → the tick's snapshot,
/// delivered to the client synchronously), then the client-side post-step (the screen machines, the per-tick analog
/// clear).</summary>
internal sealed class WorldSimulation(WorldServer server, WorldClient client, WorldScreenBinder screens, WorldAddonDriver addons, WorldSeatBindings seatBindings) : IFixedStepSimulation {
    private const ulong TimingReportInterval = 60UL;

    private readonly WorldServer m_server = server;
    private readonly WorldClient m_client = client;
    private readonly WorldScreenBinder m_screens = screens;
    private readonly WorldAddonDriver m_addons = addons;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private SimulationTiming m_timingWorst;
    private ulong m_timingSamples;

    /// <summary>The number of fixed ticks completed.</summary>
    public ulong Tick { get; private set; }

    /// <summary>The exact engine time completed by the authoritative simulation.</summary>
    public ulong ElapsedTicks { get; private set; }

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) {
        var timingEnabled = GpuTimingControl.Shared.Armed;
        var phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);

        m_client.SubmitSeatIntents(tick: (context.Tick + 1UL));
        // The addon principals submit their intents into the same pre-step window as the seats (drained at Step, where
        // the server enforces Drive). A no-op when the world enables no addons.
        m_addons.Tick(tick: (context.Tick + 1UL));

        var rosterTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

        phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);
        m_server.Step(context: in context);

        // Reflect any applied world-binding-overlay mutation into the per-seat resolvers (reference-equal check
        // short-circuits when the definition did not change — one comparison on an ordinary tick, no per-frame work).
        m_seatBindings.SyncOverlays(overlays: m_client.Definition.BindingOverlays);

        var populationTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

        phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);
        m_screens.AdvanceMachines(stepTicks: context.StepTicks);

        var machinesTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

        phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);
        m_client.Roster.ClearAnalog();

        var finishTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

        ElapsedTicks = context.ElapsedTicks;
        Tick = (context.Tick + 1UL);

        if (timingEnabled) {
            ReportTiming(sample: new SimulationTiming(
                Tick: Tick,
                PopulationTicks: populationTicks,
                RosterTicks: rosterTicks,
                MachinesTicks: machinesTicks,
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

        Console.Error.WriteLine(value: $"[frame-timing] world-simulation worst-of-{TimingReportInterval} tick {worst.Tick} total {ToMs(ticks: worst.TotalTicks, frequency: frequency):0.000}ms | population {ToMs(ticks: worst.PopulationTicks, frequency: frequency):0.000} | roster {ToMs(ticks: worst.RosterTicks, frequency: frequency):0.000} | machines {ToMs(ticks: worst.MachinesTicks, frequency: frequency):0.000} | finish {ToMs(ticks: worst.FinishTicks, frequency: frequency):0.000}");

        m_timingWorst = default;
    }

    private readonly record struct SimulationTiming(
        ulong Tick,
        long PopulationTicks,
        long RosterTicks,
        long MachinesTicks,
        long FinishTicks
    ) {
        public long TotalTicks => (((PopulationTicks + RosterTicks) + MachinesTicks) + FinishTicks);
    }
}
