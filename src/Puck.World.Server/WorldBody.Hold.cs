using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // How closely a tracking probe's normal must still agree with the held normal for it to be the same face:
    // cos 60 degrees. A representation tolerance rather than a feel knob — it separates the face the body is on,
    // turning, from a different face entirely, and one step of travel turns a real face by far less than this.
    private static readonly FixedQ4816 HoldTrackAlignment = FixedQ4816.FromDouble(value: 0.5);

    private FixedBodyHold[] m_holds = [];
    private int m_holdIndex = -1;
    private FixedVector3 m_holdAnchor;
    private FixedVector3 m_holdNormal;
    private FixedRateAccumulator m_holdSpendAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    // The axis the body is DRAWN standing on, carried across ticks so a grip's lean is reached by turning over the
    // body's own span rather than by snapping — see SteerAttitudeToward. Zero until the first resolve seats it.
    private FixedVector3 m_attitudeUp;
    private FixedRateAccumulator m_attitudeTurnAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    // The world's compiled cos(maxSlopeDegrees) — the same threshold contact resolution grounds on, so a hold and
    // the ground it ends on cannot disagree about which faces are walkable.
    private FixedQ4816 m_walkableThreshold = FixedQ4816.One;

    /// <summary>Gets the name of the hold this body currently holds, or <see langword="null"/> when nothing holds
    /// it.</summary>
    public string? HoldName => (((m_holdIndex >= 0) && (m_holdIndex < m_holds.Length))
        ? m_holds[m_holdIndex].Name
        : null
    );
    /// <summary>Gets the surface normal of the current hold, or <see cref="FixedVector3.Zero"/> when nothing holds
    /// the body or its hold bonds to no surface.</summary>
    public FixedVector3 HoldNormal => m_holdNormal;
    /// <summary>Gets the surface point the current hold is anchored to, or <see cref="FixedVector3.Zero"/> when
    /// nothing holds the body or its hold bonds to no surface.</summary>
    public FixedVector3 HoldAnchor => m_holdAnchor;
    /// <summary>Gets the value left in the state slot the current hold spends, or <see langword="null"/> when
    /// nothing holds the body or its hold spends nothing.</summary>
    public FixedQ4816? HoldSpendRemaining {
        get {
            if (!TryCurrentHold(hold: out var hold)) {
                return null;
            }

            var slot = HoldSpendSlot(hold: in hold);

            return ((slot >= 0)
                ? m_actionStateValues[slot]
                : null
            );
        }
    }

    /// <summary>Sets the world's compiled walkable-slope threshold — the alignment with the body's up axis a surface
    /// normal must clear to read as ground.</summary>
    /// <param name="threshold">The compiled <c>cos(maxSlopeDegrees)</c>.</param>
    internal void SetWalkableThreshold(FixedQ4816 threshold) {
        m_walkableThreshold = threshold;
    }

    // Where a hold's probes search from: the collider's mid-height above the body origin, so a body standing at a
    // wall's foot finds the wall rather than the floor under its feet, and a body at a ledge loses the face only once
    // its middle has cleared the lip.
    private FixedQ4816 HoldProbeHeight {
        get {
            if (m_collider is not { Volumes.Length: > 0 } collider) {
                return FixedQ4816.Zero;
            }

            var volume = collider.Volumes[0];

            return ((volume.Kind == FixedBodyColliderKind.Capsule)
                ? ((volume.Center.Y + volume.Endpoint.Y) / FixedQ4816.FromInteger(value: 2L))
                : volume.Center.Y
            );
        }
    }
    private FixedQ4816 HoldStandoff => ((m_collider is { Volumes.Length: > 0 } collider)
        ? collider.Volumes[0].Radius
        : FixedQ4816.Zero
    );
    private FixedVector3 HoldProbeOrigin => (m_position + (UnitY * HoldProbeHeight));
    // The axis every hold cone is measured against — the direction the world's own gravity opposes, never the body's
    // own up. A row's cone says what a face is (floor, wall, ceiling), which is a fact about the world; measuring it
    // against a body already leaned onto a ceiling would read that ceiling's own floor as a ceiling.
    private FixedVector3 HoldGravityUp {
        get {
            if (
                TrySolvedGravity(acceleration: out var acceleration) &&
                (acceleration.Length >= MinFieldUpMagnitude)
            ) {
                var down = acceleration.Normalize();

                if (down != FixedVector3.Zero) {
                    return -down;
                }
            }

            return UnitY;
        }
    }

    private bool TryCurrentHold(out FixedBodyHold hold) {
        if (
            (m_holdIndex < 0) ||
            (m_holdIndex >= m_holds.Length)
        ) {
            hold = default;

            return false;
        }

        hold = m_holds[m_holdIndex];

        return true;
    }
    // Casts a hold probe, starting one standoff behind the origin along its own direction. A body pressed against a
    // face by its own drive sits flush with it, and a march that starts on (or a hair inside) a surface has no
    // distance to march: backing the origin off guarantees the ray begins in open space, and the extra length is
    // added back so the reach the world authored is the reach actually searched.
    private bool TryCastHold(in FixedVector3 direction, FixedQ4816 reach, out FixedSurfaceAttachCandidate candidate) {
        candidate = default;

        if (m_contactField is not { } field) {
            return false;
        }

        var backoff = HoldStandoff;

        return field.TryHoldableSurfaceAlongDirection(
            candidate: out candidate,
            direction: in direction,
            grantedByOverride: out _,
            maxDistance: (reach + backoff),
            origin: (HoldProbeOrigin - (direction.Normalize() * backoff))
        );
    }
    private static bool ChannelHeld(in PlayerIntent intent, int ordinal, FixedQ4816 threshold) => ((ordinal >= 0) && (intent[ordinal] >= threshold));
    private int HoldSpendSlot(in FixedBodyHold hold) => ((hold.SpendState is { Length: > 0 } name)
        ? FindActionState(name: name)
        : -1
    );
    // Whether a row may be taken (or kept) at all this tick, before any geometry is probed: its release channel is
    // not held down, and whatever it spends still has something to spend.
    private bool HoldEligible(in FixedBodyHold hold, in PlayerIntent intent) {
        if (ChannelHeld(
            intent: in intent,
            ordinal: hold.ReleaseOrdinal,
            threshold: hold.ReleaseThreshold
        )) {
            return false;
        }

        var slot = HoldSpendSlot(hold: in hold);

        return ((slot < 0) || (m_actionStateValues[slot] > FixedQ4816.Zero));
    }
    // Drains one step of what the current hold spends. The remainder carries in the hold's own accumulator, so a
    // rate that does not divide the step evenly still drains exactly the authored amount over time.
    private void SpendHold(in FixedBodyHold hold, ulong stepTicks) {
        var slot = HoldSpendSlot(hold: in hold);

        if (
            (slot < 0) ||
            (hold.SpendPerSecond <= FixedQ4816.Zero)
        ) {
            return;
        }

        var drained = m_holdSpendAccumulator.Integrate(
            elapsedTicks: stepTicks,
            ratePerSecond: hold.SpendPerSecond
        );

        if (drained <= FixedQ4816.Zero) {
            return;
        }

        ApplyRawState(
            reason: hold.Name,
            requested: (m_actionStateValues[slot] - drained).Value,
            slot: slot,
            writer: "hold"
        );
        MarkDurableDirty(slot: slot);
    }
    // Re-reads the held face along its own inward normal and reports whether the same face, inside the same row's
    // cone, is still there. Distance alone cannot answer that: at a ledge, at a wall's foot, and at an outside corner
    // there is always some geometry within reach, facing a different way.
    private bool TryTrackHold(in FixedBodyHold hold, in FixedVector3 up, out FixedSurfaceAttachCandidate candidate) {
        candidate = default;

        if (m_holdNormal == FixedVector3.Zero) {
            return false;
        }
        if (!TryCastHold(
            candidate: out var nearest,
            direction: -m_holdNormal,
            reach: hold.Reach
        )) {
            return false;
        }

        var normal = nearest.Normal.Normalize();

        if (FixedVector3.Dot(
            left: normal,
            right: m_holdNormal
        ) < HoldTrackAlignment) {
            return false;
        }
        if (!hold.ConeAdmits(alignment: FixedVector3.Dot(
            left: normal,
            right: up
        ))) {
            return false;
        }

        candidate = (nearest with { Normal = normal });

        return true;
    }
    // Takes a surface row by an active drive: probe along the commanded direction, and require both that the face
    // sits inside the row's cone and that the drive is pushing into it rather than sliding along it.
    private bool TryTakeHoldOnDrive(in FixedBodyHold hold, in FixedVector3 drive, in FixedVector3 up, out FixedSurfaceAttachCandidate candidate) {
        candidate = default;

        if (hold.Bond != BodyHoldBond.Surface) {
            return false;
        }
        if (!TryCastHold(
            candidate: out var driven,
            direction: in drive,
            reach: hold.Reach
        )) {
            return false;
        }

        var normal = driven.Normal.Normalize();

        if (!hold.ConeAdmits(alignment: FixedVector3.Dot(
            left: normal,
            right: up
        )) || (FixedVector3.Dot(
            left: drive,
            right: -normal
        ) < hold.DriveAlignment)) {
            return false;
        }

        candidate = (driven with { Normal = normal });

        return true;
    }
    // Takes a surface row from where the body already is. Both probes are directed and both are derived from the
    // authored cone rather than from any assumption about what is holding on: a cone reaching at or below a right
    // angle can admit a face under the body, and one reaching at or above it can admit a face over the body. A
    // nearest-anything query on a world whose floor, walls and overhangs are one placement answers with the floor
    // under the body's feet.
    private bool TryTakeHold(in FixedBodyHold hold, in FixedVector3 up, out FixedSurfaceAttachCandidate candidate) {
        candidate = default;

        if (hold.Bond != BodyHoldBond.Surface) {
            return false;
        }
        if (
            hold.ConeAdmitsBelow &&
            TryTakeHoldAlong(
            candidate: out candidate,
            direction: -up,
            hold: in hold,
            up: in up
        )
        ) {
            return true;
        }

        return (hold.ConeAdmitsAbove && TryTakeHoldAlong(
            candidate: out candidate,
            direction: in up,
            hold: in hold,
            up: in up
        ));
    }
    private bool TryTakeHoldAlong(in FixedBodyHold hold, in FixedVector3 direction, in FixedVector3 up, out FixedSurfaceAttachCandidate candidate) {
        candidate = default;

        if (!TryCastHold(
            candidate: out var hit,
            direction: in direction,
            reach: hold.Reach
        )) {
            return false;
        }

        var normal = hit.Normal.Normalize();

        if (!hold.ConeAdmits(alignment: FixedVector3.Dot(
            left: normal,
            right: up
        ))) {
            return false;
        }

        candidate = (hit with { Normal = normal });

        return true;
    }
    // The body span every reach-past probe is measured in: the probe sits a mid-height above the feet and the body
    // stands one standoff out from whatever it holds, so this is how far a body has to travel to arrive on a face
    // whose edge it has just cleared.
    private FixedQ4816 HoldSpan => (HoldProbeHeight + HoldStandoff);

    // Reaches past a face whose edge the body has just cleared: up the last tangent by one body span and in past
    // that face by one body width, then the ordinary probes from there. A body climbing a wall loses the wall when
    // its middle passes the lip, and what it is reaching for is over the lip, not below its feet — this is the one
    // probe that can see it. On a hit the body ARRIVES on the face, one body span off it along its own normal,
    // which is what standing on a ledge is.
    private bool TryReachPastLostSurface(in FixedBodyHold hold, in FixedVector3 lostNormal, in FixedVector3 lostTangent, in FixedVector3 up, out FixedSurfaceAttachCandidate candidate) {
        candidate = default;

        if (
            (m_contactField is not { } field) ||
            (lostNormal == FixedVector3.Zero) ||
            (lostTangent == FixedVector3.Zero)
        ) {
            return false;
        }

        var span = HoldSpan;
        var standoff = HoldStandoff;
        var origin = ((HoldProbeOrigin + (lostTangent * span)) - (lostNormal * (standoff + standoff)));
        var reach = (hold.Reach + standoff);

        if (
            (hold.ConeAdmitsBelow && field.TryHoldableSurfaceAlongDirection(
            candidate: out var below,
            direction: -up,
            grantedByOverride: out _,
            maxDistance: reach,
            origin: in origin
        ) && hold.ConeAdmits(alignment: FixedVector3.Dot(
            left: below.Normal.Normalize(),
            right: up
        )))
        ) {
            candidate = (below with { Normal = below.Normal.Normalize() });

            return true;
        }
        if (
            (hold.ConeAdmitsAbove && field.TryHoldableSurfaceAlongDirection(
            candidate: out var above,
            direction: in up,
            grantedByOverride: out _,
            maxDistance: reach,
            origin: in origin
        ) && hold.ConeAdmits(alignment: FixedVector3.Dot(
            left: above.Normal.Normalize(),
            right: up
        )))
        ) {
            candidate = (above with { Normal = above.Normal.Normalize() });

            return true;
        }

        return false;
    }
    // Whether a held surface row has been outlived by the body simply standing: the contact resolve has stood the
    // body on something walkable and the body has stopped pulling itself along the face. A body still driving up a
    // face keeps holding it however close the ground is, which is what lets a hold be taken at a wall's foot.
    private bool HoldOutlivedByStanding(in FixedBodyHold hold, in PlayerIntent intent) => (m_grounded && (PlanarIntent(intent: in intent).Forward <= FixedQ4816.Zero) && (hold.Bond == BodyHoldBond.Surface) && (m_holdNormal != FixedVector3.Zero) && (FixedVector3.Dot(
        left: m_holdNormal,
        right: UnitY
    ) < m_walkableThreshold));

    // The ResolveHold operation. What the body is actively driving into outranks what it happens to be resting on,
    // so the drive pass runs first. Otherwise the ordered list decides, first match wins, with the row already held
    // evaluated by whether its own face is still there rather than by a fresh take — so the list order is a
    // preference and a row authored EARLIER still wins from where the body stands.
    private void ResolveHold(ref BodyMotionScratch scratch) {
        if (m_holds.Length == 0) {
            return;
        }

        var up = HoldGravityUp;
        var previousIndex = m_holdIndex;
        var previousNormal = m_holdNormal;
        var previousTangent = HoldTangentForward(
            normal: in previousNormal,
            up: in up
        );
        var chosen = -1;
        var normal = FixedVector3.Zero;
        var anchor = FixedVector3.Zero;
        var arrive = false;
        // Whether the face the body was holding has ENDED, as opposed to the row having been released, spent out, or
        // outlived by the body standing. Only an ended face is worth reaching past.
        var lostFace = false;

        if (TryCommandedDirection(
            direction: out var drive,
            intent: in scratch.Intent
        )) {
            for (var index = 0; (index < m_holds.Length); index++) {
                var candidateHold = m_holds[index];

                if (
                    !candidateHold.OnDrive ||
                    !HoldEligible(
                    hold: in candidateHold,
                    intent: in scratch.Intent
                )
                ) {
                    continue;
                }
                if (TryTakeHoldOnDrive(
                    candidate: out var driven,
                    drive: in drive,
                    hold: in candidateHold,
                    up: in up
                )) {
                    anchor = driven.Point;
                    chosen = index;
                    normal = driven.Normal;

                    break;
                }
            }
        }
        if (chosen < 0) {
            for (var index = 0; (index < m_holds.Length); index++) {
                var hold = m_holds[index];

                if (!HoldEligible(
                    hold: in hold,
                    intent: in scratch.Intent
                )) {
                    continue;
                }
                if (hold.Bond == BodyHoldBond.Free) {
                    chosen = index;

                    break;
                }
                if (hold.Bond == BodyHoldBond.Medium) {
                    // The world either offers a medium where the body is or it does not — the lattice column is the
                    // whole test, and there is no face to probe for or to keep tracking.
                    if (m_mediumSurface is not null) {
                        chosen = index;

                        break;
                    }

                    continue;
                }
                if (index != previousIndex) {
                    if (TryTakeHold(
                        candidate: out var taken,
                        hold: in hold,
                        up: in up
                    )) {
                        anchor = taken.Point;
                        chosen = index;
                        normal = taken.Normal;

                        break;
                    }

                    continue;
                }
                if (HoldOutlivedByStanding(
                    hold: in hold,
                    intent: in scratch.Intent
                )) {
                    // Nothing to reach past: the body has arrived on ground it can stand on.
                    continue;
                }
                if (TryTrackHold(
                    candidate: out var tracked,
                    hold: in hold,
                    up: in up
                )) {
                    anchor = tracked.Point;
                    chosen = index;
                    normal = tracked.Normal;

                    break;
                }

                lostFace = (previousNormal != FixedVector3.Zero);
            }
        }
        if (
            lostFace &&
            (PlanarIntent(intent: in scratch.Intent).Forward > FixedQ4816.Zero)
        ) {
            // The body was travelling along a face whose edge it has just cleared, so what it is arriving at is past
            // that edge, not under its feet. This outranks the ordinary pass, whose probes cannot see past an edge
            // at all.
            for (var index = 0; (index < m_holds.Length); index++) {
                var hold = m_holds[index];

                if (
                    (hold.Bond != BodyHoldBond.Surface) ||
                    !HoldEligible(
                    hold: in hold,
                    intent: in scratch.Intent
                )
                ) {
                    continue;
                }
                if (TryReachPastLostSurface(
                    candidate: out var reached,
                    hold: in hold,
                    lostNormal: in previousNormal,
                    lostTangent: in previousTangent,
                    up: in up
                )) {
                    anchor = reached.Point;
                    arrive = true;
                    chosen = index;
                    normal = reached.Normal;

                    break;
                }
            }
        }

        if (chosen != m_holdIndex) {
            m_holdSpendAccumulator.Reset();
            CarryGripMomentum(
                next: chosen,
                up: in up
            );
        }

        m_holdAnchor = anchor;
        m_holdIndex = chosen;
        m_holdNormal = normal;

        if (chosen < 0) {
            SetFreeAttitude(scratch: ref scratch);

            return;
        }

        var held = m_holds[chosen];

        if (arrive) {
            m_position = ((anchor + (normal * HoldSpan)) - (UnitY * HoldProbeHeight));
            m_positionAccumulator.Reset();
            m_planarVelocity = FixedVector3.Zero;
            m_verticalVelocity = FixedQ4816.Zero;
            m_verticalVelocityAccumulator.Reset();
            scratch.NextPosition = m_position;
        }
        if (held.Speed > FixedQ4816.Zero) {
            // The row's own travel speed along its tangent plane, replacing the kit's resolved move speed for as
            // long as it holds; a row authoring none rides the kit's.
            scratch.MoveSpeed = held.Speed;
        }

        SpendHold(
            hold: in held,
            stepTicks: scratch.StepTicks
        );
        SetHoldFrame(
            gravityUp: in up,
            hold: in held,
            scratch: ref scratch
        );
    }
    // The direction "along the face, away from down" — gravity-up projected onto the face's tangent plane, zero for
    // a face whose normal is parallel to gravity-up (a floor or a flat ceiling says nothing about forward).
    private static FixedVector3 HoldTangentForward(in FixedVector3 normal, in FixedVector3 up) {
        if (normal == FixedVector3.Zero) {
            return FixedVector3.Zero;
        }

        var tangent = (up - (normal * FixedVector3.Dot(
            left: up,
            right: normal
        )));

        return (tangent.TryLength(length: out var length) && (length > FixedQ4816.Zero)
            ? (tangent / length)
            : FixedVector3.Zero
        );
    }
    // The hold's frame. Movement rides the face's own tangent plane, oriented by gravity: forward is gravity-up
    // projected onto the face ("up the face"), right completes it. The body's own up axis blends from gravity-up
    // toward the face normal by the row's lean, so a lean of zero keeps a body upright on a wall and reads that wall
    // as unwalkable, while a lean of one lays the body on it and reads it as ground.
    //
    // A face whose normal is parallel to gravity-up leaves the tangent undefined; there the surface says nothing
    // about forward, so the ordinary frame stands and only the lean's own transport applies.
    private void SetHoldFrame(in FixedBodyHold hold, in FixedVector3 gravityUp, ref BodyMotionScratch scratch) {
        if (hold.Bond != BodyHoldBond.Surface) {
            SetFreeAttitude(scratch: ref scratch);

            return;
        }

        var normal = m_holdNormal;
        var leaned = (gravityUp + ((normal - gravityUp) * hold.UpLean)).Normalize();

        if (leaned == FixedVector3.Zero) {
            return;
        }

        // Whether the lean also carries the body's CONTACT axis is decided by what is holding the body, never by the
        // lean itself. A hold gravity keeps presses the body onto its face, so that face is the ground the solver
        // should stand it on and the axis leans with it — a kart on a loop. A grip holds the body instead, gravity is
        // suspended, and leaning the contact axis onto the face would tell the solver that the floor under the body
        // is a ceiling and that falling is upward: the floor stops depenetrating and a released body flies off. So a
        // grip's lean is the body's FRAME — the plane it travels in and the attitude it is drawn at — and the contact
        // axis stays with the ambient resolve.
        //
        // The adoption is BOUNDED, through the same accumulator a measured contact normal is adopted by and for the
        // same reason: a face change is a discontinuity in the axis, and rotating the carried velocity through it
        // whole throws the body off. It is also why nothing here ever asks FromTo for an antipodal arc.
        var rotation = FixedQuaternion.Identity;

        if (hold.Kind == BodyHoldKind.Gravity) {
            rotation = SteerUpToward(
                accumulator: ref m_contactUpTurnAccumulator,
                halfRate: ContactUpTurnHalfRate,
                stepTicks: scratch.StepTicks,
                target: leaned
            );
        }

        scratch.Up = m_up;

        // The drawn body stands on the leaned axis whether or not the solver does, and every later attitude writer
        // (the facing snap) composes about this rather than flattening it back onto the contact axis. A grip reaches
        // its lean by turning over the body's own span, so a face change is a turn and never a pop; a gravity hold's
        // drawn axis stays with its lean, whose contact axis is already bounded above.
        FixedVector3 attitude;

        if (hold.Kind == BodyHoldKind.Grip) {
            attitude = SteerAttitudeToward(
                speed: scratch.MoveSpeed,
                stepTicks: scratch.StepTicks,
                target: in leaned
            );
        } else {
            attitude = leaned;
            m_attitudeUp = leaned;
        }

        scratch.AttitudeUp = attitude;

        var tangent = (gravityUp - (normal * FixedVector3.Dot(
            left: gravityUp,
            right: normal
        )));

        if (!tangent.TryLength(length: out var length)) {
            return;
        }

        if (length > FixedQ4816.Zero) {
            var forward = (tangent / length);

            scratch.Facing = forward;
            scratch.Right = FixedVector3.Cross(
                left: forward,
                right: normal
            );
        } else if (rotation != FixedQuaternion.Identity) {
            // A face whose normal is parallel to gravity-up says nothing about forward, so the ordinary frame stands
            // — carried by whatever arc the axis actually turned through.
            scratch.Facing = rotation.Rotate(vector: scratch.Facing);
            scratch.Right = rotation.Rotate(vector: scratch.Right);
        }

        SetHoldAttitude(
            hold: in hold,
            leaned: in attitude,
            scratch: ref scratch
        );
    }
    // Turns the carried drawn axis toward a target by at most one step's share of the rate a body turns over its own
    // span — speed over span, radians per second: the pivot of a body that long rolling over an edge at that speed —
    // so the rate is the hold's own travel and the collider's own size, never a knob. A body with no span, or no
    // seated axis yet, takes the target whole; so does an antipodal target, which names no arc to turn through.
    private FixedVector3 SteerAttitudeToward(in FixedVector3 target, FixedQ4816 speed, ulong stepTicks) {
        var span = HoldSpan;

        if (
            (m_attitudeUp == FixedVector3.Zero) ||
            (m_attitudeUp == target) ||
            (span <= FixedQ4816.Zero)
        ) {
            m_attitudeUp = target;

            return target;
        }

        // Accumulated as the HALF angle the rotor is built from, the same way the contact axis budgets its turn.
        var budget = m_attitudeTurnAccumulator.Integrate(
            elapsedTicks: stepTicks,
            ratePerSecond: (speed / (span * FixedQ4816.FromInteger(value: 2L)))
        );

        if (budget <= FixedQ4816.Zero) {
            return m_attitudeUp;
        }

        var rotation = FixedQuaternion.FromTo(
            from: m_attitudeUp,
            to: target
        );
        var (halfSin, halfCos) = FixedQ4816.SinCos(angle: budget);

        if (rotation.W >= halfCos) {
            m_attitudeUp = target;

            return target;
        }

        var axis = new FixedVector3(
            X: rotation.X,
            Y: rotation.Y,
            Z: rotation.Z
        ).Normalize();

        if (axis == FixedVector3.Zero) {
            m_attitudeUp = target;

            return target;
        }

        var step = new FixedQuaternion(
            W: halfCos,
            X: (axis.X * halfSin),
            Y: (axis.Y * halfSin),
            Z: (axis.Z * halfSin)
        );

        m_attitudeUp = step.Rotate(vector: m_attitudeUp).Normalize();

        return m_attitudeUp;
    }
    // With no face to lean on, the drawn axis returns to the contact axis at the same bounded rate it left it, and
    // the attitude is recomposed about wherever it has got to so the return is drawn rather than popped — UNLESS the
    // program integrates its own local attitude (a body-frame 6DOF flight program), which already owns
    // scratch.Orientation in full: composing a yaw-only snap over it here would discard the pitch/roll that
    // integration just built, for an axis the program never asked this to draw a facing against.
    private void SetFreeAttitude(ref BodyMotionScratch scratch) {
        var attitude = SteerAttitudeToward(
            speed: scratch.MoveSpeed,
            stepTicks: scratch.StepTicks,
            target: in scratch.Up
        );

        if (attitude == scratch.AttitudeUp) {
            return;
        }

        scratch.AttitudeUp = attitude;

        if (m_bodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)) {
            return;
        }

        SnapFacing(
            scratch: ref scratch,
            yaw: m_yaw
        );
    }
    // A grip owns the whole tangent-plane velocity, its rise included, so the tick it ends that rise would be
    // replaced by the next planar shape and lost. It is momentum the body earned: split it against gravity-up, the
    // planar part for the shaper and the rest for the ballistic channel gravity now acts on, so a body letting go
    // mid-climb keeps climbing for the moment its momentum buys rather than dropping from rest.
    private void CarryGripMomentum(int next, in FixedVector3 up) {
        if (
            !TryCurrentHold(hold: out var previous) ||
            (previous.Kind != BodyHoldKind.Grip) ||
            ((next >= 0) && (m_holds[next].Kind == BodyHoldKind.Grip))
        ) {
            return;
        }

        var rise = FixedVector3.Dot(
            left: m_planarVelocity,
            right: up
        );

        if (rise == FixedQ4816.Zero) {
            return;
        }

        m_planarVelocity -= (up * rise);
        m_verticalVelocity += rise;
        m_verticalVelocityAccumulator.Reset();
    }
    // The body's drawn attitude under a hold. While the lean and the contact axis agree — every unleaned hold, and
    // every hold gravity keeps — this is the heading carried into the body's own up frame, exactly as the ordinary
    // frame operation builds it, so an unleaned world is unchanged. Where they disagree (a grip's lean) the attitude
    // is built from the leaned axis directly, which is what puts a gripping body's up on the face it holds while the
    // solver keeps measuring against gravity.
    private void SetHoldAttitude(in FixedBodyHold hold, in FixedVector3 leaned, ref BodyMotionScratch scratch) {
        var source = (hold.Forward switch {
            BodyHoldForward.Intent => (TryCommandedDirection(
                direction: out var commanded,
                intent: in scratch.Intent
            )
                ? commanded
                : FixedVector3.Zero),
            BodyHoldForward.Velocity => m_planarVelocity,
            _ => FixedVector3.Zero,
        });

        if (leaned != m_up) {
            var forward = ((source != FixedVector3.Zero)
                ? source
                : scratch.Facing
            );

            if (!TryPerpendicular(
                axis: in leaned,
                unit: out var aligned,
                vector: in forward
            ) && !TryPerpendicular(
                axis: in leaned,
                unit: out aligned,
                vector: scratch.Orientation.Rotate(vector: -UnitZ)
            ) && !TryPerpendicular(
                axis: in leaned,
                unit: out aligned,
                vector: in scratch.Right
            )) {
                return;
            }

            scratch.Orientation = AttitudeFrom(
                forward: in aligned,
                up: in leaned
            );

            return;
        }
        if (source != FixedVector3.Zero) {
            var local = m_frame.Inverse().Rotate(vector: source);
            var planar = new FixedVector3(
                X: local.X,
                Y: FixedQ4816.Zero,
                Z: local.Z
            );

            if (planar.LengthSquared > FixedQ4816.Zero) {
                m_yaw = FixedQ4816.Atan2(
                    x: -planar.Z,
                    y: -planar.X
                );
            }
        }

        var attitude = FixedQuaternion.FromAxisAngle(
            angle: m_yaw,
            axis: UnitY
        );

        scratch.Orientation = ((m_up == UnitY)
            ? attitude
            : (m_frame * attitude)
        );
    }
    // The component of a vector perpendicular to an axis, unit length, or false when the two are parallel.
    private static bool TryPerpendicular(in FixedVector3 vector, in FixedVector3 axis, out FixedVector3 unit) {
        unit = FixedVector3.Zero;

        var perpendicular = (vector - (axis * FixedVector3.Dot(
            left: vector,
            right: axis
        )));

        if (
            !perpendicular.TryLength(length: out var length) ||
            (length <= FixedQ4816.Zero)
        ) {
            return false;
        }

        unit = (perpendicular / length);

        return true;
    }
    // The attitude carrying world +Y onto an up axis and world -Z onto a forward already perpendicular to it. The
    // shortest arc from +Y is undefined at the antipode, but the twist that follows aligns forward exactly, so the
    // composed rotation is the same whichever axis that arc picked.
    private static FixedQuaternion AttitudeFrom(in FixedVector3 forward, in FixedVector3 up) {
        var arc = FixedQuaternion.FromTo(
            from: UnitY,
            to: up
        );
        var arcForward = arc.Rotate(vector: -UnitZ);
        var twist = FixedQuaternion.FromAxisAngle(
            angle: FixedQ4816.Atan2(
                x: FixedVector3.Dot(
                    left: arcForward,
                    right: forward
                ),
                y: FixedVector3.Dot(
                    left: FixedVector3.Cross(
                        left: arcForward,
                        right: forward
                    ),
                    right: up
                )
            ),
            axis: up
        );

        return (twist * arc).Normalize();
    }
    // The ApplyHold operation: the current row's vertical law, plus its own MoveUp thrust in every bond. Pairing
    // this with ResolveHold, and authoring a hold list with one row nothing can drop, are both required for a
    // Motion-kind program (see ValidateMotionRow/ValidateHolds), so TryCurrentHold always succeeds against a
    // validated document; the guard exists because this method itself has no document to check that against.
    private void ApplyHold(ref BodyMotionScratch scratch) {
        if (!TryCurrentHold(hold: out var hold)) {
            return;
        }

        switch (hold.Kind) {
            case BodyHoldKind.Gravity:
                ApplyHoldGravity(
                    hold: in hold,
                    scale: FixedQ4816.One,
                    stepTicks: scratch.StepTicks
                );

                break;
            case BodyHoldKind.Lift:
                if (hold.Lift >= FixedQ4816.One) {
                    // Full lift owns the vertical channel: whatever contact folded into it (a glance off a walkable
                    // face) bleeds back to rest instead of carrying the body away forever.
                    ApplyHoldGravityDecay(
                        hold: in hold,
                        scratch: ref scratch
                    );
                } else {
                    ApplyHoldGravity(
                        hold: in hold,
                        scale: (FixedQ4816.One - hold.Lift),
                        stepTicks: scratch.StepTicks
                    );
                }

                break;
            case BodyHoldKind.Grip:
                // Gravity is not applied while a grip holds, and the ballistic channel it would have filled is held
                // clear so nothing accumulates across the hold. The pull itself is positional — see SeatToHold.
                if (m_verticalVelocity != FixedQ4816.Zero) {
                    m_verticalVelocity = FixedQ4816.Zero;
                    m_verticalVelocityAccumulator.Reset();
                }

                break;
            default:
                break;
        }
        if (hold.Bond == BodyHoldBond.Medium) {
            // A medium holds by displacement whatever its kind says about gravity, so its law runs alongside rather
            // than instead of the kind switch above (a medium row ordinarily authors kind None), and folds the
            // row's own thrust into its drift before the convergence runs rather than through ApplyHoldThrust below.
            ApplyMedium(
                hold: in hold,
                scratch: ref scratch
            );
        } else {
            ApplyHoldThrust(
                hold: in hold,
                scratch: ref scratch
            );
        }
    }
    // Integrates ONE gravity channel whatever sources it — the world's solved field when it authors one, the row's
    // own arc otherwise. The rise/fall asymmetry is SHAPING, not a source — under a solved field it rides as the
    // row's authored Rise:Fall ratio so a floaty top of the arc survives on a planetoid, and the row's own Terminal
    // clamps either way. scale is the fraction of the arc still reaching the body — a lift row's own fraction
    // cancels the rest; one is the unscaled arc.
    private void ApplyHoldGravity(in FixedBodyHold hold, FixedQ4816 scale, ulong stepTicks) {
        var rising = (m_verticalVelocity > FixedQ4816.Zero);
        var gravity = (rising
            ? hold.Gravity.Rise
            : hold.Gravity.Fall
        );

        if (TrySolvedGravityMagnitude(magnitude: out var solved)) {
            gravity = solved;

            if (
                rising &&
                (hold.Gravity.Fall > FixedQ4816.Zero)
            ) {
                gravity = ((gravity * hold.Gravity.Rise) / hold.Gravity.Fall);
            }
        }

        var gravityStep = m_verticalVelocityAccumulator.Integrate(
            elapsedTicks: stepTicks,
            ratePerSecond: -(gravity * scale)
        );
        var terminalVelocity = -hold.Gravity.Terminal;
        var acceleratedVelocity = (m_verticalVelocity + gravityStep);

        if (acceleratedVelocity < terminalVelocity) {
            m_verticalVelocity = terminalVelocity;
            m_verticalVelocityAccumulator.Reset();
        } else {
            m_verticalVelocity = acceleratedVelocity;
        }
    }
    // A full-lift row owns the vertical channel outright, bleeding whatever it carries back toward rest at the
    // row's own Rise rate (a symmetric bleed, not the asymmetric arc) rather than integrating gravity onto it.
    private void ApplyHoldGravityDecay(in FixedBodyHold hold, ref BodyMotionScratch scratch) {
        if (m_verticalVelocity == FixedQ4816.Zero) {
            return;
        }

        scratch.Velocity = scratch.Velocity with { Y = (scratch.Velocity.Y + m_verticalVelocity) };

        if (m_verticalVelocity > FixedQ4816.Zero) {
            var bleed = m_verticalVelocityAccumulator.Integrate(
                ratePerSecond: -hold.Gravity.Rise,
                elapsedTicks: scratch.StepTicks
            );
            var next = (m_verticalVelocity + bleed);

            m_verticalVelocity = ((next < FixedQ4816.Zero)
                ? FixedQ4816.Zero
                : next
            );
        } else {
            var bleed = m_verticalVelocityAccumulator.Integrate(
                ratePerSecond: hold.Gravity.Rise,
                elapsedTicks: scratch.StepTicks
            );
            var next = (m_verticalVelocity + bleed);

            m_verticalVelocity = ((next > FixedQ4816.Zero)
                ? FixedQ4816.Zero
                : next
            );
        }

        if (m_verticalVelocity == FixedQ4816.Zero) {
            m_verticalVelocityAccumulator.Reset();
        }
    }
    // A row's own thrust, applied to every non-Medium bond: while MoveUp is non-zero, drives vertical velocity
    // directly at the row's thrust fraction of the resolved move speed and suspends the ballistic channel; releasing
    // MoveUp returns vertical ownership to the row's own kind this same tick. A Medium bond's own thrust folds into
    // ApplyMedium's convergence instead, so this never runs for one.
    private void ApplyHoldThrust(in FixedBodyHold hold, ref BodyMotionScratch scratch) {
        if (hold.Thrust <= FixedQ4816.Zero) {
            return;
        }

        var drive = Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveUp
        );

        if (drive == FixedQ4816.Zero) {
            return;
        }

        m_verticalVelocity = FixedQ4816.Zero;
        m_verticalVelocityAccumulator.Reset();
        scratch.DirectVerticalVelocity = ((drive * scratch.MoveSpeed) * hold.Thrust);
    }
    // The one medium law. The body's own commanded thrust is folded into the medium's drift BEFORE the convergence
    // runs, so nothing writes the vertical channel twice: below the bob band the medium drifts the body at its
    // buoyancy, inside the band (and above it, recovering a breach) it settles proportionally toward the float line,
    // and the sum is clamped to the medium's own terminal speeds.
    private void ApplyMedium(in FixedBodyHold hold, ref BodyMotionScratch scratch) {
        var medium = hold.Medium;

        if (m_mediumSurface is not { } mediumSurface) {
            return;
        }

        var surfaceRest = (mediumSurface - medium.FloatDepth);
        var error = (surfaceRest - m_position.Y);
        FixedQ4816 drift;

        if (m_position.Y < (surfaceRest - medium.FloatDepth)) {
            drift = FixedQ4816.Clamp(
                value: medium.Buoyancy,
                minimum: -medium.MaxSinkSpeed,
                maximum: medium.MaxRiseSpeed
            );
        } else {
            var upwardCap = ((medium.Buoyancy > FixedQ4816.Zero)
                ? medium.Buoyancy
                : FixedQ4816.Zero
            );

            drift = FixedQ4816.Clamp(
                value: (error * medium.SurfaceSettleRate),
                minimum: -medium.MaxSinkSpeed,
                maximum: upwardCap
            );
        }

        var target = FixedQ4816.Clamp(
            value: (MediumThrust(
                hold: in hold,
                scratch: ref scratch
            ) + drift),
            minimum: -medium.MaxSinkSpeed,
            maximum: medium.MaxRiseSpeed
        );

        if (m_tuning.PlanarDynamics is { Planar: { } planar }) {
            // scratch.StepTicks can differ from planar.StepTicks for exactly one tick — a world-load/reload swap
            // recompiles the propagator at the NEW rate but the batch already in flight still advances at its OLD
            // width. The follower steps through the mismatched width rather than fault on it.
            StepVerticalFollower(
                step: in planar,
                target: target,
                minimum: -medium.MaxSinkSpeed,
                maximum: medium.MaxRiseSpeed
            );
            WriteMediumFacts(
                error: error,
                medium: in medium,
                surface: mediumSurface
            );

            return;
        }

        var response = m_tuning.Response;

        if (response.Length == 0) {
            m_verticalVelocity = target;
        } else {
            var matched = false;

            // ShapePlanarVelocity already ticked the recency clocks this step (phase 2 precedes 4); this scan only
            // SELECTS (first open row wins, same rule the planar half follows).
            foreach (var row in response) {
                if (!MotionGateOpen(gate: row.Gate)) {
                    continue;
                }

                var hasVerticalInput = (Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.MoveUp
                ) != FixedQ4816.Zero);
                var rate = (hasVerticalInput
                    ? row.EngageRate
                    : row.ReleaseRate
                );
                var maxDelta = m_mediumThrustRampAccumulator.Integrate(
                    elapsedTicks: scratch.StepTicks,
                    ratePerSecond: rate
                );

                m_verticalVelocity = FixedQ4816.MoveToward(
                    current: m_verticalVelocity,
                    maxDelta: maxDelta,
                    target: target
                );
                matched = true;

                break;
            }

            if (!matched) {
                m_verticalVelocity = target;
            }
        }

        WriteMediumFacts(
            error: error,
            medium: in medium,
            surface: mediumSurface
        );
    }
    // The vertical half of the commanded thrust: the MoveUp role scaled by the hold's travel speed and the row's own
    // thrust, with the same held-sprint multiplier the planar half applies.
    private FixedQ4816 MediumThrust(in FixedBodyHold hold, ref BodyMotionScratch scratch) {
        var speed = (((m_sprintChannelOrdinal >= 0) && (scratch.Intent[m_sprintChannelOrdinal] >= m_channelThresholds[m_sprintChannelOrdinal]))
            ? (scratch.MoveSpeed * m_tuning.SprintMultiplier)
            : scratch.MoveSpeed
        );

        return ((Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveUp
        ) * speed) * hold.Thrust);
    }
    // The two medium facts, on the same one-tick-behind terms every other body fact is published under.
    private void WriteMediumFacts(FixedQ4816 surface, FixedQ4816 error, in FixedBodyMedium medium) {
        m_submerged = (m_position.Y < surface);
        m_atSurface = (m_submerged && (((error < FixedQ4816.Zero)
            ? -error
            : error) <= medium.FloatDepth));
    }
    /// <summary>Gets a value indicating whether the current hold owns the body's vertical channel outright, so
    /// contact resolution must not fold its resolved velocity back into it.</summary>
    private bool HoldOwnsVerticalChannel => (TryCurrentHold(hold: out var hold) && (hold.Kind == BodyHoldKind.Grip));

    // The grip's pull along the face normal, as a positional constraint rather than a force. A force cannot be
    // trusted here: the field depenetrates along its own nearest gradient, which for a body at a wall's foot is the
    // floor, so an inward force meets no resistance from the wall and walks the body through it.
    //
    // The two directions are deliberately asymmetric. Closing a gap is rate-limited by the authored pull, so a body
    // that drifts off a leaning face is drawn back at a speed the world chose; coming out of a penetration is never
    // rate-limited, because a rate-limited correction is exactly the budget a tunnel needs.
    private void SeatToHold(ulong stepTicks) {
        if (
            !TryCurrentHold(hold: out var hold) ||
            (hold.Kind != BodyHoldKind.Grip) ||
            (m_holdNormal == FixedVector3.Zero)
        ) {
            return;
        }

        var gap = FixedVector3.Dot(
            left: (HoldProbeOrigin - m_holdAnchor),
            right: m_holdNormal
        );
        var error = (gap - HoldStandoff);

        if (error <= FixedQ4816.Zero) {
            m_position -= (m_holdNormal * error);

            return;
        }

        var reach = PerStep(
            stepTicks: stepTicks,
            value: hold.Grip
        );

        m_position -= (m_holdNormal * ((error < reach)
            ? error
            : reach
        ));
    }
    // Whether the body is holding a surface the contact resolve would refuse to stand it on — the published Climbing
    // fact, stated over the world's own walkable threshold rather than any creature's idea of a wall.
    private bool HoldsUnwalkableSurface() {
        if (
            !TryCurrentHold(hold: out var hold) ||
            (hold.Bond != BodyHoldBond.Surface) ||
            (m_holdNormal == FixedVector3.Zero)
        ) {
            return false;
        }

        return (FixedVector3.Dot(
            left: m_holdNormal,
            right: UnitY
        ) < m_walkableThreshold);
    }
    // Whether the body is holding itself up with no surface at all — the published Flying fact.
    private bool HoldsFree() => (TryCurrentHold(hold: out var hold) && (hold.Bond == BodyHoldBond.Free) && (hold.Lift > FixedQ4816.Zero));
}
