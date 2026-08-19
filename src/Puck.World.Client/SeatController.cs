using System.Numerics;

using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>How a routed movement sample affects body facing.</summary>
public enum SeatMoveBehavior : byte {
    /// <summary>Preserves body heading and follows the live camera unless held free look selects body heading.</summary>
    Strafe,
    /// <summary>Latches camera yaw for the movement and faces the body along its travel.</summary>
    FaceTravel,
}
/// <summary>One tick-local movement sample and its authored behavior.</summary>
/// <param name="Value">The quantized movement pair, with positive Y forward and positive X right.</param>
/// <param name="Behavior">How the sample affects body facing.</param>
public readonly record struct SeatMoveSample(FixedVector2 Value, SeatMoveBehavior Behavior) {
    /// <summary>Gets a value indicating whether either movement axis is active.</summary>
    public bool IsActive => ((Value.X != FixedQ4816.Zero) || (Value.Y != FixedQ4816.Zero));
}
/// <summary>How a routed look sample affects body facing.</summary>
public enum SeatLookBehavior : byte {
    /// <summary>Orbits the camera without changing body heading.</summary>
    Orbit,
    /// <summary>Orbits the camera and faces the body along horizontal look.</summary>
    FaceBody,
}
/// <summary>One tick-local look sample and its authored behavior.</summary>
/// <param name="Value">The quantized look pair, with positive X right and positive Y up.</param>
/// <param name="Behavior">How horizontal look affects body facing.</param>
public readonly record struct SeatLookSample(FixedVector2 Value, SeatLookBehavior Behavior) {
    /// <summary>Gets a value indicating whether either look axis is active.</summary>
    public bool IsActive => ((Value.X != FixedQ4816.Zero) || (Value.Y != FixedQ4816.Zero));
    /// <summary>Gets a value indicating whether horizontal look faces the body.</summary>
    public bool FacesBody => ((Behavior == SeatLookBehavior.FaceBody) && (Value.X != FixedQ4816.Zero));
}
/// <summary>
/// One local seat's device-intent producer: held channel contributions, analog sticks, and toggled motion samples —
/// everything a seat's physical devices stage between ticks.
/// <see cref="HeldIntent"/> folds the producers into the per-tick <see cref="PlayerIntent"/> the client submits to the
/// authoritative server; <see cref="HeldChannels"/> is the always-overlay device-channel image riding the same
/// submission (composition ordinals only — movement roles ride <see cref="HeldIntent"/> directly). The seat's
/// authoritative body lives server-side — this type never integrates a pose.
/// </summary>
/// <remarks>Single-threaded: every mutator runs during the command pump's apply window and the per-tick submission
/// reads immediately after, both on the launcher's window-pump thread, so no lock guards this state.</remarks>
public sealed class SeatController {
    private static readonly FixedQ4816 NegativeOne = -FixedQ4816.One;

    private SeatLookSample m_look;
    // The analog producer's latest sample, routed from this tick's snapshot. InputRouter re-dispatches a carried analog
    // value every tick; ClearAnalog wipes this local staging state after the tick so only snapshot input can refill it.
    private SeatMoveSample m_move;
    // Presentation-only angular velocity from the seat's motion-input lane. Kept as the provider-neutral physical
    // sample (radians/second in the shared gamepad frame); WorldClient alone maps it to semantic look axes. Like the
    // sticks, it is consume-then-clear so a stopped/disconnected sensor cannot leave a stale turn behind.
    private Vector3 m_motionAngularVelocity;

    // The device-image fold primitive per channel ordinal: base zero, contributions are (control value × scale), no
    // pool, accumulate in RAW Int64 and clamp EXACTLY ONCE at the end. A saturating clamp per contribution is
    // commutative but NOT associative — order-dependent near the ceiling. Producing the device image IS the fold
    // primitive under a degenerate configuration, never a second merge
    // rule beside it — which is why holding W and S while a stick reports +0.3 still yields +0.3, where a sign-group
    // or max-of-group rule would have to be invented (and would get that case wrong) to reproduce it.
    // Keyed by (contributing control, ordinal) — the binding source, e.g. "keyboard.w", per channel it feeds — never
    // by (ordinal, scale) or by control alone: two controls sharing one ordinal at the SAME scale (W and a redundant
    // Up-arrow row) must hold INDEPENDENTLY, and one control bound to two channels (Q feeding both a ground and an
    // air action) must hold BOTH. Opposing scales on one ordinal (W=+1, S=-1) still cancel, by summing.
    private readonly Dictionary<(string Control, int Ordinal), FixedQ4816> m_heldControls = [];
    // The client copy of the seat's intent source: device edges and the held-intent submission run only under Live,
    // mirroring the server body's merge rule.
    private IntentSource m_source = IntentSource.Live;
    // The world's declared channel shapes — HeldChannels' only source for a composition ordinal's fold range
    // (bipolar/unipolar/binary). Defaults to the empty table (every composition ordinal falls back to the widest,
    // non-lossy bipolar range) so a seat built before the table is threaded in never silently drops a negative
    // contribution the way a hardcoded [0, One] used to.
    private WorldChannelTable m_channels = WorldChannelTable.Empty;

    /// <summary>The seat-lifetime logical view state shared by input, movement, every renderer, and read-back.</summary>
    public WorldSeatViewState View { get; } = new();

    /// <summary>Gets a value indicating whether <c>player.orbit</c> is held: pointer motion orbits the camera.</summary>
    public bool Orbiting { get; private set; }
    /// <summary>Whether any movement input is live this tick — a held row on a role ordinal or a deflected movement
    /// stick. A cheap read (no fold) for gates such as the follow camera's; <see cref="HeldIntent"/> is the fold.</summary>
    public bool MovementHeld {
        get {
            if (m_move.IsActive) {
                return true;
            }

            foreach (var (key, _) in m_heldControls) {
                if (m_channels.IsRole(ordinal: key.Ordinal)) {
                    return true;
                }
            }

            return false;
        }
    }
    /// <summary>The camera yaw a movement-facing stick or camera-framed channel row is composed against — latched
    /// when that movement begins and held until it stops. The action-strafe stick deliberately bypasses this latch
    /// and follows live look yaw. <see langword="null"/> while no latched producer moves. Presentation-side
    /// composition state, like the camera itself.</summary>
    public float? CameraFrameYaw { get; set; }
    /// <summary>Gets a value indicating whether <c>player.look.recenter</c> is held: the camera is driven round
    /// behind the body every tick, so it stays there while the body turns.</summary>
    public bool Recentering { get; private set; }
    /// <summary>Gets a value indicating whether the seat's motion-control mode is toggled on. This is the generic
    /// gate for sensor input; gyro look is its first consumer, while orientation/tilt movement can share the mode.</summary>
    public bool MotionControlsActive { get; private set; }
    /// <summary>This tick's provider-neutral angular velocity in radians per second. The gamepad frame is +X right,
    /// +Y up, +Z back; the camera adapter maps it to semantic look directions.</summary>
    public Vector3 MotionAngularVelocity => m_motionAngularVelocity;
    /// <summary>Whether this tick's Axis2D look sample carries horizontal input that faces the body along the camera's
    /// logical yaw. A held <see cref="FreeLooking"/> modifier suppresses it at read time, making chord/look dispatch
    /// order irrelevant.</summary>
    public bool LookFacesBody => (m_look.FacesBody && !FreeLooking);
    /// <summary>Whether the held free-look modifier is active. Camera look continues, but right-stick yaw does not
    /// write body heading and left-stick movement remains relative to authoritative heading until release.</summary>
    public bool FreeLooking { get; private set; }
    /// <summary>Gets a value indicating whether <c>player.steer</c> is held: pointer motion orbits the camera and the
    /// body faces where it looks (the seat composes the camera facing into the Face roles).</summary>
    public bool PointerSteering { get; private set; }

    /// <summary>Sets the <c>player.orbit</c> hold.</summary>
    /// <param name="held">Whether the command is held.</param>
    public void SetOrbit(bool held) {
        Orbiting = held;
    }
    /// <summary>Sets the <c>player.look.recenter</c> hold.</summary>
    /// <param name="held">Whether the command is held.</param>
    public void SetRecenter(bool held) {
        Recentering = held;
    }
    /// <summary>Sets the held right-stick free-look modifier.</summary>
    /// <param name="held">Whether free look is active.</param>
    public void SetFreeLook(bool held) {
        FreeLooking = held;
    }
    /// <summary>Sets the generic motion-control mode. Disabling it immediately drops the last sensor sample.</summary>
    /// <param name="active">Whether motion controls are active.</param>
    public void SetMotionControls(bool active) {
        MotionControlsActive = active;

        if (!active) {
            m_motionAngularVelocity = Vector3.Zero;
        }
    }
    /// <summary>Toggles the generic motion-control mode and returns its new state.</summary>
    /// <returns><see langword="true"/> when motion controls are now active.</returns>
    public bool ToggleMotionControls() {
        SetMotionControls(active: !MotionControlsActive);

        return MotionControlsActive;
    }
    /// <summary>Feeds the current angular-velocity sensor sample. Samples are accepted only while motion controls
    /// are toggled on, so background sensor noise cannot alter a seat that is using ordinary controls.</summary>
    /// <param name="angularVelocity">Provider-neutral radians per second.</param>
    public void SetMotionAngularVelocity(Vector3 angularVelocity) {
        if (
            MotionControlsActive &&
            float.IsFinite(f: angularVelocity.X) &&
            float.IsFinite(f: angularVelocity.Y) &&
            float.IsFinite(f: angularVelocity.Z)
        ) {
            m_motionAngularVelocity = angularVelocity;
        }
    }
    /// <summary>Sets the <c>player.steer</c> hold.</summary>
    /// <param name="held">Whether the command is held.</param>
    public void SetSteer(bool held) {
        PointerSteering = held;
    }

    /// <summary>Gets this tick's typed look sample; zero after <see cref="ClearAnalog"/> until routed input refills
    /// it.</summary>
    public SeatLookSample Look => m_look;
    /// <summary>Gets this tick's typed movement sample, already quantized at the router boundary. It is zero after
    /// <see cref="ClearAnalog"/> until routed input refills it; <c>player.sticks</c> is the only float echo.</summary>
    public SeatMoveSample Move => m_move;
    /// <summary>The world's declared channel table — resolves each composition ordinal's shape for
    /// <see cref="HeldChannels"/>'s end clamp. Set once by the roster from the same table the server compiled
    /// (<c>WorldServer.Population.Channels</c>); <see langword="null"/> is normalized to
    /// <see cref="WorldChannelTable.Empty"/>.</summary>
    public WorldChannelTable Channels {
        get => m_channels;
        set => m_channels = (value ?? WorldChannelTable.Empty);
    }
    /// <summary>This tick's live-held device-channel image, submitted alongside <see cref="HeldIntent"/> — derived
    /// from the SAME held-control set <see cref="HeldIntent"/> reads, restricted to non-role ordinals; movement roles
    /// ride <see cref="HeldIntent"/> directly, never this image. Every held control's contribution to
    /// one ordinal sums in raw <see cref="FixedQ4816"/> storage, then runs through
    /// <see cref="FixedContributionFold.Evaluate"/> once against that ordinal's declared shape
    /// range (bipolar <c>[-One, One]</c>; unipolar or binary <c>[0, One]</c> — a binary channel's PRE-quantization
    /// pool-clamp domain per its own remarks, since bit-quantization is the server's job, never the client's).</summary>
    public PlayerIntent HeldChannels {
        get {
            if (m_heldControls.Count == 0) {
                return default;
            }

            Span<long> raw = stackalloc long[ChannelLimits.MaxChannels];

            foreach (var ((_, ordinal), scale) in m_heldControls) {
                if (m_channels.IsRole(ordinal: ordinal)) {
                    continue;
                }

                raw[ordinal] += scale.Value;
            }

            var channels = default(ChannelValues);

            for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
                if (m_channels.IsRole(ordinal: ordinal)) {
                    continue;
                }
                if (raw[ordinal] == 0L) {
                    continue;
                }

                var shape = (m_channels.IsDeclared(ordinal: ordinal)
                    ? m_channels.Shape(ordinal: ordinal)
                    : ChannelShape.Bipolar
                );

                var (minimum, maximum, _) = WorldChannelTable.CompileFoldShape(
                    shape: shape,
                    threshold: m_channels.Threshold(ordinal: ordinal)
                );

                // A seat's held device image is the no-pool specialization: zero baseline, the completed raw device
                // sum in the pool-delta slot, no outside-pool term, and deliberately no binary threshold. Binary is
                // continuous [0, One] here; authoritative composition performs the terminal bit quantization.
                channels[ordinal] = FixedContributionFold.Evaluate(
                    baseline: FixedQ4816.Zero,
                    poolDeltaRaw: raw[ordinal],
                    outsidePoolDeltaRaw: 0L,
                    poolRadius: null,
                    minimum: minimum,
                    maximum: maximum,
                    threshold: null,
                    poolClamped: out _
                );
            }

            return new PlayerIntent(Channels: channels);
        }
    }
    /// <summary>The profile this seat selects — the client-side identity (color and look-invert). The server body holds
    /// its own reference for speeds, assigned over the session wire.</summary>
    public WorldIdentity? Profile { get; set; }
    /// <summary>The seat's client-side intent-source copy (matches the server body's; both are written by
    /// <c>player.control</c>).</summary>
    public IntentSource Source => m_source;

    private static FixedQ4816 ClampedRaw(ReadOnlySpan<long> raw, int ordinal) => ((ordinal >= 0)
        ? FixedQ4816.Clamp(
            value: FixedQ4816.FromRawBits(value: raw[ordinal]),
            minimum: NegativeOne,
            maximum: FixedQ4816.One
        )
        : FixedQ4816.Zero
    );

    /// <summary>Wipes the tick-local stick and motion samples to zero after their consumers run. Active routed axes
    /// refill them on the next tick; an inactive or disconnected source therefore cannot leave a stale deflection
    /// or angular velocity behind.</summary>
    public void ClearAnalog() {
        m_move = default;
        m_look = default;
        m_motionAngularVelocity = Vector3.Zero;
    }
    /// <summary>Folds the held-control set — every channel ROW held on a role ordinal — into the tick's submitted
    /// intent: peers summed then clamped, so opposing rows cancel and two rows never exceed full deflection. The
    /// stick's <c>player.move</c>/<c>player.move.strafe</c> sample is deliberately NOT in this fold: the rows and the stick may be authored in
    /// different frames (<see cref="WorldChannelTable.MoveFrame"/> for the rows; the stick is camera-framed by its
    /// verb's definition), so <c>WorldClient.ComposeMoveFrame</c> rotates each into world axes and sums them there —
    /// read the stick from <see cref="Move"/>. The right stick never writes authoritative Turn. All six role
    /// channels fold identically (MoveUp/Pitch/Roll alongside the original three) — a <c>WorldBody</c> running a
    /// grounded body motion program simply never reads the extra three, exactly like an unbound composition channel;
    /// a document declaring them (required for a free-attitude body motion program, see
    /// <c>WorldDefinitionValidator</c>) is the only way they drive anything, so wiring them through here never
    /// changes Grounded behavior.</summary>
    public PlayerIntent HeldIntent() {
        // No rows held (the common case — an idle seat, or a stick-only one): nothing to fold.
        if (m_heldControls.Count == 0) {
            return default;
        }

        // The role-channel fold primitive, mirroring HeldChannels' vector accumulate above: sum every held role
        // contribution into raw[ordinal] (RAW Int64, no per-add clamp — see HeldChannels' remarks on why a saturating
        // clamp per contribution is order-dependent), then clamp each role EXACTLY ONCE below.
        // [-One, One] is safe on every role ordinal below because every role channel IS bipolar by validator rule
        // (WorldDefinitionValidator.ValidateChannels refuses any other declared shape on a role channel).
        Span<long> raw = stackalloc long[ChannelLimits.MaxChannels];
        var roles = m_channels.RoleOrdinals;

        foreach (var ((_, ordinal), scale) in m_heldControls) {
            if (!m_channels.IsRole(ordinal: ordinal)) {
                continue;
            }

            raw[ordinal] += scale.Value;
        }

        return roles.Intent(
            moveAdvance: ClampedRaw(
                raw: raw,
                ordinal: roles.MoveAdvance
            ),
            moveStrafe: ClampedRaw(
                raw: raw,
                ordinal: roles.MoveStrafe
            ),
            turn: ClampedRaw(
                raw: raw,
                ordinal: roles.Turn
            ),
            moveUp: ClampedRaw(
                raw: raw,
                ordinal: roles.MoveUp
            ),
            pitch: ClampedRaw(
                raw: raw,
                ordinal: roles.Pitch
            ),
            roll: ClampedRaw(
                raw: raw,
                ordinal: roles.Roll
            )
        );
    }
    /// <summary>Asserts a channel contribution as held, keyed by (control, ordinal) — so a second physical control
    /// sharing this ordinal (even at the identical scale) holds independently of the first, one control feeding several
    /// channels holds each of them, and an analog control's magnitude updates in place every re-dispatch tick without
    /// leaking a stale (ordinal, scale) pair under a different key. Idempotent per (control, ordinal) — a key held down
    /// and auto-repeating (or an unchanged analog re-dispatch) re-asserts the same entry with no effect.</summary>
    /// <param name="controlId">The contributing control's identity — the binding source (e.g. <c>"keyboard.w"</c>);
    /// synthesized bindings provide a stable logical destination id; <see langword="null"/> or empty is normalized
    /// to a shared fallback key only for a caller with no binding owner.</param>
    /// <param name="ordinal">The channel ordinal this control contributes to.</param>
    /// <param name="scale">This control's current scale/sample (e.g. <c>+One</c> for W, <c>-One</c> for S on the same "forward" ordinal).</param>
    public void HoldChannel(string? controlId, int ordinal, FixedQ4816 scale) {
        m_heldControls[((controlId ?? string.Empty), ordinal)] = scale;
    }
    /// <summary>Releases every held movement contribution and live-held composition channel. Called when a
    /// possession/engagement latch transitions, when the keyboard leaves this seat (a still-down key's release edge
    /// routes to the keyboard's new slot, so the source would walk forever), and by <c>player.stop</c>'s seat half.</summary>
    public void ReleaseAllHeld() {
        // A single Clear covers both movement and composition holds — a still-down Space would otherwise stick the
        // jump channel held, exactly the hazard clearing only the movement set would reintroduce.
        m_heldControls.Clear();
        Orbiting = false;
        Recentering = false;
        MotionControlsActive = false;
        m_motionAngularVelocity = Vector3.Zero;
        m_look = default;
        m_move = default;
        FreeLooking = false;
        PointerSteering = false;
        CameraFrameYaw = null;
    }
    /// <summary>Releases every channel contribution held under <paramref name="controlId"/>. A no-op if that control
    /// holds nothing — in particular, releasing one control never touches a DIFFERENT control's entry, even one on
    /// the same ordinal at the same scale (see <see cref="HoldChannel"/>).</summary>
    /// <param name="controlId">The releasing control's identity, matching the one <see cref="HoldChannel"/> was called with.</param>
    public void ReleaseChannel(string? controlId) {
        var control = (controlId ?? string.Empty);

        foreach (var key in m_heldControls.Keys) {
            if (key.Control == control) {
                _ = m_heldControls.Remove(key: key);
            }
        }
    }
    /// <summary>Feeds this frame's look sample: +X looks right and +Y looks up. The binding chooses free orbit or
    /// camera-facing body steering; neither path writes Turn. Same consume-then-clear and already-quantized-at-the-door
    /// contract as <see cref="SetAnalogMove"/>.</summary>
    /// <param name="look">The already-quantized look stick sample (+X looks right, +Y looks up).</param>
    /// <param name="behavior">How horizontal look affects body facing.</param>
    public void SetAnalogLook(FixedVector2 look, SeatLookBehavior behavior) {
        m_look = new SeatLookSample(
            Behavior: behavior,
            Value: look
        );
    }
    /// <summary>Feeds this frame's movement (left) stick sample, already quantized to fixed point at the router seam
    /// (see <see cref="Puck.Commands.CommandValueQuantization.QuantizeAxis"/>) and deadzoned/normalized to <c>[-1, 1]</c>
    /// by the platform layer (+Y forward, +X strafe right). Set by the roster's per-device router while a dispatch is
    /// live; a centered stick emits no dispatch, so the value is wiped by <see cref="ClearAnalog"/> each frame
    /// (consume-then-clear, so a disconnected pad never leaves a stale deflection behind). No float conversion
    /// happens here — the value arrives already quantized, once, and is stored verbatim.</summary>
    /// <param name="move">The already-quantized movement stick sample.</param>
    /// <param name="behavior">How the sample affects body facing.</param>
    public void SetAnalogMove(FixedVector2 move, SeatMoveBehavior behavior) {
        m_move = new SeatMoveSample(
            Behavior: behavior,
            Value: move
        );
    }
    /// <summary>Sets the client-side intent-source copy — <c>player.control</c>'s seat half (the server body's axis is
    /// written by the same command). A transition drops the live device holds via <see cref="ReleaseAllHeld"/>, so
    /// nothing leaks through a source switch or bursts when Live returns. A no-op if the source is unchanged.</summary>
    /// <param name="source">The intent source to latch.</param>
    public void SetIntentSource(IntentSource source) {
        if (source == m_source) {
            return;
        }

        m_source = source;
        ReleaseAllHeld();
    }
}
