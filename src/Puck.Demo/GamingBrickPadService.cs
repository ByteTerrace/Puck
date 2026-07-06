using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.HumbleGamingBrick;
using Puck.Input;
using Puck.Input.Devices;

namespace Puck.Demo;

/// <summary>How connected controllers route to the gaming-brick panes.</summary>
internal enum GamepadRouting {
    /// <summary>One connected controller multicasts to every brick; two or more map one-to-one by player index.</summary>
    Auto,
    /// <summary>The first controller's input goes to every brick, regardless of how many are connected.</summary>
    Multicast,
    /// <summary>Player index N drives brick ordinal N; a brick with no matching controller idles.</summary>
    PerPlayer,
}

/// <summary>
/// A brick-pane run's SOLE per-frame gamepad drainer (the overworld and any document with gaming-brick viewports): it
/// drains the <see cref="GamepadManager"/> exactly once per
/// render frame (frame-keyed by the callers' shared render clock), folds each device's held state into a per-player
/// map, and answers two questions — which <see cref="JoypadButtons"/> a brick pane holds this frame (per the
/// <see cref="GamepadRouting"/> policy), and what movement the overworld's room player intends. When the overworld root
/// is mirroring, the room player's movement REPLACES the bricks' directional input (walking the room walks the
/// games) while face/start/select still pass through from the first controller. Single-drainer discipline is
/// load-bearing: the manager's drain is destructive per device, so the global gamepad command source is suppressed
/// for runs that use this service (the same rule the live overworld root uses).
/// </summary>
/// <summary>Composition-root wiring for the pad-routing service, kept out of the top-level program to keep its
/// class coupling in check (the same discipline as <see cref="GamepadDemoRegistration"/>).</summary>
internal static class GamingBrickPadRegistration {
    /// <summary>Whether the document routes controllers through the shared pad service (any gaming-brick viewport
    /// pane) — in which case that service must be the run's sole gamepad drainer.</summary>
    /// <param name="document">The validated run document.</param>
    /// <returns><see langword="true"/> when a pad-service pane is present.</returns>
    public static bool UsesPadService(Puck.Scene.PuckRunDocument document) =>
        document.Viewports.Any(predicate: static viewport => viewport.Source is Puck.Scene.GamingBrickSource);

    /// <summary>Registers the pad-routing service, configured from the document's input section. Registered
    /// unconditionally — it stays idle unless a pane samples it.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="document">The validated run document.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddBrickPadRouting(this IServiceCollection services, Puck.Scene.PuckRunDocument document) {
        var routing = ((document.Input?.GamepadRouting ?? "auto").ToLowerInvariant() switch {
            "multicast" => GamepadRouting.Multicast,
            "per-player" => GamepadRouting.PerPlayer,
            _ => GamepadRouting.Auto,
        });

        services.AddSingleton(implementationFactory: sp => new GamingBrickPadService(routing: routing, serviceProvider: sp));

        return services;
    }
}

internal sealed class GamingBrickPadService(IServiceProvider serviceProvider, GamepadRouting routing) {
    private const float StickThreshold = 0.5f;

    // Host-side ownership overrides (the overworld's proximity takeover): brick ordinal → owning player index. An
    // owned brick is driven by that player's pad ALONE, bypassing both the multicast policy and the room mirror.
    // Never sim state — ownership is input routing, so the deterministic world hash never sees it.
    private readonly HashSet<int> m_brickInputBlockedPlayers = [];
    private readonly Dictionary<int, int> m_brickOwners = [];
    private readonly Dictionary<InputDeviceId, GamepadState> m_deviceStates = [];
    private readonly List<GamepadDrain> m_drainBuffer = [];
    // Per-player held state rebuilt from the device map after each drain; player indexes are small and stable.
    private readonly Dictionary<int, GamepadState> m_playerStates = [];
    // Per-player COMMAND-derived joypad images the overworld publishes each frame (PublishPlayerJoypad): the brick's input
    // as PROJECTED FROM THE SAME COMMANDS that drive the avatar, so a brick is a command sink like the world — not a
    // second raw-pad path. When a slot is published, it WINS over MapPad; an unpublished slot (a non-overworld document
    // with gaming-brick viewports) falls back to the raw pad map. Cleared and refilled by the overworld every frame.
    private readonly Dictionary<int, JoypadButtons> m_playerJoypads = [];

    private ulong m_drainedFrame = ulong.MaxValue;
    private GamepadManager? m_manager;
    private bool m_managerResolved;
    private JoypadButtons m_mirror;
    private bool m_mirrorActive;

    /// <summary>Samples the joypad a brick pane holds this frame.</summary>
    /// <param name="brickOrdinal">The brick's ordinal among the document's brick panes, in viewport-slot order.</param>
    /// <param name="frameKey">The caller's render-frame key (any value that changes once per pumped frame).</param>
    /// <returns>The held buttons.</returns>
    public JoypadButtons SampleBrick(int brickOrdinal, ulong frameKey) {
        DrainOnce(frameKey: frameKey);

        // An OWNED brick (proximity takeover) answers with its owner's pad, full stop: directions AND buttons come
        // from that one player, so neither the room mirror nor the routing policy below applies while owned.
        if (m_brickOwners.TryGetValue(key: brickOrdinal, value: out var owner)) {
            return MapPlayerPad(playerIndex: owner);
        }

        return SharedImage(brickOrdinal: brickOrdinal);
    }

    /// <summary>Samples the SHARED stream's joypad image this frame — what an unowned brick holds (the room mirror
    /// / routing policy), with every ownership override ignored. The overworld's input timeline records THIS, so an
    /// owned brick's private pad never leaks into the shared recording.</summary>
    /// <param name="frameKey">The caller's render-frame key.</param>
    /// <returns>The shared stream's held buttons.</returns>
    public JoypadButtons SampleSharedStream(ulong frameKey) {
        DrainOnce(frameKey: frameKey);

        return SharedImage(brickOrdinal: 0);
    }

    /// <summary>Samples the overworld room player's raw pad this frame (first controller): movement vector, jump, and
    /// interact holds. Interact rides the NORTH face button — East is the GB joypad's B, so a boot press never leaks
    /// into a running game.</summary>
    /// <param name="frameKey">The caller's render-frame key.</param>
    /// <returns>The movement in -1..1 per axis, whether jump (South) is held, and whether interact (North) is held.</returns>
    public (Vector2 Move, bool JumpHeld, bool InteractHeld) SampleOverworld(ulong frameKey) {
        DrainOnce(frameKey: frameKey);

        var state = PlayerState(playerIndex: 0);
        var move = state.LeftStick;

        if (0 != (state.Buttons & GamepadButtons.DpadLeft)) { move.X = -1f; }
        if (0 != (state.Buttons & GamepadButtons.DpadRight)) { move.X = 1f; }
        if (0 != (state.Buttons & GamepadButtons.DpadUp)) { move.Y = 1f; }
        if (0 != (state.Buttons & GamepadButtons.DpadDown)) { move.Y = -1f; }

        return (
            move,
            (0 != (state.Buttons & GamepadButtons.ButtonSouth)),
            (0 != (state.Buttons & GamepadButtons.ButtonNorth))
        );
    }

    /// <summary>Samples a player's whole drained pad state this frame — the raw feed the overworld's binding-page
    /// adapter replays into the paged resolver (sticks, triggers, and buttons together, under the same
    /// single-drainer discipline as every other sampler here).</summary>
    /// <param name="playerIndex">The player index to sample.</param>
    /// <param name="frameKey">The caller's render-frame key.</param>
    /// <returns>The player's current held state (neutral when no device is bound).</returns>
    public GamepadState SamplePlayerRaw(int playerIndex, ulong frameKey) {
        DrainOnce(frameKey: frameKey);

        return PlayerState(playerIndex: playerIndex);
    }

    /// <summary>Publishes the room player's movement as this frame's brick mirror (and marks mirroring active). The
    /// overworld root calls this every frame it advances; the bricks consume it on the same frame.</summary>
    /// <param name="mirror">The joypad image of the box player's movement.</param>
    public void PublishMirror(JoypadButtons mirror) {
        m_mirror = mirror;
        m_mirrorActive = true;
    }

    /// <summary>Clears the per-player COMMAND-derived joypad images; the overworld calls this once per frame before
    /// republishing every active slot, so a slot that went inactive stops driving a brick with a stale image.</summary>
    public void ClearPublishedJoypads() => m_playerJoypads.Clear();

    /// <summary>Publishes a player's COMMAND-derived joypad image for this frame — the brick input as projected from the
    /// same commands that drive the avatar (the command switchboard's feed). Overrides the raw pad map for that player;
    /// call <see cref="ClearPublishedJoypads"/> first each frame.</summary>
    /// <param name="playerIndex">The player slot whose command joypad this is.</param>
    /// <param name="joypad">The projected joypad image.</param>
    public void PublishPlayerJoypad(int playerIndex, JoypadButtons joypad) => m_playerJoypads[playerIndex] = joypad;

    /// <summary>Sets (or clears) a brick's ownership override — the overworld's proximity takeover. While set,
    /// <see cref="SampleBrick"/> answers with the owner's pad alone; -1 restores the normal routing (mirror /
    /// policy). Called on the render thread like every other consumer here, so the drain's no-lock discipline
    /// covers it.</summary>
    /// <param name="brickOrdinal">The brick's ordinal among the document's brick panes.</param>
    /// <param name="playerIndex">The owning player index, or -1 to clear.</param>
    public void SetBrickOwner(int brickOrdinal, int playerIndex) {
        if (playerIndex < 0) {
            _ = m_brickOwners.Remove(key: brickOrdinal);
        } else {
            m_brickOwners[brickOrdinal] = playerIndex;
        }
    }

    /// <summary>Controls whether a player slot may feed GamingBrick joypad input. The overworld disables this while the
    /// slot is on a debug binding page so debug buttons do not leak into the emulated game.</summary>
    /// <param name="playerIndex">The player slot.</param>
    /// <param name="enabled">Whether this player may currently drive bricks.</param>
    public void SetPlayerBrickInputEnabled(int playerIndex, bool enabled) {
        if (enabled) {
            _ = m_brickInputBlockedPlayers.Remove(item: playerIndex);
        } else {
            _ = m_brickInputBlockedPlayers.Add(item: playerIndex);
        }
    }

    // Drain the manager exactly once per pumped frame, whichever consumer gets here first. All consumers run on the
    // render thread (ProduceFrame), so no lock is needed.
    private void DrainOnce(ulong frameKey) {
        if (frameKey == m_drainedFrame) {
            return;
        }

        m_drainedFrame = frameKey;

        if (!m_managerResolved) {
            m_managerResolved = true;
            m_manager = (serviceProvider.GetService(serviceType: typeof(GamepadManager)) as GamepadManager);
        }

        if (m_manager is null) {
            return;
        }

        // A device with nothing pending is absent from the drain; its last-known held state stays current.
        m_manager.Drain(buffer: m_drainBuffer);

        foreach (var drain in m_drainBuffer) {
            m_deviceStates[drain.DeviceId] = drain.Latest;
        }

        m_playerStates.Clear();

        foreach (var (deviceId, state) in m_deviceStates) {
            // A disconnected device no longer resolves a player index; its stale state simply stops mattering.
            if (m_manager.TryGetPlayerIndex(deviceId: deviceId, playerIndex: out var playerIndex)) {
                m_playerStates[playerIndex] = state;
            }
        }
    }
    private GamepadState PlayerState(int playerIndex) =>
        (m_playerStates.TryGetValue(key: playerIndex, value: out var state) ? state : GamepadState.Neutral);

    // The pre-takeover routing an UNOWNED brick rides: the room mirror when active, otherwise the configured
    // multicast/per-player policy. Assumes DrainOnce already ran this frame.
    private JoypadButtons SharedImage(int brickOrdinal) {
        if (m_mirrorActive) {
            if (!PlayerBrickInputEnabled(playerIndex: 0)) {
                return default;
            }

            // The room player (slot 0) drives every unowned brick — "walk the room walks the games". Its COMMAND-
            // derived joypad (published by the overworld) IS the mirror; only a non-overworld document (no publish) falls
            // back to the raw movement mirror + first-pad buttons.
            if (m_playerJoypads.TryGetValue(key: 0, value: out var published)) {
                return published;
            }

            const JoypadButtons Directions = (JoypadButtons.Up | JoypadButtons.Down | JoypadButtons.Left | JoypadButtons.Right);

            return (m_mirror | (MapPad(state: PlayerState(playerIndex: 0)) & ~Directions));
        }

        var multicast = (routing switch {
            GamepadRouting.Multicast => true,
            GamepadRouting.PerPlayer => false,
            _ => (m_playerStates.Count <= 1),
        });

        return MapPlayerPad(playerIndex: (multicast ? 0 : brickOrdinal));
    }
    private bool PlayerBrickInputEnabled(int playerIndex) =>
        !m_brickInputBlockedPlayers.Contains(item: playerIndex);
    // The brick input for a player: the COMMAND-derived joypad the overworld published wins (a brick as a command sink);
    // an unpublished slot (non-overworld brick document) falls back to the raw pad map.
    private JoypadButtons MapPlayerPad(int playerIndex) {
        if (!PlayerBrickInputEnabled(playerIndex: playerIndex)) {
            return default;
        }

        return (m_playerJoypads.TryGetValue(key: playerIndex, value: out var joypad) ? joypad : MapPad(state: PlayerState(playerIndex: playerIndex)));
    }

    // The neutral pad → brick joypad image: South→A, East→B, Start→Start, Back→Select, dpad OR stick → directions.
    private static JoypadButtons MapPad(GamepadState state) {
        var buttons = default(JoypadButtons);
        var held = state.Buttons;

        if (0 != (held & GamepadButtons.ButtonSouth)) { buttons |= JoypadButtons.A; }
        if (0 != (held & GamepadButtons.ButtonEast)) { buttons |= JoypadButtons.B; }
        if (0 != (held & GamepadButtons.Start)) { buttons |= JoypadButtons.Start; }
        if (0 != (held & GamepadButtons.Back)) { buttons |= JoypadButtons.Select; }
        if ((0 != (held & GamepadButtons.DpadUp)) || (state.LeftStick.Y > StickThreshold)) { buttons |= JoypadButtons.Up; }
        if ((0 != (held & GamepadButtons.DpadDown)) || (state.LeftStick.Y < -StickThreshold)) { buttons |= JoypadButtons.Down; }
        if ((0 != (held & GamepadButtons.DpadLeft)) || (state.LeftStick.X < -StickThreshold)) { buttons |= JoypadButtons.Left; }
        if ((0 != (held & GamepadButtons.DpadRight)) || (state.LeftStick.X > StickThreshold)) { buttons |= JoypadButtons.Right; }

        return buttons;
    }
}
