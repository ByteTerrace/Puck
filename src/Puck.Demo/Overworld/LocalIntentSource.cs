using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;

namespace Puck.Demo.Overworld;

/// <summary>
/// The local-input <see cref="IIntentSource{TIntent}"/> of <see cref="PlayerIntent"/>: each frame it routes EACH
/// controller's per-device state (its own coalesced stick + button edges) to the slot of the player it is bound to,
/// building the fixed-width intent row the simulation steps. This is the per-device routing split-screen needs —
/// every controller drives its own player independently. Network and AI sources implement the same interface, so the
/// simulation never knows which it is. The frame's actual drain lives one layer up, in a registered
/// <see cref="IInputArbiter"/> lane (an <see cref="InputLaneMode.Multicast"/>-policy lane: this source routes
/// per-device itself via <see cref="IInputArbiter.DrainedDevices"/>, so it needs the whole frame's drain, not one
/// seat's). In <c>--overworld</c> mode this is the SOLE gamepad drainer of the arbiter (the global gamepad command
/// source is suppressed) so the per-device edges aren't consumed before this runs.
/// </summary>
public sealed class LocalIntentSource : IIntentSource<PlayerIntent> {
    private readonly IInputArbiter m_arbiter;
    private readonly object m_lane;
    private readonly ControllerPlayerRegistry m_registry;
    private readonly OverworldWorld m_world;
    private readonly PlayerIntent[] m_frameIntents = new PlayerIntent[OverworldWorld.MaxPlayers];
    private ulong m_firstTick;

    /// <summary>Initializes the source over the input arbiter, the controller→player bind table, and the world (for
    /// player→slot resolution).</summary>
    public LocalIntentSource(IInputArbiter arbiter, ControllerPlayerRegistry registry, OverworldWorld world) {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(world);

        m_arbiter = arbiter;
        m_lane = arbiter.RegisterLane(policy: InputLanePolicy.Multicast);
        m_registry = registry;
        m_world = world;
    }

    /// <summary>Gets this source's arbiter lane token — exposed so a future registrant can mute gameplay input via
    /// <see cref="IInputArbiter.SuppressLane"/> without this source needing to know why.</summary>
    public object LaneToken => m_lane;

    /// <inheritdoc/>
    public void BeginFrame(ulong firstTick) {
        m_firstTick = firstTick;

        Array.Fill(array: m_frameIntents, value: PlayerIntent.None);

        m_arbiter.DrainFrame(frameKey: firstTick);

        foreach (var drain in m_arbiter.DrainedDevices) {
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
    public PlayerIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> participants) {
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
