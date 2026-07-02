using Puck.Commands;
using Puck.Input;

namespace Puck.Demo.MiniAction;

/// <summary>
/// The local-input <see cref="IPlayerIntentSource"/> on the engine's deterministic input path: it captures the
/// gamepad into an <see cref="InputRouter"/> (the single canonical drain), then reads each tick's
/// <see cref="CommandSnapshot"/> and projects the per-slot move/jump lane to a <see cref="PlayerIntent"/>. This
/// replaces the direct <see cref="GamepadManager"/> drain — gameplay input now flows through the same
/// timestamped, bindable, recordable path as everything else, so a controller drives its bound slot via the
/// router's device→slot resolution.
/// </summary>
public sealed class RouterIntentSource : IPlayerIntentSource {
    private readonly GamepadCaptureSource m_capture;
    private readonly bool m_hasJump;
    private readonly bool m_hasMove;
    private readonly ushort m_jumpId;
    private readonly ushort m_moveId;
    private readonly InputRouter m_router;
    private CommandSnapshot m_frameSnapshot;
    private ulong m_firstTick;

    /// <summary>Initializes the source over the gamepad manager, the controller→player bind table, the world (for player→slot resolution), and the capture clock.</summary>
    public RouterIntentSource(GamepadManager manager, ControllerPlayerRegistry registry, MiniActionWorld world, IInputClock clock) {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(world);

        // A self-contained registry interns just MiniAction's two commands; the router binds the gamepad to
        // them and resolves each device to its bound player's slot.
        var commandRegistry = new CommandRegistry(modules: [new MiniActionCommandModule()]);

        m_router = new InputRouter(
            bindings: MiniActionInput.DefaultBindings,
            registry: commandRegistry,
            slotResolver: device => (registry.TryGetPlayer(device: device, player: out var player)
                ? world.SlotOf(playerId: player)
                : -1)
        );
        m_capture = new GamepadCaptureSource(clock: clock, manager: manager, router: m_router);
        m_hasMove = commandRegistry.TryGetId(name: MiniActionInput.MoveCommand, id: out m_moveId);
        m_hasJump = commandRegistry.TryGetId(name: MiniActionInput.JumpCommand, id: out m_jumpId);
    }

    /// <inheritdoc/>
    public void BeginFrame(ulong firstTick) {
        m_firstTick = firstTick;

        // Drain the manager once, capture into the router, then fold the whole frame's input into ONE snapshot
        // (the MaxValue window consumes everything captured this frame). The held fold persists the stick value
        // and the jump-held state, so CollectTick reuses this snapshot for every tick of the frame — the move
        // axis applies on every tick (not just the first), matching the prior direct-drain behavior. Sub-tick
        // attribution is a later refinement.
        m_capture.Capture();
        m_frameSnapshot = m_router.SnapshotForTick(tick: firstTick, windowEndTick: ulong.MaxValue);
    }

    /// <inheritdoc/>
    public PlayerIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> players) {
        var firstOfFrame = (tick == m_firstTick);
        var row = new PlayerIntent[MiniActionWorld.MaxPlayers];

        for (var slot = 0; (slot < MiniActionWorld.MaxPlayers); slot++) {
            if (m_frameSnapshot.TryGetLane(slot: slot, out var lane)) {
                var intent = MiniActionSnapshotProjection.FromLane(lane: lane, moveId: m_moveId, hasMove: m_hasMove, jumpId: m_jumpId, hasJump: m_hasJump);

                // Press/release edges fire only on the frame's first tick; the move axis and held state carry.
                row[slot] = (firstOfFrame
                    ? intent
                    : (intent with { JumpPressed = false, JumpReleased = false }));
            }
        }

        return row;
    }
}
