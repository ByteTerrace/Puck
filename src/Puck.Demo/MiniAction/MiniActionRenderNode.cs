using System.Numerics;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;

namespace Puck.Demo.MiniAction;

/// <summary>
/// The live, windowed root node for <c>--mini-action</c>. It owns the deterministic simulation and, each frame, advances
/// it by the whole fixed ticks elapsed — applying ROSTER EVENTS (controller join/leave) then INTENTS, in that order, per
/// tick — and renders the result through a <see cref="WorldProducerNode"/> whose frame source reflects the players via
/// the per-frame dynamic-transform buffer and the camera director's view list. Up to four local players join/leave by
/// connecting/disconnecting controllers; the screen frames everyone together when they're close and splits when they
/// spread. Vulkan host for this milestone. A headless debug spawn (<c>PUCK_MINIACTION_DEBUG_PLAYERS</c>) drives N
/// scripted players apart so split-screen can be captured without hardware.
/// </summary>
internal sealed class MiniActionRenderNode : IRenderNode {
    private readonly IServiceProvider m_serviceProvider;
    private readonly uint m_width;
    private readonly uint m_height;
    private readonly string? m_capturePath;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "mini-action",
        SurfaceId: SurfaceId.New()
    );
    private MiniActionWorld? m_world;
    private IPlayerIntentSource? m_intentSource;
    private IRosterEventSource? m_rosterSource;
    private WorldProducerNode? m_producer;

    /// <summary>Initializes a new instance of the <see cref="MiniActionRenderNode"/> class.</summary>
    public MiniActionRenderNode(IServiceProvider serviceProvider, uint width, uint height, string? capturePath) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        m_serviceProvider = serviceProvider;
        m_width = width;
        m_height = height;
        m_capturePath = capturePath;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out _)) {
            return default;
        }

        EnsureResources(context: in context);
        AdvanceSimulation(context: in context);

        return m_producer!.ProduceFrame(context: in context);
    }

    private void EnsureResources(in FrameContext context) {
        if (m_world is not null) {
            return;
        }

        var tickSeconds = (float)EngineTicks.ToSeconds(ticks: context.StepTicks);
        var room = MiniActionRoom.Default;

        m_world = new MiniActionWorld(room: room, tuning: PlatformerTuning.Default, tickSeconds: tickSeconds, seed: 1u);

        var debugPlayers = DebugPlayerCount();

        if (debugPlayers > 0) {
            // Headless verification: spawn N scripted players that walk to the corners, so the director splits the
            // screen — captured without any controller. Uses the real roster/world path, not a bypass.
            for (var index = 0; (index < debugPlayers); index++) {
                _ = m_world.AddPlayer(playerId: DeterministicGuid(salt: (uint)index));
            }

            m_intentSource = new ScriptedIntentSource(script: DebugScript);
            m_rosterSource = new ScriptedRosterEventSource(schedule: []);
        } else if (m_serviceProvider.GetService(serviceType: typeof(GamepadManager)) is GamepadManager manager) {
            // Live: controllers join/leave at runtime; each binds to a player and drives it per-device. Input
            // flows through the engine's deterministic router (RouterIntentSource) when the capture clock is
            // available; LocalIntentSource (the direct manager drain) remains the fallback.
            var registry = new ControllerPlayerRegistry();

            if (m_serviceProvider.GetService(serviceType: typeof(IInputClock)) is IInputClock clock) {
                m_intentSource = new RouterIntentSource(clock: clock, manager: manager, registry: registry, world: m_world);
            } else {
                m_intentSource = new LocalIntentSource(manager: manager, registry: registry, world: m_world);
            }

            m_rosterSource = new LocalRosterEventSource(manager: manager, registry: registry);
        } else {
            // No gamepad service: an empty room (overview camera) until input is available.
            m_intentSource = new ScriptedIntentSource(script: static (_, _) => PlayerIntent.None);
            m_rosterSource = new ScriptedRosterEventSource(schedule: []);
        }

        var shaderDirectory = CrossBackendShowcase.ShaderDirectory;

        m_producer = new WorldProducerNode(
            beamBytecode: File.ReadAllBytes(path: Path.Combine(path1: shaderDirectory, path2: "sdf-beam.comp.spv")),
            cullArgsBytecode: File.ReadAllBytes(path: Path.Combine(path1: shaderDirectory, path2: "sdf-cull-args.comp.spv")),
            capturePath: m_capturePath,
            children: null,
            compositeBytecode: File.ReadAllBytes(path: Path.Combine(path1: shaderDirectory, path2: "sdf-world-composite.comp.spv")),
            frameSource: new MiniActionFrameSource(world: m_world, room: room, director: new CameraDirector()),
            height: m_height,
            serviceProvider: m_serviceProvider,
            viewsBytecode: File.ReadAllBytes(path: Path.Combine(path1: shaderDirectory, path2: "sdf-world-views.comp.spv")),
            width: m_width
        );
    }
    private void AdvanceSimulation(in FrameContext context) {
        if (context.StepTicks == 0UL) {
            return;
        }

        var tickCount = (int)(context.DeltaTicks / context.StepTicks);

        if (tickCount <= 0) {
            return;
        }

        m_intentSource!.BeginFrame(firstTick: m_world!.CurrentTick);

        for (var index = 0; (index < tickCount); index++) {
            var tick = m_world.CurrentTick;

            // Roster events FIRST, so a joiner is present (and a leaver gone) for this tick's intents + step.
            foreach (var rosterEvent in m_rosterSource!.EventsForTick(tick: tick)) {
                _ = ((rosterEvent.Kind == RosterEventKind.Join)
                    ? m_world.AddPlayer(playerId: rosterEvent.PlayerId)
                    : m_world.RemovePlayer(playerId: rosterEvent.PlayerId));
            }

            var intents = m_intentSource.CollectTick(tick: tick, players: m_world.RosterBySlot());

            m_world.Advance(intentsBySlot: intents);
        }
    }
    private static int DebugPlayerCount() {
        return ((int.TryParse(Environment.GetEnvironmentVariable(variable: "PUCK_MINIACTION_DEBUG_PLAYERS"), out var count))
            ? Math.Clamp(count, 0, MiniActionWorld.MaxPlayers)
            : 0);
    }

    // Each debug player walks toward its corner so the swarm spreads past the split threshold.
    private static PlayerIntent DebugScript(ulong tick, int slot) {
        return new PlayerIntent(
            Move: new Vector2(x: (((slot % 2) == 0) ? -1f : 1f), y: ((slot < 2) ? -1f : 1f)),
            JumpHeld: false,
            JumpPressed: false,
            JumpReleased: false
        );
    }
    private static Guid DeterministicGuid(uint salt) {
        var bytes = new byte[16];

        BitConverter.TryWriteBytes(destination: bytes, value: (0xA571_0000u | salt));

        return new Guid(b: bytes);
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_producer?.Dispose();
    }
}
