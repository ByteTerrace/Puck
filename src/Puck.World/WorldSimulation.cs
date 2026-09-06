using Puck.Commands;
using Puck.Hosting;
using Puck.World.Addons;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The world's fixed-step shell composing the client and server halves over the loopback. Launcher owns time
/// and snapshots; this type only consumes one exact tick at a time: <see cref="WorldHostStep"/> runs the one step
/// sequence (the client's seats submit their device intents, the authoritative <see cref="WorldServer"/> steps — the
/// mounted addon guests at its three pinned points → buffered protocol traffic → every body, INCLUDING every booted
/// screen machine (<c>Server.WorldMachineHost.Advance</c>) → the tick's snapshot, delivered to the client
/// synchronously — then every other instance), and this shell's post-step syncs the seats' bindings and publishes
/// their context before the seat intents finish.</summary>
internal sealed class WorldSimulation(WorldServer server, WorldClient client, WorldAddonRuntime addons, WorldSeatBindings seatBindings, WorldSeatAuthorityRouter seatRouter, WorldReplayTape replayTape, WorldConsoleWaitGate waitGate, WorldCaptureScheduler captureScheduler, WorldPeerHost peerHost, WorldPerceptionAnchor anchor, WorldInstanceHost instances, WorldViewComposer composer) : IFixedStepSimulation, IWorldSimulationClock {
    private readonly WorldServer m_server = server;
    private readonly WorldClient m_client = client;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly WorldSeatAuthorityRouter m_seatRouter = seatRouter;
    private readonly WorldPerceptionAnchor m_anchor = anchor;
    private readonly WorldViewComposer m_composer = composer;
    private readonly WorldHostStep m_step = new(
        captureScheduler: captureScheduler,
        instances: instances,
        peerHost: peerHost,
        replayTape: replayTape,
        server: server,
        waitGate: waitGate
    );
    private Action? m_afterInstances;

    /// <summary>The mounted addon runtime this shell holds — never called from here: the addon principals tick INSIDE
    /// <see cref="WorldServer.Step"/>, at its own three pinned points. It is a CONSTRUCTOR DEPENDENCY so that DI
    /// materializes it, because constructing it is what mounts every guest and attaches it to the server (see
    /// <see cref="WorldAddonRuntime.Create"/>) — dropping the parameter would leave the singleton unresolved and the
    /// world silently addon-less.</summary>
    public WorldAddonRuntime Addons { get; } = addons;

    /// <inheritdoc/>
    public ulong ElapsedTicks => m_step.ElapsedTicks;
    /// <inheritdoc/>
    public uint RatePerSecond => m_step.RatePerSecond;
    /// <inheritdoc/>
    public ulong Tick => m_step.Tick;

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) => m_step.Step(
        context: in context,
        afterInstances: (m_afterInstances ??= PublishSeats)
    );

    // Reflect any applied world-binding-overlay mutation into the per-seat resolvers, then publish the seats'
    // context-family states and the perception anchor so a state change this tick applied (an engage, a possession,
    // a roster move) flips the context-derived binding group and swaps the seat's perceived body the same tick.
    // Each seat's binding vocabulary and state-backed control contexts resolve from its complete authority route,
    // never uniformly from boot. Cheap on an ordinary tick: SyncSeat compares the delivered state, overlay, and
    // channel-list references before doing any work.
    private void PublishSeats() {
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
    }
}
