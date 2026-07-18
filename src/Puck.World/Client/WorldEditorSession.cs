using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The editor camera's shape: a free-fly pose driven directly by the sticks, or an orbit around the live
/// selection (falling back to the seat's avatar while nothing is selected).</summary>
internal enum EditorCameraMode {
    Fly,
    Orbit,
}

/// <summary>The outcome of an <see cref="WorldEditorSession.Enter"/>/<see cref="WorldEditorSession.Exit"/> attempt,
/// echoed by the <c>editor.enter</c>/<c>editor.exit</c> verbs.</summary>
internal enum EditorModeOutcome {
    Applied,
    AlreadyThere,
    NotJoined,
    Pending,
}

/// <summary>
/// One seat's per-session editor mode — the client-side owner of everything entering the editor changes: the seat's
/// ACTIVE binding group (<see cref="WorldSeatBindings.SetActiveGroup"/> onto <see cref="WorldEditorBindings.GroupId"/>
/// — a pointer flip on the compiled profile, no recompose), its intent diversion (the seat goes
/// <see cref="IntentSource.Idle"/> client-side AND server-side over the existing <c>SetControl</c> wire — the honest
/// idle: live device input is masked while tapes/<c>player.press</c> still drive, exactly the <c>player.control idle</c>
/// contract), and its camera (a free-fly/orbit rig swapped in for the chase rig, integrated at presentation cadence
/// from the pad samples the <c>editor.stick.move/look</c> routers stage). Exit restores the seat's prior intent source
/// and flips the group back; the chase rig re-anchors deterministically to the avatar, so there is no pose to restore.
/// </summary>
/// <remarks>Single-threaded, like every input-fold type here: the verb/router mutators run during the command pump's
/// apply window, <see cref="LatchTick"/> runs in the simulation's finish phase, and <see cref="ResolveRig"/> runs
/// during frame produce — all on the launcher's window-pump thread, so no lock guards this state. Stick samples are
/// two-phase latched (staged by each tick's routed dispatch, promoted by <see cref="LatchTick"/>) so per-frame camera
/// integration between 32 Hz ticks reads a stable deflection instead of a consume-then-clear zero.</remarks>
internal sealed class WorldEditorSession {
    private const float DefaultFlySpeed = 8f;
    private const float MinFlySpeed = 0.5f;
    private const float MaxFlySpeed = 64f;
    private const float SpeedStepFactor = 1.5f;
    private const float LookRateRadiansPerSecond = 2.6f;
    private const float MaxPitchRadians = 1.45f;
    private const float DefaultOrbitDistance = 6f;
    private const float MinOrbitDistance = 1.5f;
    private const float MaxOrbitDistance = 60f;
    private const float OrbitZoomRatePerSecond = 1.6f;
    // The orbit pivot sits at the avatar's chest height, matching the chase rig's target lift.
    private static readonly Vector3 s_orbitPivotLift = new(x: 0f, y: 1f, z: 0f);

    // The world-space reach of Focus along the look ray — where a spawn ghost lands and proximity candidates sort from.
    private const float FocusDistance = 6f;

    private readonly PlayerRoster m_roster;
    private readonly WorldSeatBindings m_bindings;
    private readonly IServerLink m_link;
    private readonly WorldEditorDrag m_drag;
    private readonly Seat[] m_seats;

    // One seat's mode state: the camera pose/mode, the two-phase stick latches, the vertical holds, and the intent
    // source to restore on exit. A mutable class per seat so the per-frame paths never copy.
    private sealed class Seat {
        public bool Active;
        public bool SeedPending;
        public EditorCameraMode Mode;
        public IntentSource PriorSource;
        public float Speed = DefaultFlySpeed;
        // Fly pose: eye + look angles (yaw 0 looks down +Z, the OrbitRig convention; pitch positive looks up).
        public Vector3 Eye;
        public float Yaw;
        public float Pitch;
        // Orbit authoring state (the pivot resolves per frame: selection first, seat avatar fallback).
        public float OrbitYaw;
        public float OrbitPitch = 0.5f;
        public float OrbitDistance = DefaultOrbitDistance;
        // Two-phase stick latches: handlers stage during the tick's apply window; LatchTick promotes to active.
        public Vector2 StagedMove;
        public Vector2 StagedLook;
        public Vector2 ActiveMove;
        public Vector2 ActiveLook;
        public bool AscendHeld;
        public bool DescendHeld;
        // The rig handed to the frame source while editing — its Eye/Target are rewritten every resolved frame.
        public readonly FixedRig Rig = new();
    }

    /// <summary>Initializes a new instance of the <see cref="WorldEditorSession"/> class.</summary>
    /// <param name="roster">The participant roster (seat liveness and controllers).</param>
    /// <param name="bindings">The per-seat binding resolver the mode layer enters/leaves.</param>
    /// <param name="link">The server link the intent-source diversion rides (the existing <c>SetControl</c> wire).</param>
    /// <param name="drag">The drag preview channel: while a seat's drag is live, its latched sticks translate the
    /// pending row instead of flying the camera.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldEditorSession(PlayerRoster roster, WorldSeatBindings bindings, IServerLink link, WorldEditorDrag drag) {
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: drag);

        m_roster = roster;
        m_bindings = bindings;
        m_link = link;
        m_drag = drag;
        m_seats = new Seat[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            m_seats[slot] = new Seat();
        }
    }

    /// <summary>Whether the seat is currently in editor mode.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public bool IsEditing(int slot) => (((uint)slot < (uint)m_seats.Length) && m_seats[slot].Active);

    /// <summary>The seat's editor camera mode (meaningful while editing).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public EditorCameraMode Mode(int slot) => m_seats[SlotOrFirst(slot: slot)].Mode;

    /// <summary>The seat's fly speed, world units per second.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public float Speed(int slot) => m_seats[SlotOrFirst(slot: slot)].Speed;

    /// <summary>The seat's current editor eye position — the AUTHORED fly pose (which the orbit trails every frame),
    /// not the frame-resolved rig, so a verb batch that poses the camera and then picks/places in the same pump window
    /// reads the fresh pose without waiting for a produced frame.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3 Eye(int slot) => m_seats[SlotOrFirst(slot: slot)].Eye;

    /// <summary>The seat's look direction (the pick ray; valid in both camera modes — the fly pose trails the orbit).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3 Facing(int slot) {
        var seat = m_seats[SlotOrFirst(slot: slot)];

        return LookDirection(yaw: seat.Yaw, pitch: seat.Pitch);
    }

    /// <summary>The seat's editor focus point — a fixed reach along the look ray (where a spawn ghost lands, and the
    /// proximity-candidate sort origin).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3 Focus(int slot) => (Eye(slot: slot) + (Facing(slot: slot) * FocusDistance));

    /// <summary>The selection-pivot resolver the orbit camera consults: a present, non-null answer retargets the orbit
    /// at the selection; otherwise the seat's avatar anchors it. Property-injected (the targeting state is composed
    /// after this session).</summary>
    public Func<int, Vector3?>? OrbitPivotSource { get; set; }

    /// <summary>Enters editor mode for a seat: captures its intent source, diverts it to Idle on BOTH halves (the
    /// client mask and the server body over <c>SetControl</c>), flips the seat's active binding group to the editor
    /// group, and arms the camera to seed from the seat's current chase framing on the next produced frame (no pose
    /// pop).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public EditorModeOutcome Enter(int slot) {
        if (!m_roster.IsJoined(slot: slot)) {
            return EditorModeOutcome.NotJoined;
        }

        if (m_roster.IsPending(slot: slot)) {
            return EditorModeOutcome.Pending;
        }

        var seat = m_seats[slot];

        if (seat.Active) {
            return EditorModeOutcome.AlreadyThere;
        }

        var controller = m_roster.Seat(slot: slot);

        seat.PriorSource = (controller?.Source ?? IntentSource.Live);
        // The diversion is the existing player.control contract, applied on both halves in one act so the mask lands
        // with no tick gap: the avatar idles honestly (a live tape or player.press still drives it — script outranks
        // idle), and the transition drops held keys/lanes so nothing leaks into or out of the mode.
        m_link.SubmitCommand(command: new WorldCommand.SetControl(Principal: WorldPrincipal.Console, EntityIndex: slot, Source: IntentSource.Idle));
        controller?.SetIntentSource(source: IntentSource.Idle);
        _ = m_bindings.SetActiveGroup(slot: slot, group: WorldEditorBindings.GroupId);
        seat.Active = true;
        seat.SeedPending = true;
        seat.Mode = EditorCameraMode.Fly;
        seat.StagedMove = Vector2.Zero;
        seat.StagedLook = Vector2.Zero;
        seat.ActiveMove = Vector2.Zero;
        seat.ActiveLook = Vector2.Zero;
        seat.AscendHeld = false;
        seat.DescendHeld = false;

        return EditorModeOutcome.Applied;
    }

    /// <summary>Exits editor mode for a seat: flips the active binding group back to the default and restores the
    /// intent source captured at enter on both halves. The chase rig re-anchors to the avatar deterministically, so
    /// the camera restores with no pose pop by construction.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public EditorModeOutcome Exit(int slot) {
        if ((uint)slot >= (uint)m_seats.Length) {
            return EditorModeOutcome.NotJoined;
        }

        var seat = m_seats[slot];

        if (!seat.Active) {
            return EditorModeOutcome.AlreadyThere;
        }

        Deactivate(slot: slot, seat: seat);
        m_link.SubmitCommand(command: new WorldCommand.SetControl(Principal: WorldPrincipal.Console, EntityIndex: slot, Source: seat.PriorSource));
        m_roster.Seat(slot: slot)?.SetIntentSource(source: seat.PriorSource);

        return EditorModeOutcome.Applied;
    }

    /// <summary>Sets the seat's camera mode. Entering orbit re-derives the orbit angles from the current fly pose so
    /// the switch holds the vantage; entering fly adopts the resolved orbit eye the same way.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="mode">The camera mode to select.</param>
    public void SetMode(int slot, EditorCameraMode mode) {
        var seat = m_seats[SlotOrFirst(slot: slot)];

        if (seat.Mode == mode) {
            return;
        }

        if (mode == EditorCameraMode.Fly) {
            // Adopt the orbit's resolved eye and aim back at the pivot, so the switch is seamless.
            seat.Eye = seat.Rig.Eye;
            SetLookToward(seat: seat, target: seat.Rig.Target);
        } else {
            // Orbit angles that reproduce the current vantage relative to the avatar are derived on the next frame's
            // resolve from the fly pose; seed with the fly look inverted (orbiting FROM where the camera is).
            seat.OrbitYaw = (seat.Yaw + MathF.PI);
            seat.OrbitPitch = Math.Clamp(value: -seat.Pitch, min: -MaxPitchRadians, max: MaxPitchRadians);
        }

        seat.Mode = mode;
    }

    /// <summary>Sets the seat's fly speed (clamped to the sane envelope).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="unitsPerSecond">The speed, world units per second.</param>
    /// <returns>The clamped applied speed.</returns>
    public float SetSpeed(int slot, float unitsPerSecond) {
        var seat = m_seats[SlotOrFirst(slot: slot)];

        seat.Speed = Math.Clamp(value: unitsPerSecond, min: MinFlySpeed, max: MaxFlySpeed);

        return seat.Speed;
    }

    /// <summary>Steps the seat's fly speed up or down by the chord step factor.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="up">Whether to step faster (<see langword="true"/>) or slower.</param>
    /// <returns>The stepped speed.</returns>
    public float StepSpeed(int slot, bool up) {
        var seat = m_seats[SlotOrFirst(slot: slot)];

        return SetSpeed(slot: slot, unitsPerSecond: (up ? (seat.Speed * SpeedStepFactor) : (seat.Speed / SpeedStepFactor)));
    }

    /// <summary>Stages this tick's movement-stick sample (the <c>editor.stick.move</c> router; +Y flies forward, +X
    /// strafes right). Promoted to the frame-visible latch by <see cref="LatchTick"/>.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="move">The deadzoned movement sample.</param>
    public void RouteMove(int slot, Vector2 move) {
        if ((uint)slot < (uint)m_seats.Length) {
            m_seats[slot].StagedMove = move;
        }
    }

    /// <summary>Stages this tick's look-stick sample (the <c>editor.stick.look</c> router; +X looks right, +Y looks up).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="look">The deadzoned look sample.</param>
    public void RouteLook(int slot, Vector2 look) {
        if ((uint)slot < (uint)m_seats.Length) {
            m_seats[slot].StagedLook = look;
        }
    }

    /// <summary>Holds or releases a vertical channel (the shoulder HoldRelease pairs).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="ascend">Whether the channel is rise (<see langword="true"/>) or sink.</param>
    /// <param name="held">Whether the channel is held from this edge.</param>
    public void SetVertical(int slot, bool ascend, bool held) {
        if ((uint)slot >= (uint)m_seats.Length) {
            return;
        }

        if (ascend) {
            m_seats[slot].AscendHeld = held;
        } else {
            m_seats[slot].DescendHeld = held;
        }
    }

    /// <summary>Teleports the seat's editor camera to an explicit pose — the console twin of stick flight
    /// (<c>editor.cam.pose</c>). Forces fly mode and cancels any pending seed.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="eye">The eye position, world space.</param>
    /// <param name="yawRadians">The look heading (0 looks down +Z).</param>
    /// <param name="pitchRadians">The look tilt (positive looks up; clamped).</param>
    public void SetPose(int slot, Vector3 eye, float yawRadians, float pitchRadians) {
        var seat = m_seats[SlotOrFirst(slot: slot)];

        seat.Mode = EditorCameraMode.Fly;
        seat.SeedPending = false;
        seat.Eye = eye;
        seat.Yaw = yawRadians;
        seat.Pitch = Math.Clamp(value: pitchRadians, min: -MaxPitchRadians, max: MaxPitchRadians);
    }

    /// <summary>Promotes each editing seat's staged stick samples to the frame-visible latch and clears the staging —
    /// called once per completed simulation tick (the analog re-dispatch cadence), so a centered stick reads zero
    /// after its final routed sample while a held deflection stays stable for every frame until the next tick.</summary>
    public void LatchTick() {
        foreach (var seat in m_seats) {
            if (!seat.Active) {
                continue;
            }

            seat.ActiveMove = seat.StagedMove;
            seat.ActiveLook = seat.StagedLook;
            seat.StagedMove = Vector2.Zero;
            seat.StagedLook = Vector2.Zero;
        }
    }

    /// <summary>Self-heals a departed seat: a slot that left the roster while editing is force-exited (group flipped
    /// back, camera dropped) so a later join never inherits editor bindings. Called once per produced frame.</summary>
    public void PruneDeparted() {
        for (var slot = 0; (slot < m_seats.Length); slot++) {
            if (m_seats[slot].Active && !m_roster.IsJoined(slot: slot)) {
                // The body is already gone with the seat; only the client-side mode state needs unwinding.
                Deactivate(slot: slot, seat: m_seats[slot]);
            }
        }
    }

    /// <summary>Resolves the rig that frames a seat this frame: the chase rig when the seat is not editing, else the
    /// editor rig advanced by this frame's presentation delta (seeding from the chase framing on the first frame
    /// after enter, so there is no pose pop).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="chase">The seat's chase rig (the non-editing default).</param>
    /// <param name="anchor">The seat avatar's render pose this frame (the orbit pivot and the seed subject).</param>
    /// <param name="time">The presentation clock, seconds.</param>
    /// <param name="deltaSeconds">The clamped presentation interval to integrate camera motion by.</param>
    /// <returns>The rig to resolve the seat's camera with this frame.</returns>
    public ISdfCameraRig ResolveRig(int slot, ISdfCameraRig chase, in SdfAnchor anchor, float time, float deltaSeconds) {
        if (((uint)slot >= (uint)m_seats.Length) || !m_seats[slot].Active) {
            return chase;
        }

        var seat = m_seats[slot];

        if (seat.SeedPending) {
            var (chaseEye, chaseTarget, _) = chase.Resolve(anchor: in anchor, time: time);

            seat.Eye = chaseEye;
            SetLookToward(seat: seat, target: chaseTarget);
            seat.SeedPending = false;
        }

        if (m_drag.IsDragging(slot: slot)) {
            // A live drag steals the sticks: the camera holds its last resolved pose (look included — a moving frame
            // under a precision drag fights the hand), the latched move sample translates the pending row in the
            // camera's yaw frame, and the shoulder verticals lift/sink it.
            AdvanceDrag(slot: slot, seat: seat, deltaSeconds: deltaSeconds);
        } else if (seat.Mode == EditorCameraMode.Fly) {
            AdvanceFly(seat: seat, deltaSeconds: deltaSeconds);
        } else {
            // The orbit pivots at the selection when one resolves; the seat avatar (chest-lifted) anchors it otherwise.
            var pivot = (OrbitPivotSource?.Invoke(arg: slot) ?? (anchor.Position + s_orbitPivotLift));

            AdvanceOrbit(seat: seat, pivot: pivot, deltaSeconds: deltaSeconds);
        }

        return seat.Rig;
    }

    // The drag-steered frame: the same planar camera frame the fly path derives (right = cross(look, up)), fed to the
    // pending row at the seat's fly speed. Quadratic stick response matches flight so a drag feels like the camera.
    private void AdvanceDrag(int slot, Seat seat, float deltaSeconds) {
        var forward = LookDirection(yaw: seat.Yaw, pitch: 0f);
        var right = Vector3.Normalize(value: Vector3.Cross(vector1: forward, vector2: Vector3.UnitY));
        var move = seat.ActiveMove;
        var vertical = ((seat.AscendHeld ? 1f : 0f) - (seat.DescendHeld ? 1f : 0f));

        m_drag.Advance(
            slot: slot,
            planarRight: right,
            planarForward: forward,
            move: new Vector2(x: Response(value: move.X), y: Response(value: move.Y)),
            vertical: vertical,
            speed: seat.Speed,
            deltaSeconds: deltaSeconds
        );
    }

    // Free-fly integration: quadratic stick response for fine control near center, look before move so the frame's
    // translation follows the freshest heading, vertical from the held shoulder channels.
    private static void AdvanceFly(Seat seat, float deltaSeconds) {
        var look = seat.ActiveLook;

        // Stick +X looks right = yaw decreases in the 0-looks-down-+Z convention; stick +Y looks up.
        seat.Yaw -= (Response(value: look.X) * LookRateRadiansPerSecond * deltaSeconds);
        seat.Pitch = Math.Clamp(
            value: (seat.Pitch + (Response(value: look.Y) * LookRateRadiansPerSecond * deltaSeconds)),
            min: -MaxPitchRadians,
            max: MaxPitchRadians
        );

        var forward = LookDirection(yaw: seat.Yaw, pitch: seat.Pitch);
        // The camera's screen-right axis for a look-at with +Y up (cross(look, up), normalized; pitch is clamped
        // well short of the poles, so the planar magnitude never degenerates).
        var right = Vector3.Normalize(value: Vector3.Cross(vector1: forward, vector2: Vector3.UnitY));
        var move = seat.ActiveMove;
        var vertical = ((seat.AscendHeld ? 1f : 0f) - (seat.DescendHeld ? 1f : 0f));
        var velocity = ((forward * Response(value: move.Y)) + (right * Response(value: move.X)) + (Vector3.UnitY * vertical));

        seat.Eye += (velocity * (seat.Speed * deltaSeconds));
        seat.Rig.Eye = seat.Eye;
        seat.Rig.Target = (seat.Eye + forward);
    }

    // Orbit integration: left stick orbits the pivot, right stick's Y zooms exponentially, the pivot tracks its
    // source live (the selection when one resolves; the avatar otherwise).
    private static void AdvanceOrbit(Seat seat, Vector3 pivot, float deltaSeconds) {
        var move = seat.ActiveMove;

        seat.OrbitYaw -= (Response(value: move.X) * LookRateRadiansPerSecond * deltaSeconds);
        seat.OrbitPitch = Math.Clamp(
            value: (seat.OrbitPitch + (Response(value: move.Y) * LookRateRadiansPerSecond * deltaSeconds)),
            min: -MaxPitchRadians,
            max: MaxPitchRadians
        );
        seat.OrbitDistance = Math.Clamp(
            value: (seat.OrbitDistance * MathF.Exp(x: (-Response(value: seat.ActiveLook.Y) * OrbitZoomRatePerSecond * deltaSeconds))),
            min: MinOrbitDistance,
            max: MaxOrbitDistance
        );

        seat.Rig.Eye = (pivot + OrbitRig.Offset(yaw: seat.OrbitYaw, pitch: seat.OrbitPitch, distance: seat.OrbitDistance));
        seat.Rig.Target = pivot;
        // Keep the fly pose trailing the orbit so a mode toggle adopts the vantage (see SetMode).
        seat.Eye = seat.Rig.Eye;
        SetLookToward(seat: seat, target: pivot);
    }

    // Quadratic stick response: sign-preserving v*|v| — fine control near center, full authority at the rim.
    private static float Response(float value) => (value * MathF.Abs(x: value));

    // The look direction at yaw/pitch (yaw 0 = +Z, the OrbitRig convention).
    private static Vector3 LookDirection(float yaw, float pitch) {
        var cosPitch = MathF.Cos(x: pitch);

        return new Vector3(
            x: (MathF.Sin(x: yaw) * cosPitch),
            y: MathF.Sin(x: pitch),
            z: (MathF.Cos(x: yaw) * cosPitch)
        );
    }

    // Derive the seat's fly look angles from an eye→target direction (the seed and mode-switch shared math).
    private static void SetLookToward(Seat seat, Vector3 target) {
        var direction = (target - seat.Eye);

        if (direction.LengthSquared() < 1e-8f) {
            return;
        }

        direction = Vector3.Normalize(value: direction);
        seat.Yaw = MathF.Atan2(y: direction.X, x: direction.Z);
        seat.Pitch = Math.Clamp(value: MathF.Asin(x: Math.Clamp(value: direction.Y, min: -1f, max: 1f)), min: -MaxPitchRadians, max: MaxPitchRadians);
    }

    /// <summary>The view-order index of the single editing seat when EXACTLY one seat edits while at least one other
    /// plays — the layout policy's trigger — or <c>-1</c> (standard split ladder) otherwise.</summary>
    public int SoleEditorViewIndex() {
        var viewIndex = 0;
        var editors = 0;
        var editorViewIndex = -1;

        for (var slot = 0; (slot < m_seats.Length); slot++) {
            if (!m_roster.IsJoined(slot: slot)) {
                continue;
            }

            if (m_seats[slot].Active) {
                editors++;
                editorViewIndex = viewIndex;
            }

            viewIndex++;
        }

        return (((editors == 1) && (viewIndex >= 2)) ? editorViewIndex : -1);
    }

    // Unwind a seat's mode state without touching the (possibly departed) body: flip the binding group back, drop
    // the camera, forget the sticks.
    private void Deactivate(int slot, Seat seat) {
        _ = m_bindings.SetActiveGroup(slot: slot, group: null);
        seat.Active = false;
        seat.SeedPending = false;
        seat.StagedMove = Vector2.Zero;
        seat.StagedLook = Vector2.Zero;
        seat.ActiveMove = Vector2.Zero;
        seat.ActiveLook = Vector2.Zero;
        seat.AscendHeld = false;
        seat.DescendHeld = false;
    }

    private int SlotOrFirst(int slot) => (((uint)slot < (uint)m_seats.Length) ? slot : 0);
}
