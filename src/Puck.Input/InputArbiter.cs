using System.Numerics;
using Puck.Commands;
using Puck.Input.Devices;

namespace Puck.Input;

/// <summary>
/// How a lane resolves its <see cref="GamepadState"/> each frame. Orthogonal to <see cref="IInputArbiter.SuppressLane"/>:
/// a lane's mode says WHICH device(s) it would normally read; suppression is a separate, toggleable mute any mode can
/// carry (a lane parked on <see cref="Owned"/> can still be suppressed while its owner is on a debug page, say).
/// </summary>
public enum InputLaneMode {
    /// <summary>Reads the first currently connected device (player index order), OR-ing its buttons with every other
    /// connected device's so a press on ANY pad registers. Sticks/triggers/motion ride the first device with a
    /// non-neutral reading, falling back to the first connected device. For "any pad drives this" consumers (a
    /// shared attract-mode cursor, a lobby-ready check) rather than a specific seat.</summary>
    Multicast,
    /// <summary>Reads whichever device <see cref="GamepadManager.TryGetPlayerIndex"/> currently resolves to the
    /// policy's <see cref="InputLanePolicy.PlayerIndex"/>. Tracks hotplug/re-slot automatically — a lane never binds
    /// a specific <see cref="InputDeviceId"/>, only a seat number, so a pad that reconnects into the same slot keeps
    /// driving the lane with no explicit re-registration.</summary>
    PerPlayer,
    /// <summary>Reads whichever device was last bound with <see cref="IInputArbiter.SetLaneDevice"/> — an explicit,
    /// host-side override that ignores player-slot resolution entirely (a proximity takeover: "this pane is now
    /// driven by exactly this pad, regardless of which seat it occupies"). Neutral until a device is bound; a device
    /// that disconnects reads neutral again (silently — the caller decides whether that means "release the lane").</summary>
    Owned,
    /// <summary>Always reads neutral, unconditionally. Use this when a lane's mute is permanent for its lifetime;
    /// for a TEMPORARY mute on a lane that otherwise has real routing (the common case — a debug page blocking brick
    /// input), register the lane with its real mode and call <see cref="IInputArbiter.SuppressLane"/> instead.</summary>
    Suppressed,
}
/// <summary>
/// A lane's routing policy, fixed at <see cref="IInputArbiter.RegisterLane"/> time. <see cref="PlayerIndex"/> is
/// read only when <see cref="Mode"/> is <see cref="InputLaneMode.PerPlayer"/>; every other mode ignores it (kept at
/// its default 0 by the factory properties/method below).
/// </summary>
public readonly record struct InputLanePolicy {
    /// <summary>Initializes a routing policy.</summary>
    /// <param name="mode">How the lane resolves its device each frame.</param>
    /// <param name="playerIndex">The zero-based seat a per-player lane tracks; ignored by other modes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is undefined, or a per-player policy has a negative <paramref name="playerIndex"/>.</exception>
    public InputLanePolicy(InputLaneMode mode, int playerIndex = 0) {
        if (!Enum.IsDefined(value: mode)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(mode),
                actualValue: mode,
                message: "The lane mode is undefined."
            );
        }

        if (
            (mode == InputLaneMode.PerPlayer) &&
            (playerIndex < 0)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(playerIndex),
                actualValue: playerIndex,
                message: "The player index must be non-negative."
            );
        }

        Mode = mode;
        PlayerIndex = playerIndex;
    }

    /// <summary>Gets how the lane resolves its device each frame.</summary>
    public InputLaneMode Mode { get; }
    /// <summary>Gets the zero-based seat tracked by a per-player lane.</summary>
    public int PlayerIndex { get; }

    /// <summary>A lane that reads any connected pad (see <see cref="InputLaneMode.Multicast"/>).</summary>
    public static readonly InputLanePolicy Multicast = new(mode: InputLaneMode.Multicast);
    /// <summary>A lane that starts unbound and is driven only by explicit <see cref="IInputArbiter.SetLaneDevice"/>
    /// calls (see <see cref="InputLaneMode.Owned"/>).</summary>
    public static readonly InputLanePolicy Owned = new(mode: InputLaneMode.Owned);
    /// <summary>A lane that always reads neutral (see <see cref="InputLaneMode.Suppressed"/>).</summary>
    public static readonly InputLanePolicy Suppressed = new(mode: InputLaneMode.Suppressed);

    /// <summary>Builds a lane policy that tracks player seat <paramref name="playerIndex"/> (see
    /// <see cref="InputLaneMode.PerPlayer"/>).</summary>
    /// <param name="playerIndex">The non-negative, zero-based seat number to track.</param>
    /// <returns>The per-player lane policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="playerIndex"/> is negative.</exception>
    public static InputLanePolicy ForPlayer(int playerIndex) =>
        new(
            mode: InputLaneMode.PerPlayer,
            playerIndex: playerIndex
        );
}
/// <summary>
/// The engine-owned single-drainer mechanism: registrants that want a gamepad's per-frame state go through a
/// registered LANE instead of touching <see cref="GamepadManager.Drain"/> themselves, so the manager's destructive
/// per-device drain has exactly one caller no matter how many registrants read from it this frame.
/// </summary>
/// <remarks>
/// <para>
/// Usage shape, from a registrant's point of view: register a lane once (at construction — the returned token is
/// reference-stable for the lane's lifetime, so it is cheap to hold in a field), then each frame call
/// <see cref="DrainFrame"/> with a key that is stable for the whole produced frame (a render-tick counter, a tick
/// number — anything two registrants sampling the SAME frame will agree on) followed by <see cref="Sample"/>.
/// Calling <see cref="DrainFrame"/> more than once with the same key is free — the SECOND and later calls are a
/// no-op, so every registrant can call it defensively on every sample without coordinating who goes first.
/// </para>
/// <para>
/// Example — a lane that tracks player seat 0 and reads it once a frame:
/// <code>
/// var lane = arbiter.RegisterLane(policy: InputLanePolicy.ForPlayer(playerIndex: 0));
/// // ... once per produced frame, from anywhere:
/// arbiter.DrainFrame(frameKey: context.RenderTicks);
/// var state = arbiter.Sample(laneToken: lane);
/// </code>
/// </para>
/// <para>
/// Example — a debug page temporarily blocking a seat's input from reaching a lane another registrant reads (the
/// overworld's brick-input suppression): the page-input adapter and the brick-pad service each hold their OWN lane
/// for the same seat (lanes are cheap and independent), and the adapter mutes the BRICK service's lane while a
/// debug page is active:
/// <code>
/// arbiter.SuppressLane(laneToken: brickLaneForSeat, suppressed: true);   // debug page entered
/// // ... brickLaneForSeat now samples neutral until:
/// arbiter.SuppressLane(laneToken: brickLaneForSeat, suppressed: false); // every page button released
/// </code>
/// </para>
/// </remarks>
public interface IInputArbiter {
    /// <summary>Registers a new lane under the given policy and returns its reference-stable token. Register once
    /// (typically at construction) and hold the token — registering a fresh lane every frame works but pointlessly
    /// discards the previous one's state.</summary>
    /// <param name="policy">The lane's routing policy.</param>
    /// <returns>An opaque token identifying the lane for every other member on this interface.</returns>
    object RegisterLane(InputLanePolicy policy);
    /// <summary>Releases a registered lane and its retained routing state.</summary>
    /// <param name="laneToken">A token returned by <see cref="RegisterLane"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="laneToken"/> was not returned by this arbiter's <see cref="RegisterLane"/>.</exception>
    void UnregisterLane(object laneToken);
    /// <summary>
    /// Performs the run's one destructive <see cref="GamepadManager.Drain"/> for this produced frame, if it has not
    /// already run for <paramref name="frameKey"/>. Idempotent: any number of registrants may call this with the
    /// same key in the same frame, and only the first call does real work — so a registrant never needs to know
    /// whether it is first, only that it must call this before <see cref="Sample"/>.
    /// </summary>
    /// <param name="frameKey">
    /// A value that is the SAME for every call meant to observe one produced frame's input, and DIFFERENT from the
    /// previous frame's (a render-tick counter or a simulation tick number both work — the arbiter does not
    /// interpret it beyond equality).
    /// </param>
    void DrainFrame(ulong frameKey);
    /// <summary>Reads a lane's current <see cref="GamepadState"/>, per its <see cref="InputLaneMode"/> and current
    /// suppression flag. Safe to call before the first <see cref="DrainFrame"/> (reads neutral) and for a lane whose
    /// device has disconnected or never resolved (also neutral) — a lane never throws for "nothing to read".</summary>
    /// <param name="laneToken">A token returned by <see cref="RegisterLane"/>.</param>
    /// <returns>The lane's resolved state this frame, or <see cref="GamepadState.Neutral"/> if suppressed, unbound,
    /// or not yet drained.</returns>
    /// <exception cref="ArgumentException"><paramref name="laneToken"/> was not returned by this arbiter's
    /// <see cref="RegisterLane"/>.</exception>
    GamepadState Sample(object laneToken);
    /// <summary>Explicitly binds (or clears) the device an <see cref="InputLaneMode.Owned"/> lane reads — the
    /// proximity-takeover seam. Meaningless (silently ignored) for a lane registered under any other mode, since
    /// their routing already has its own resolution rule.</summary>
    /// <param name="laneToken">A token returned by <see cref="RegisterLane"/>.</param>
    /// <param name="device">The device to bind, or <see langword="default"/> to release the lane back to unbound
    /// (neutral until re-bound).</param>
    /// <exception cref="ArgumentException"><paramref name="laneToken"/> was not returned by this arbiter's
    /// <see cref="RegisterLane"/>.</exception>
    void SetLaneDevice(object laneToken, InputDeviceId device);
    /// <summary>Mutes (or unmutes) a lane, independent of its <see cref="InputLaneMode"/>: while suppressed,
    /// <see cref="Sample"/> reads neutral regardless of what the lane's mode would otherwise resolve. The lane
    /// keeps its policy/bound device underneath — unsuppressing resumes exactly where routing left off.</summary>
    /// <param name="laneToken">A token returned by <see cref="RegisterLane"/>.</param>
    /// <param name="suppressed">Whether the lane should read neutral starting this call.</param>
    /// <exception cref="ArgumentException"><paramref name="laneToken"/> was not returned by this arbiter's
    /// <see cref="RegisterLane"/>.</exception>
    void SuppressLane(object laneToken, bool suppressed);
    /// <summary>Returns a value indicating whether a lane is currently muted — either explicitly via <see cref="SuppressLane"/>, or because it
    /// was registered under <see cref="InputLaneMode.Suppressed"/>. Lets a registrant that keeps its own
    /// higher-level policy on top of a lane (a shared/mirror routing fallback, say) ask "would this lane read
    /// neutral right now" without duplicating the mute bookkeeping itself.</summary>
    /// <param name="laneToken">A token returned by <see cref="RegisterLane"/>.</param>
    /// <returns><see langword="true"/> if the lane currently reads neutral regardless of its routing.</returns>
    /// <exception cref="ArgumentException"><paramref name="laneToken"/> was not returned by this arbiter's
    /// <see cref="RegisterLane"/>.</exception>
    bool IsLaneSuppressed(object laneToken);
    /// <summary>Copies the current frame's full per-device drain into caller-owned reusable storage.</summary>
    /// <param name="destination">The list to clear and fill with the last frame passed to <see cref="DrainFrame"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    void CopyDrainedDevices(List<GamepadDrain> destination);
}
/// <summary>
/// The <see cref="IInputArbiter"/> implementation: owns exactly one <see cref="GamepadManager"/> and provides the
/// canonical single-drainer mechanism for all gamepad consumers.
/// </summary>
public sealed class InputArbiter : IInputArbiter {
    private readonly List<GamepadDrain> m_drainBuffer = [];
    private readonly Lock m_gate = new();
    private readonly List<Lane> m_lanes = [];

    private readonly GamepadManager m_manager;

    private ulong? m_drainedFrame;

    /// <summary>Initializes a new instance of the <see cref="InputArbiter"/> class.</summary>
    /// <param name="manager">The manager this arbiter is the sole drainer of.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    public InputArbiter(GamepadManager manager) {
        ArgumentNullException.ThrowIfNull(manager);

        m_manager = manager;
    }

    private static bool HasStickOrTriggerInput(in GamepadState state) =>
        ((state.LeftStick != Vector2.Zero) || (state.RightStick != Vector2.Zero) || (0f < state.LeftTrigger) || (0f < state.RightTrigger));
    // Called only while m_gate is held so validation and the following lane operation are one atomic action: a
    // concurrent unregister can never slip between them and let an already-released token mutate stale state.
    private Lane ResolveLocked(object laneToken) {
        ArgumentNullException.ThrowIfNull(laneToken);

        if (laneToken is not Lane lane) {
            throw new ArgumentException(
                message: "The lane token was not returned by this arbiter's RegisterLane.",
                paramName: nameof(laneToken)
            );
        }

        if (!m_lanes.Contains(item: lane)) {
            throw new ArgumentException(
                message: "The lane token was not returned by this arbiter's RegisterLane.",
                paramName: nameof(laneToken)
            );
        }

        return lane;
    }
    private GamepadState SampleDeviceLocked(InputDeviceId deviceId) {
        if (deviceId == default) {
            return GamepadState.Neutral;
        }

        foreach (var drain in m_drainBuffer) {
            if (drain.DeviceId == deviceId) {
                return drain.Latest;
            }
        }

        return GamepadState.Neutral;
    }
    // "Any pad" — buttons OR together across every connected device (so a press on ANY pad reads as held), while
    // continuous axes/motion ride the first device with a meaningfully non-rest stick/trigger (merging several
    // pads' sticks numerically has no canonical meaning), falling back to the first connected device's raw state
    // when every device is at rest.
    private GamepadState SampleMulticastLocked() {
        if (m_drainBuffer.Count == 0) {
            return GamepadState.Neutral;
        }

        var buttons = GamepadButtons.None;
        GamepadState? axesSource = null;

        foreach (var drain in m_drainBuffer) {
            var latest = drain.Latest;

            buttons |= latest.Buttons;

            if (
                (axesSource is null) &&
                HasStickOrTriggerInput(state: in latest)
            ) {
                axesSource = latest;
            }
        }

        var baseState = (axesSource ?? m_drainBuffer[0].Latest);

        return (baseState with { Buttons = buttons });
    }
    // playerIndex resolution rides GamepadManager.TryGetPlayerIndex against the just-drained device set, exactly
    // like every hand-rolled per-player rebuild this type replaces — a device that hot-unplugged this frame is
    // absent from the drain and never contributes, and a device that reconnects into the same slot resolves again
    // with no lane-side bookkeeping at all.
    private GamepadState SamplePerPlayerLocked(int playerIndex) {
        foreach (var drain in m_drainBuffer) {
            if (
                m_manager.TryGetPlayerIndex(
                deviceId: drain.DeviceId,
                playerIndex: out var resolved
            ) &&
                (resolved == playerIndex)
            ) {
                return drain.Latest;
            }
        }

        return GamepadState.Neutral;
    }

    /// <inheritdoc/>
    public void CopyDrainedDevices(List<GamepadDrain> destination) {
        ArgumentNullException.ThrowIfNull(destination);

        lock (m_gate) {
            destination.Clear();
            destination.AddRange(collection: m_drainBuffer);
        }
    }
    /// <inheritdoc/>
    public void DrainFrame(ulong frameKey) {
        lock (m_gate) {
            // Idempotent: a second call for the same produced frame (any number of registrants may each call this
            // defensively before sampling) is a deliberate no-op, not a re-drain — GamepadManager.Drain is
            // destructive per device, so draining twice would silently discard whatever arrived between the calls.
            if (m_drainedFrame == frameKey) {
                return;
            }

            m_drainedFrame = frameKey;
            m_manager.Drain(buffer: m_drainBuffer);
        }
    }
    /// <inheritdoc/>
    public bool IsLaneSuppressed(object laneToken) {
        lock (m_gate) {
            var lane = ResolveLocked(laneToken: laneToken);

            return (
                lane.Suppressed ||
                (lane.Policy.Mode == InputLaneMode.Suppressed)
            );
        }
    }
    /// <inheritdoc/>
    public object RegisterLane(InputLanePolicy policy) {
        var lane = new Lane(policy: policy);

        lock (m_gate) {
            m_lanes.Add(item: lane);
        }

        return lane;
    }
    /// <inheritdoc/>
    public GamepadState Sample(object laneToken) {
        lock (m_gate) {
            var lane = ResolveLocked(laneToken: laneToken);

            if (
                lane.Suppressed ||
                (lane.Policy.Mode == InputLaneMode.Suppressed)
            ) {
                return GamepadState.Neutral;
            }

            return (lane.Policy.Mode switch {
                InputLaneMode.PerPlayer => SamplePerPlayerLocked(playerIndex: lane.Policy.PlayerIndex),
                InputLaneMode.Owned => SampleDeviceLocked(deviceId: lane.Device),
                InputLaneMode.Multicast => SampleMulticastLocked(),
                _ => GamepadState.Neutral,
            });
        }
    }
    /// <inheritdoc/>
    public void SetLaneDevice(object laneToken, InputDeviceId device) {
        lock (m_gate) {
            var lane = ResolveLocked(laneToken: laneToken);

            lane.Device = device;
        }
    }
    /// <inheritdoc/>
    public void SuppressLane(object laneToken, bool suppressed) {
        lock (m_gate) {
            var lane = ResolveLocked(laneToken: laneToken);

            lane.Suppressed = suppressed;
        }
    }
    /// <inheritdoc/>
    public void UnregisterLane(object laneToken) {
        lock (m_gate) {
            var lane = ResolveLocked(laneToken: laneToken);

            _ = m_lanes.Remove(item: lane);
        }
    }

    // The opaque token IS the lane's own state holder — reference-stable by construction (a class instance), and
    // never exposed outside this file beyond the object reference RegisterLane hands back.
    private sealed class Lane {
        public Lane(InputLanePolicy policy) {
            Policy = policy;
        }

        public InputDeviceId Device { get; set; }
        public InputLanePolicy Policy { get; }
        public bool Suppressed { get; set; }
    }
}
