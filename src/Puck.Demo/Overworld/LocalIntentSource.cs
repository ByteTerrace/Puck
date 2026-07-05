using System.Numerics;
using Puck.Input;
using Puck.Input.Devices;

namespace Puck.Demo.Overworld;

/// <summary>
/// The local-input <see cref="IPlayerIntentSource"/>: it drains the <see cref="GamepadManager"/> directly each frame and
/// routes EACH controller's per-device state (its own coalesced stick + button edges) to the slot of the player it is
/// bound to, building the fixed-width intent row the simulation steps. This is the per-device routing split-screen needs
/// — every controller drives its own player independently. Network and AI sources implement the same interface, so the
/// simulation never knows which it is. In <c>--overworld</c> mode this is the SOLE gamepad drainer (the global gamepad
/// command source is suppressed) so the per-device edges aren't consumed before this runs.
/// </summary>
public sealed class LocalIntentSource : IPlayerIntentSource {
    private readonly GamepadManager m_manager;
    private readonly ControllerPlayerRegistry m_registry;
    private readonly OverworldWorld m_world;
    private readonly List<GamepadDrain> m_drainBuffer = [];
    private readonly PlayerIntent[] m_frameIntents = new PlayerIntent[OverworldWorld.MaxPlayers];
    private ulong m_firstTick;

    /// <summary>Initializes the source over the gamepad manager, the controller→player bind table, and the world (for
    /// player→slot resolution).</summary>
    public LocalIntentSource(GamepadManager manager, ControllerPlayerRegistry registry, OverworldWorld world) {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(world);

        m_manager = manager;
        m_registry = registry;
        m_world = world;
    }

    /// <inheritdoc/>
    public void BeginFrame(ulong firstTick) {
        m_firstTick = firstTick;

        Array.Fill(array: m_frameIntents, value: PlayerIntent.None);

        m_drainBuffer.Clear();
        m_manager.Drain(buffer: m_drainBuffer);

        foreach (var drain in m_drainBuffer) {
            if (!m_registry.TryGetPlayer(device: drain.DeviceId, player: out var player)) {
                continue;
            }

            var slot = m_world.SlotOf(playerId: player);

            if ((slot < 0) || (slot >= OverworldWorld.MaxPlayers)) {
                continue;
            }

            m_frameIntents[slot] = ToIntent(drain: drain);
        }
    }

    /// <inheritdoc/>
    public PlayerIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> players) {
        var firstOfFrame = (tick == m_firstTick);
        var row = new PlayerIntent[OverworldWorld.MaxPlayers];

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            var intent = m_frameIntents[slot];

            // The press/release edges fire only on the frame's first tick; held state carries across the rest.
            row[slot] = (firstOfFrame ? intent : (intent with { JumpPressed = false, JumpReleased = false, InteractPressed = false }));
        }

        return row;
    }

    private static PlayerIntent ToIntent(GamepadDrain drain) {
        var stick = drain.Latest.LeftStick;

        return new PlayerIntent(
            // The fixed chase camera looks toward -Z, so stick-up (forward) maps to world -Z.
            Move: new Vector2(x: stick.X, y: -stick.Y),
            JumpHeld: ((drain.Latest.Buttons & GamepadButtons.ButtonSouth) != GamepadButtons.None),
            JumpPressed: ((drain.Pressed & GamepadButtons.ButtonSouth) != GamepadButtons.None),
            JumpReleased: ((drain.Released & GamepadButtons.ButtonSouth) != GamepadButtons.None),
            InteractPressed: ((drain.Pressed & GamepadButtons.ButtonNorth) != GamepadButtons.None)
        );
    }
}
