using Puck.Commands;
using Puck.Input;

namespace Puck.Demo.Overworld;

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
    private readonly CommandRegistry m_commandRegistry;
    private readonly bool m_hasInteract;
    private readonly bool m_hasJump;
    private readonly bool m_hasMove;
    private readonly ushort m_interactId;
    private readonly ushort m_jumpId;
    private readonly ushort m_moveId;
    private readonly InputRouter m_router;
    private CommandSnapshot m_frameSnapshot;
    private ulong m_firstTick;

    /// <summary>Initializes the source over the gamepad manager, the controller→player bind table, the world (for player→slot resolution), and the capture clock.</summary>
    /// <param name="bindings">The binding resolver the router folds signals through (a paged profile layered over
    /// the default, when one is loaded); <see langword="null"/> uses <see cref="OverworldInput.DefaultBindings"/>.</param>
    public RouterIntentSource(GamepadManager manager, ControllerPlayerRegistry registry, OverworldWorld world, IInputClock clock, IInputBindings? bindings = null) {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(world);

        // A self-contained registry interns the overworld's three commands plus the demo's placeholder actions (the
        // binding-page targets); the router binds the gamepad to them and resolves each device to its bound
        // player's slot.
        m_commandRegistry = new CommandRegistry(modules: [new OverworldCommandModule(), new DemoActionCommandModule()]);
        m_router = new InputRouter(
            bindings: (bindings ?? OverworldInput.DefaultBindings),
            registry: m_commandRegistry,
            slotResolver: device => (registry.TryGetPlayer(device: device, player: out var player)
                ? world.SlotOf(playerId: player)
                : -1)
        );
        m_capture = new GamepadCaptureSource(clock: clock, manager: manager, router: m_router);
        m_hasMove = m_commandRegistry.TryGetId(name: OverworldInput.MoveCommand, id: out m_moveId);
        m_hasJump = m_commandRegistry.TryGetId(name: OverworldInput.JumpCommand, id: out m_jumpId);
        m_hasInteract = m_commandRegistry.TryGetId(name: OverworldInput.InteractCommand, id: out m_interactId);
    }

    /// <summary>Gets the whole-frame snapshot the last <see cref="BeginFrame"/> produced (read by the binding-bar
    /// adapter for pressed-state display; the same data the intents project from).</summary>
    public CommandSnapshot FrameSnapshot => m_frameSnapshot;

    /// <summary>Resolves a command name to the id interned by this source's registry.</summary>
    /// <param name="name">The command name.</param>
    /// <param name="id">The interned id when found.</param>
    /// <returns><see langword="true"/> when the command is interned.</returns>
    public bool TryGetCommandId(string name, out ushort id) {
        return m_commandRegistry.TryGetId(name: name, id: out id);
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
        // Dispatch the frame's edges through the snapshot-driven path so the non-gameplay commands a binding
        // page targets (the demo.* placeholders) run their handlers; move/jump/interact are no-ops here and are
        // projected to intents below, exactly as before.
        m_commandRegistry.ApplySnapshot(snapshot: m_frameSnapshot);
    }

    /// <inheritdoc/>
    public PlayerIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> players) {
        var firstOfFrame = (tick == m_firstTick);
        var row = new PlayerIntent[OverworldWorld.MaxPlayers];

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            if (m_frameSnapshot.TryGetLane(slot: slot, out var lane)) {
                var intent = OverworldSnapshotProjection.FromLane(lane: lane, moveId: m_moveId, hasMove: m_hasMove, jumpId: m_jumpId, hasJump: m_hasJump, interactId: m_interactId, hasInteract: m_hasInteract);

                // Press/release edges fire only on the frame's first tick; the move axis and held state carry.
                row[slot] = (firstOfFrame
                    ? intent
                    : (intent with { JumpPressed = false, JumpReleased = false, InteractPressed = false }));
            }
        }

        return row;
    }
}
