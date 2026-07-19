using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// One local seat's device-intent producer: the held movement axes, the analog stick samples, the live-held action
/// lanes, and the client-side possession latch — everything a seat's physical devices stage between ticks.
/// <see cref="HeldIntent"/> folds the producers into the per-tick <see cref="PlayerIntent"/> the client submits to the
/// authoritative server; <see cref="HeldLanes"/> is the always-overlay device-lane image riding the same submission.
/// The seat's authoritative body lives server-side — this type never integrates a pose.
/// </summary>
/// <remarks>Single-threaded: every mutator runs during the command pump's apply window and the per-tick submission
/// reads immediately after, both on the launcher's window-pump thread, so no lock guards this state.</remarks>
internal sealed class SeatController {
    /// <summary>The held-axis key for forward motion (W / Up).</summary>
    public const string AxisForward = "forward";
    /// <summary>The held-axis key for backward motion (S / Down).</summary>
    public const string AxisBack = "back";
    /// <summary>The held-axis key for strafing left (Q).</summary>
    public const string AxisStrafeLeft = "strafe-left";
    /// <summary>The held-axis key for strafing right (E).</summary>
    public const string AxisStrafeRight = "strafe-right";
    /// <summary>The held-axis key for turning left (A / Left).</summary>
    public const string AxisTurnLeft = "turn-left";
    /// <summary>The held-axis key for turning right (D / Right).</summary>
    public const string AxisTurnRight = "turn-right";

    private static readonly FixedQ4816 s_negativeOne = -FixedQ4816.One;
    // The live held axes (see the Axis* keys). A HashSet so Hold is idempotent under key auto-repeat, and Release is a
    // no-op for an axis already up.
    private readonly HashSet<string> m_held = new(comparer: StringComparer.Ordinal);
    // The analog producer's latest sample, routed from this tick's snapshot. InputRouter re-dispatches a carried analog
    // value every tick; ClearAnalog wipes this local staging state after the tick so only snapshot input can refill it.
    private FixedQ4816 m_analogMoveX;
    private FixedQ4816 m_analogMoveY;
    private FixedQ4816 m_analogLookX;
    private FixedQ4816 m_analogLookY;
    // The live-held action lanes (a button down until its release edge) — submitted each tick as the device lane
    // image, so a held jump rides a tape-driven runner (the server admits it under Live only).
    private ActionLanes m_lanesHeld;
    // The client copy of the seat's intent source: device edges and the held-intent submission run only under Live,
    // mirroring the server body's merge rule.
    private IntentSource m_source = IntentSource.Live;

    /// <summary>The profile this seat selects — the client-side identity (color and look-invert). The server body holds
    /// its own reference for speeds, assigned over the session wire.</summary>
    public WorldProfile? Profile { get; set; }

    /// <summary>The seat's client-side intent-source copy (matches the server body's; both are written by
    /// <c>player.control</c>).</summary>
    public IntentSource Source => m_source;

    /// <summary>This tick's live-held device-lane image, submitted alongside <see cref="HeldIntent"/>.</summary>
    public ActionLanes HeldLanes => m_lanesHeld;

    /// <summary>This frame's movement-stick sample (left stick), for the <c>player.sticks</c> observability verb.
    /// Zero once <see cref="ClearAnalog"/> has run for the frame — a live read only sees a non-zero value while a
    /// routed dispatch has set it earlier in the same command pump (i.e. the stick is actively deflected).</summary>
    public Vector2 AnalogMove => new(x: (float)(double)m_analogMoveX, y: (float)(double)m_analogMoveY);
    /// <summary>This frame's look-stick sample (right stick); see <see cref="AnalogMove"/> for the freshness caveat.</summary>
    public Vector2 AnalogLook => new(x: (float)(double)m_analogLookX, y: (float)(double)m_analogLookY);

    /// <summary>Asserts a movement axis as held (one of the <c>Axis*</c> keys). Idempotent — a key held down and
    /// auto-repeating re-asserts the same axis with no effect.</summary>
    /// <param name="axis">The axis key to hold; an unrecognized key is stored but never read by the intent merge.</param>
    public void Hold(string axis) {
        _ = m_held.Add(item: axis);
    }
    /// <summary>Releases a movement axis previously held by <see cref="Hold"/>. A no-op if the axis is not held.</summary>
    /// <param name="axis">The axis key to release.</param>
    public void Release(string axis) {
        _ = m_held.Remove(item: axis);
    }
    /// <summary>Presses an action lane on its live edge — the keyboard/pad path, no timer: the lane reads held from now
    /// until the matching <see cref="ReleaseLaneEdge"/>, which is what gives variable jump height from a live control.
    /// Idempotent — a held button auto-repeating re-asserts the same bit, so the jump's rising edge (detected server-side
    /// against the previous sub-step) fires once per press, not once per repeat.</summary>
    /// <param name="lane">The lane to hold live.</param>
    public void PressLaneEdge(ActionLanes lane) {
        // The device path (player.jump/player.south). Ignored unless the seat's source is Live, so a human's pad/keyboard
        // jump cannot reach the avatar mid-takeover; the wire twin (player.press) is unaffected and overlays regardless.
        if (m_source != IntentSource.Live) {
            return;
        }

        m_lanesHeld |= lane;
    }
    /// <summary>Releases a live-held action lane (the button's up edge). A no-op for a lane not live-held; leaves any
    /// timed press (<c>player.press</c>) on the same lane running server-side.</summary>
    /// <param name="lane">The lane to release.</param>
    public void ReleaseLaneEdge(ActionLanes lane) {
        // Same source mask as PressLaneEdge — a device release edge is inert off-Live (the matching press was masked
        // too).
        if (m_source != IntentSource.Live) {
            return;
        }

        m_lanesHeld &= ~lane;
    }
    /// <summary>Feeds this frame's movement (left) stick sample, already deadzoned/normalized to <c>[-1, 1]</c> by the
    /// platform layer (+Y forward, +X strafe right). Set by the roster's per-device router while a dispatch is live; a
    /// centered stick emits no dispatch, so the value is wiped by <see cref="ClearAnalog"/> each frame (consume-then-clear,
    /// so a disconnected pad never leaves a stale deflection behind).</summary>
    /// <param name="move">The movement stick sample.</param>
    public void SetAnalogMove(Vector2 move) {
        m_analogMoveX = FixedQ4816.FromDouble(value: move.X);
        m_analogMoveY = FixedQ4816.FromDouble(value: move.Y);
    }
    /// <summary>Feeds this frame's look (right) stick sample (+X turns right — folded into the intent's Turn with the
    /// same sign the turn-right key uses). Same consume-then-clear contract as <see cref="SetAnalogMove"/>.</summary>
    /// <param name="look">The look stick sample.</param>
    public void SetAnalogLook(Vector2 look) {
        m_analogLookX = FixedQ4816.FromDouble(value: look.X);
        m_analogLookY = FixedQ4816.FromDouble(value: look.Y);
    }
    /// <summary>Wipes both analog samples to zero. Called once per frame AFTER the tick's submission has consumed them:
    /// a centered stick dispatches nothing, so its last value must not persist into the next frame.</summary>
    public void ClearAnalog() {
        m_analogMoveX = FixedQ4816.Zero;
        m_analogMoveY = FixedQ4816.Zero;
        m_analogLookX = FixedQ4816.Zero;
        m_analogLookY = FixedQ4816.Zero;
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
    /// <summary>Releases every held movement axis and live-held action lane. Called when a possession/engagement latch
    /// transitions, when the keyboard leaves this seat (a still-down key's release edge routes to the keyboard's new
    /// slot, so the source would walk forever), and by <c>player.stop</c>'s seat half.</summary>
    public void ReleaseAllHeld() {
        m_held.Clear();
        // The action buttons are held input too: a still-down Space would otherwise stick the jump lane held.
        m_lanesHeld = ActionLanes.None;
    }
    /// <summary>Folds the live producers — the held-key set and the analog sticks — into the tick's submitted intent:
    /// peers summed then clamped, so opposing inputs cancel and a key plus a full stick never exceeds full deflection.
    /// Stick up (+Y) is forward; look-stick right (+X) turns right, i.e. the negative Turn direction (matching the
    /// turn-right key).</summary>
    public PlayerIntent HeldIntent() {
        // The look-stick's turn contribution, its X sign flipped when the seated profile asks to invert look-X.
        var lookX = ((Profile?.InvertLookX == true) ? -m_analogLookX : m_analogLookX);

        // No keys held (the common case — an idle seat): skip the six HashSet.Contains probes and fold the analog
        // sample straight through.
        if (m_held.Count == 0) {
            return new PlayerIntent(
                MoveForward: FixedQ4816.Clamp(value: m_analogMoveY, minimum: s_negativeOne, maximum: FixedQ4816.One),
                MoveStrafe: FixedQ4816.Clamp(value: m_analogMoveX, minimum: s_negativeOne, maximum: FixedQ4816.One),
                Turn: FixedQ4816.Clamp(value: -lookX, minimum: s_negativeOne, maximum: FixedQ4816.One)
            );
        }

        return new PlayerIntent(
            MoveForward: FixedQ4816.Clamp(value: ((Axis(key: AxisForward) - Axis(key: AxisBack)) + m_analogMoveY), minimum: s_negativeOne, maximum: FixedQ4816.One),
            MoveStrafe: FixedQ4816.Clamp(value: ((Axis(key: AxisStrafeRight) - Axis(key: AxisStrafeLeft)) + m_analogMoveX), minimum: s_negativeOne, maximum: FixedQ4816.One),
            Turn: FixedQ4816.Clamp(value: ((Axis(key: AxisTurnLeft) - Axis(key: AxisTurnRight)) - lookX), minimum: s_negativeOne, maximum: FixedQ4816.One)
        );
    }
    private FixedQ4816 Axis(string key) {
        return (m_held.Contains(item: key) ? FixedQ4816.One : FixedQ4816.Zero);
    }
}
