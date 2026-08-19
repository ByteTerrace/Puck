using System.Numerics;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// One seat's FLY control application — a free camera driven by the seat's own move/look samples and its
/// <see cref="ChannelRole.MoveUp"/>-role held channel, the SAME general input a body seat's channels carry (see
/// <see cref="SeatController.Move"/>/<see cref="SeatController.Look"/>/<see cref="SeatController.HeldIntent"/>).
/// Activated/deactivated by <c>player.mode</c> composing a <see cref="WorldSeatModeState.Target"/> of <c>"camera"</c>
/// (in <c>PlayerCommandModule.Mode.cs</c>, alongside the existing <c>player.control</c> idle diversion this class
/// knows nothing about) — the engine's only knowledge of "why" a seat is flying. Replaces the deleted
/// <c>WorldEditorSession</c>: no camera-mode toggle, no live speed step, no console pose-teleport — the fly rig
/// always runs at <c>views.flyRig.motion</c>'s authored <c>defaultSpeed</c>, steppable only by
/// <c>world.row.step views.flyRig.motion.defaultSpeed</c> like any other document field.
/// </summary>
/// <remarks>Single-threaded, like every input-fold type here: the mode verb runs during the command pump's apply
/// window and <see cref="ResolveRig"/> runs during frame produce, both on the launcher's window-pump thread, so no
/// lock guards this state.</remarks>
public sealed class WorldSeatFlyRig(PlayerRoster roster) {
    // The world-space reach of Focus along the look ray — where a spawn ghost lands and proximity candidates sort
    // from. A presentation constant, not authored: unrelated to the fly rig's own speed/look tunables, and the
    // candidate policy itself (radius, cap) is already authored on WorldAuthoringDefaults.
    private const float FocusDistance = 6f;

    private readonly PlayerRoster m_roster = roster;

    private sealed class Seat {
        public bool Active;
        public Vector3 Eye;
        public float Pitch;
        public bool SeedPending;
        public float Yaw;
        public readonly FixedRig Rig = new();
    }

    private readonly Seat[] m_seats = CreateSeats();

    private static Seat[] CreateSeats() {
        var seats = new Seat[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < seats.Length); slot++) {
            seats[slot] = new Seat();
        }

        return seats;
    }
    // Look-stick contribution: yaw/pitch integrated at the authored rate with the same sign-preserving quadratic
    // response every stick-driven camera in this codebase uses (fine control near center, full authority at the rim).
    private static void AdvanceLook(Seat seat, WorldCameraMotion.Fly motion, Vector2 look, float deltaSeconds) {
        seat.Yaw -= ((Response(value: look.X) * motion.LookRateRadiansPerSecond) * deltaSeconds);
        seat.Pitch = Math.Clamp(
            value: (seat.Pitch + ((Response(value: look.Y) * motion.LookRateRadiansPerSecond) * deltaSeconds)),
            min: -motion.MaxPitchRadians,
            max: motion.MaxPitchRadians
        );
    }
    private static Vector3 LookDirection(float yaw, float pitch) {
        var cosPitch = MathF.Cos(x: pitch);

        return new Vector3(
            x: (MathF.Sin(x: yaw) * cosPitch),
            y: MathF.Sin(x: pitch),
            z: (MathF.Cos(x: yaw) * cosPitch)
        );
    }
    private static float Response(float value) => (value * MathF.Abs(x: value));
    private static void SetLookToward(Seat seat, Vector3 target) {
        var direction = (target - seat.Eye);

        if (direction.LengthSquared() < 1e-8f) {
            return;
        }

        direction = Vector3.Normalize(value: direction);
        seat.Yaw = MathF.Atan2(
            x: direction.Z,
            y: direction.X
        );
        seat.Pitch = MathF.Asin(x: Math.Clamp(
            value: direction.Y,
            min: -1f,
            max: 1f
        ));
    }
    private int SlotOrFirst(int slot) => ((((uint)slot) < ((uint)m_seats.Length))
        ? slot
        : 0
    );
    // The pre-rotation unit-disc clamp WorldClient.ComposeMoveFrame's own fold applies (two full keys are one
    // direction at full speed) — kept identical here so the fly rig's move fold matches a body's exactly.
    private static void UnitDisc(ref float forward, ref float strafe) {
        var length = MathF.Sqrt(x: ((forward * forward) + (strafe * strafe)));

        if (length > 1f) {
            forward /= length;
            strafe /= length;
        }
    }

    /// <summary>Activates the seat's fly application: arms the camera to seed from the CURRENT chase framing on the
    /// next resolved frame (no pose pop). Idempotent while already active.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public void Activate(int slot) {
        if (((uint)slot) >= ((uint)m_seats.Length)) {
            return;
        }

        var seat = m_seats[slot];

        seat.Active = true;
        seat.SeedPending = true;
    }
    /// <summary>Deactivates the seat's fly application and clears its selection (the tools' self-heal hook — see
    /// <see cref="SelectionReset"/>). A friendly no-op while already inactive.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public void Deactivate(int slot) {
        if (
            (((uint)slot) >= ((uint)m_seats.Length)) ||
            !m_seats[slot].Active
        ) {
            return;
        }

        m_seats[slot].Active = false;
        SelectionReset?.Invoke(obj: slot);
    }
    /// <summary>The seat's current fly eye position — the AUTHORED pose (advanced through the most recent resolve),
    /// not a frame-resolved read, so a verb batch that picks/places in the same pump window reads the fresh pose
    /// without waiting for a produced frame.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3 Eye(int slot) => m_seats[SlotOrFirst(slot: slot)].Eye;
    /// <summary>The seat's fly look direction (the pick ray).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3 Facing(int slot) {
        var seat = m_seats[SlotOrFirst(slot: slot)];

        return LookDirection(
            pitch: seat.Pitch,
            yaw: seat.Yaw
        );
    }
    /// <summary>The seat's fly focus point — a fixed reach along the look ray (where a spawn ghost lands, and the
    /// proximity-candidate sort origin).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3 Focus(int slot) => (Eye(slot: slot) + (Facing(slot: slot) * FocusDistance));
    /// <summary>Returns a value indicating whether the seat's fly application is currently active.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public bool IsFlying(int slot) => ((((uint)slot) < ((uint)m_seats.Length)) && m_seats[slot].Active);
    /// <summary>Returns the shared command refusal when a seat's fly application is not active, or
    /// <see langword="null"/> when it is.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="verb">The command name for the refusal text.</param>
    public CommandResult? NotFlyingError(int slot, string verb) {
        if (IsFlying(slot: slot)) {
            return null;
        }

        return CommandResult.Error(output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not flying]");
    }
    /// <summary>Self-heals a departed seat: a slot that left the roster while flying is force-deactivated so a later
    /// join never inherits a stale fly pose or selection. Called once per produced frame.</summary>
    public void PruneDeparted() {
        for (var slot = 0; (slot < m_seats.Length); slot++) {
            if (
                m_seats[slot].Active &&
                !m_roster.IsJoined(slot: slot)
            ) {
                Deactivate(slot: slot);
            }
        }
    }
    /// <summary>Resolves the rig that frames a seat this frame: <paramref name="chase"/> unchanged (the SAME
    /// instance) while the seat is not flying, else the fly rig advanced by this frame's presentation delta (seeding
    /// from the chase framing on the first frame after <see cref="Activate"/>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="chase">The seat's chase rig (the non-flying default).</param>
    /// <param name="flyRig">The world-authored fly rig (<c>views.flyRig</c>), or <see langword="null"/> for a world
    /// that authors none — falls back to <paramref name="chase"/> even while <see cref="IsFlying"/>, since there is
    /// nothing to fly with (the validator refuses a document that would reach this any other way).</param>
    /// <param name="anchor">The seat avatar's render pose this frame (the seed subject).</param>
    /// <param name="time">The presentation clock, seconds.</param>
    /// <param name="deltaSeconds">The clamped presentation interval to integrate camera motion by.</param>
    /// <returns>The rig to resolve the seat's camera with this frame.</returns>
    public ISdfCameraRig ResolveRig(int slot, ISdfCameraRig chase, WorldCameraRig? flyRig, in SdfAnchor anchor, float time, float deltaSeconds) {
        if (
            (((uint)slot) >= ((uint)m_seats.Length)) ||
            !m_seats[slot].Active ||
            (flyRig?.Motion is not WorldCameraMotion.Fly motion)
        ) {
            return chase;
        }

        var seat = m_seats[slot];

        if (seat.SeedPending) {
            var clock = new SdfCameraClock(
                AuthoritativeTick: 0UL,
                PresentationSeconds: time
            );

            var (chaseEye, chaseTarget, _) = chase.Resolve(
                anchor: in anchor,
                clock: in clock
            );

            seat.Eye = chaseEye;
            SetLookToward(
                seat: seat,
                target: chaseTarget
            );
            seat.SeedPending = false;
        }

        if (m_roster.Seat(slot: slot) is { } controller) {
            var heldIntent = controller.HeldIntent();
            var roles = controller.Channels.RoleOrdinals;
            var rowForward = ((float)((double)roles.Read(
                intent: in heldIntent,
                role: ChannelRole.MoveAdvance
            )));
            var rowStrafe = ((float)((double)roles.Read(
                intent: in heldIntent,
                role: ChannelRole.MoveStrafe
            )));
            var vertical = ((float)((double)roles.Read(
                intent: in heldIntent,
                role: ChannelRole.MoveUp
            )));
            var stick = controller.Move.Value;
            var stickForward = ((float)((double)stick.Y));
            var stickStrafe = ((float)((double)stick.X));

            UnitDisc(
                forward: ref rowForward,
                strafe: ref rowStrafe
            );
            UnitDisc(
                forward: ref stickForward,
                strafe: ref stickStrafe
            );

            var look = controller.Look.Value;

            AdvanceLook(
                deltaSeconds: deltaSeconds,
                look: new Vector2(
                    x: ((float)((double)look.X)),
                    y: ((float)((double)look.Y))
                ),
                motion: motion,
                seat: seat
            );

            var forward = LookDirection(
                pitch: seat.Pitch,
                yaw: seat.Yaw
            );
            var right = Vector3.Normalize(value: Vector3.Cross(
                vector1: forward,
                vector2: Vector3.UnitY
            ));
            var velocity = (((forward * Response(value: (rowForward + stickForward))) + (right * Response(value: (rowStrafe + stickStrafe)))) + (Vector3.UnitY * vertical));

            seat.Eye += (velocity * (motion.DefaultSpeed * deltaSeconds));
        }

        seat.Rig.Eye = seat.Eye;
        seat.Rig.Target = (seat.Eye + LookDirection(
            pitch: seat.Pitch,
            yaw: seat.Yaw
        ));
        seat.Rig.FovRadians = flyRig!.Lens.FieldOfViewRadians;

        return seat.Rig;
    }

    /// <summary>The selection-clear sink <see cref="Deactivate"/> invokes so a deactivated or departed seat never
    /// leaves a stale selection for the next occupant. Property-injected (the targeting state is composed after this
    /// rig).</summary>
    public Action<int>? SelectionReset { get; set; }
}
