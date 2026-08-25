using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The attachment primitive's mode — <see cref="None"/> is ordinary locomotion, unaffected by anything in
/// this file. <see cref="Climb"/> is the body-conforming SURFACE mode (grip replaces gravity, movement rides the
/// gripped surface's tangent plane). <see cref="Grapple"/> is the distance-CAP TETHER mode (gravity stays on; the
/// body swings under <see cref="Puck.Physics.FixedTetherConstraint"/>, solved every tick by
/// <see cref="WorldPopulation.ResolveTethers"/>).</summary>
public enum WorldBodyAttachmentMode : byte {
    /// <summary>No attachment — the body's ordinary program dispatch runs unmodified.</summary>
    None,

    /// <summary>Gripping a surface: grounding/gravity suspended, movement rides the tangent plane.</summary>
    Climb,

    /// <summary>Tethered to a grapple anchor: gravity on, the tether constraint clamps the swing.</summary>
    Grapple,
}

public sealed partial class WorldBody {
    // The body-owned action-state slot name a world may declare (state.body) to make this body's attachment mode
    // camera-select-readable (views.seatRig's select op keys off state.<row>) — see WriteAttachmentModeState. A
    // world declaring no such slot pays nothing beyond one FindActionState miss per transition.
    private const string AttachmentModeStateName = "attachment.mode";

    private FixedWorldAttachment m_attachmentPolicy = FixedWorldAttachment.Absent;
    private WorldBodyAttachmentMode m_attachmentMode = WorldBodyAttachmentMode.None;
    private bool m_attachPreviousBit;
    private bool m_detachPreviousBit;
    // CLIMB-only state: the gripped surface point/normal at attach, and the tangent-plane basis derived from the
    // normal ONCE at attach (see EnterClimb) — climbing moves within this fixed plane rather than re-projecting onto
    // curved geometry every tick, which is exact for the flat/box/half-space surfaces the analytic provider models
    // and a deliberate simplification for a curved (sphere) grip.
    private FixedVector3 m_climbAnchor;
    private FixedVector3 m_climbNormal;
    private FixedVector3 m_climbTangentRight;
    private FixedVector3 m_climbTangentUp;
    // The last tick's tangent-plane world-space velocity — read back for body.attachment and carried into
    // m_planarVelocity/m_verticalVelocity on Detach (the release-momentum-scale field applies to this).
    private FixedVector3 m_climbVelocity;
    private FixedVector3RateAccumulator m_climbAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    // Which policy layer granted CLIMB — true when the gripped collider carried a WorldPlacementGrip override,
    // false when the world's own DefaultGrip decided it. Meaningless outside Climb mode.
    private bool m_climbGrantedByOverride;

    /// <summary>Gets this body's current attachment mode.</summary>
    public WorldBodyAttachmentMode AttachmentMode => m_attachmentMode;
    /// <summary>Gets the active attachment's anchor point — the gripped surface point under <see cref="WorldBodyAttachmentMode.Climb"/>,
    /// or the tether's world-point/local-offset anchor under <see cref="WorldBodyAttachmentMode.Grapple"/>
    /// (<see cref="TetherAnchorPointOrLocalOffset"/>, meaningful as a WORLD point only when
    /// <see cref="TetherAnchorBodyIndex"/> is <see langword="null"/>). <see langword="null"/> under
    /// <see cref="WorldBodyAttachmentMode.None"/>.</summary>
    public FixedVector3? AttachmentAnchor => (m_attachmentMode switch {
        WorldBodyAttachmentMode.Climb => m_climbAnchor,
        WorldBodyAttachmentMode.Grapple => TetherAnchorPointOrLocalOffset,
        _ => null,
    });
    /// <summary>Gets the active grapple's current rope length, or <see langword="null"/> outside
    /// <see cref="WorldBodyAttachmentMode.Grapple"/> (climb has no rope).</summary>
    public FixedQ4816? AttachmentRopeLength => ((m_attachmentMode == WorldBodyAttachmentMode.Grapple)
        ? TetherLength
        : null
    );
    /// <summary>Gets the world's authored per-second grip cost (<see cref="FixedWorldAttachment.GripCost"/>) —
    /// echoed regardless of mode; it spends no resource channel of its own today.</summary>
    public FixedQ4816 AttachmentGripCost => m_attachmentPolicy.GripCost;
    /// <summary>Gets which policy layer granted the active CLIMB — <see langword="true"/> for a per-placement
    /// <see cref="WorldPlacementGrip"/> override, <see langword="false"/> for the world's own
    /// <see cref="WorldAttachmentSection.DefaultGrip"/>. Meaningless outside <see cref="WorldBodyAttachmentMode.Climb"/>.</summary>
    public bool AttachmentGrantedByOverride => m_climbGrantedByOverride;

    /// <summary>Sets the compiled climb/grapple policy this body's attach/detach/reel channels and grip/rope tuning
    /// read — called on activation and on every live recompile, the same terms as <see cref="SetContactField"/>.</summary>
    internal void SetAttachmentPolicy(FixedWorldAttachment policy) {
        m_attachmentPolicy = policy;
    }
    // Reads the attach/detach channels DIRECTLY (never through the kit action table — see FixedWorldAttachment's own
    // remarks on why it carries its own thresholds), fires at most one transition per tick: a detach edge always
    // wins over an attach edge held the SAME tick, and a fresh attach only ever starts from None (a body already
    // attached ignores a second attach press — detach first).
    private void ProcessAttachmentIntent(in PlayerIntent intent, ulong stepTicks) {
        _ = stepTicks;

        if (!m_attachmentPolicy.Enabled) {
            return;
        }

        var attachOrdinal = m_attachmentPolicy.AttachChannelOrdinal;
        var detachOrdinal = m_attachmentPolicy.DetachChannelOrdinal;
        var attachHeld = ((attachOrdinal >= 0) && (intent[attachOrdinal] >= m_attachmentPolicy.AttachThreshold));
        var detachHeld = ((detachOrdinal >= 0) && (intent[detachOrdinal] >= m_attachmentPolicy.DetachThreshold));
        var attachEdge = (attachHeld && !m_attachPreviousBit);
        var detachEdge = (detachHeld && !m_detachPreviousBit);

        m_attachPreviousBit = attachHeld;
        m_detachPreviousBit = detachHeld;

        if (
            detachEdge &&
            (m_attachmentMode != WorldBodyAttachmentMode.None)
        ) {
            Detach();

            return;
        }

        if (
            attachEdge &&
            (m_attachmentMode == WorldBodyAttachmentMode.None)
        ) {
            TryAttach();
        }
    }
    // The held reel channel — meaningful only while grappling (climbing has no rope; unattached has no tether to
    // reel). Reads a raw bipolar value every tick and hands it straight to the slice-1 rope kernel through
    // ReelTether, exactly the caller-supplied-rate contract that kernel already documents.
    private void ProcessReel(in PlayerIntent intent, ulong stepTicks) {
        if (
            (m_attachmentMode != WorldBodyAttachmentMode.Grapple) ||
            (m_attachmentPolicy.ReelChannelOrdinal < 0)
        ) {
            return;
        }

        var direction = intent[m_attachmentPolicy.ReelChannelOrdinal];

        ReelTether(
            elapsedTicks: stepTicks,
            ratePerSecond: (m_attachmentPolicy.ReelRate * direction)
        );
    }
    // The context-sensitive attach: climb wins whenever a climbable surface sits within reach (undirected —
    // FixedSurfaceQuery.TryNearest), otherwise the body's own facing tries a grapple anchor within the aim-assist
    // cone (directed — FixedSurfaceQuery.TryNearestDirected). Neither candidate existing is a silent no-op, the
    // same "nothing to act on" shape every other action-track effect in this codebase takes.
    private void TryAttach() {
        if (m_contactField is not { } field) {
            return;
        }

        if (
            (m_attachmentPolicy.ClimbReach > FixedQ4816.Zero) &&
            field.TryNearestClimbableSurface(
            candidate: out var climbCandidate,
            grantedByOverride: out var grantedByOverride,
            probe: in m_position,
            reach: m_attachmentPolicy.ClimbReach
        )
        ) {
            EnterClimb(
                candidate: in climbCandidate,
                grantedByOverride: grantedByOverride
            );

            return;
        }

        if (m_attachmentPolicy.GrappleMaxDistance <= FixedQ4816.Zero) {
            return;
        }

        var facing = m_orientation.Rotate(vector: -UnitZ);

        if (field.TryNearestSurfaceAlongDirection(
            assistHalfAngle: m_attachmentPolicy.GrappleAssistHalfAngle,
            candidate: out var grappleCandidate,
            direction: in facing,
            maxDistance: m_attachmentPolicy.GrappleMaxDistance,
            origin: in m_position
        )) {
            EnterGrapple(candidate: in grappleCandidate);
        }
    }
    // Enters CLIMB: derives the tangent-plane basis once from the gripped normal (project world +Y onto the plane
    // for "up the wall"; a ceiling/floor grip — normal parallel to +Y — falls back to world +Z projected instead,
    // since +Y's own projection there is exactly zero), snaps the body onto the surface point, and suspends the
    // ordinary vertical/planar channels for as long as the grip holds (see AdvanceClimb — nothing else ever writes
    // them back while Climb is active).
    private void EnterClimb(in FixedSurfaceAttachCandidate candidate, bool grantedByOverride) {
        m_attachmentMode = WorldBodyAttachmentMode.Climb;
        m_climbAnchor = candidate.Point;
        m_climbNormal = candidate.Normal;
        m_climbGrantedByOverride = grantedByOverride;

        var verticalTangent = (UnitY - (m_climbNormal * FixedVector3.Dot(
            left: UnitY,
            right: m_climbNormal
        )));

        if (verticalTangent.LengthSquared <= FixedQ4816.Zero) {
            verticalTangent = (UnitZ - (m_climbNormal * FixedVector3.Dot(
                left: UnitZ,
                right: m_climbNormal
            )));
        }

        m_climbTangentUp = verticalTangent.Normalize();
        m_climbTangentRight = FixedVector3.Cross(
            left: m_climbTangentUp,
            right: m_climbNormal
        ).Normalize();
        m_climbVelocity = FixedVector3.Zero;
        m_climbAccumulator.Reset();
        m_position = m_climbAnchor;
        m_verticalVelocity = FixedQ4816.Zero;
        m_verticalVelocityAccumulator.Reset();
        m_planarVelocity = FixedVector3.Zero;

        WriteAttachmentModeState();
    }
    // Enters GRAPPLE: the tether's initial length is the resolved anchor's ACTUAL distance (always <=
    // GrappleMaxDistance, since that bounded the query), never the authored ceiling itself — a closer anchor grants
    // a shorter rope, exactly the reach a body's current position earned. The reel-in floor clamps down to that
    // same distance when authored larger than what this attach actually found (FixedTetherConstraint's constructor
    // requires length >= minLength).
    private void EnterGrapple(in FixedSurfaceAttachCandidate candidate) {
        m_attachmentMode = WorldBodyAttachmentMode.Grapple;
        m_climbGrantedByOverride = false;

        var minLength = ((m_attachmentPolicy.ReelInFloor <= candidate.Distance)
            ? m_attachmentPolicy.ReelInFloor
            : candidate.Distance
        );

        SetTetherToWorldPoint(
            anchor: candidate.Point,
            length: candidate.Distance,
            minLength: minLength
        );
        WriteAttachmentModeState();
    }
    // Clears whichever mode is active and restores ordinary locomotion, carrying the release-momentum-scale field
    // into whatever velocity the mode leaves behind: CLIMB has none of its own (m_planarVelocity/m_verticalVelocity
    // sat suspended the whole grip), so the last tangent-plane velocity is what gets scaled and handed back to the
    // ordinary channels; GRAPPLE already owns live velocity in those channels (gravity never stopped integrating
    // them), so the scale applies to them directly.
    private void Detach() {
        var scale = m_attachmentPolicy.ReleaseMomentumScale;

        if (m_attachmentMode == WorldBodyAttachmentMode.Climb) {
            var released = m_climbVelocity;

            m_planarVelocity = new FixedVector3(
                X: (released.X * scale),
                Y: FixedQ4816.Zero,
                Z: (released.Z * scale)
            );
            m_verticalVelocity = (released.Y * scale);
            m_verticalVelocityAccumulator.Reset();
        } else if (m_attachmentMode == WorldBodyAttachmentMode.Grapple) {
            m_planarVelocity = (m_planarVelocity * scale);
            m_verticalVelocity = (m_verticalVelocity * scale);
            m_verticalVelocityAccumulator.Reset();
            ClearTether();
        }

        m_attachmentMode = WorldBodyAttachmentMode.None;
        m_climbGrantedByOverride = false;
        m_climbVelocity = FixedVector3.Zero;
        WriteAttachmentModeState();
    }
    // The climb integrator: forward/strafe intent (the SAME unit-disc-clamped pair ordinary grounded movement reads
    // — see PlanarIntent) maps to the tangent-plane basis EnterClimb derived once (forward = up the wall, strafe =
    // sideways), integrated through its own rate accumulator so a non-tick-exact ClimbSpeed accumulates its
    // remainder like every other per-tick rate in this class. REPLACES ExecuteProgram entirely for this tick — no
    // gravity, no contact resolution, no action track: grip is the whole program while it holds.
    private void AdvanceClimb(in PlayerIntent intent, ulong stepTicks) {
        var (forward, strafe) = PlanarIntent(intent: in intent);

        m_climbVelocity = (((m_climbTangentUp * forward) + (m_climbTangentRight * strafe)) * m_attachmentPolicy.ClimbSpeed);
        m_position += m_climbAccumulator.Integrate(
            elapsedTicks: stepTicks,
            ratePerSecond: m_climbVelocity
        );
    }
    // Echoes the current mode into an OPTIONAL body-owned action-state slot a world may declare (state.body) named
    // AttachmentModeStateName — the camera program's `select` op keys off state.<row> exactly like it keys off any
    // other body-owned state cell, so a world wanting a per-mode camera arm authors that ONE slot and a select case
    // per WorldBodyAttachmentMode value; a world that declares nothing pays one FindActionState miss.
    private void WriteAttachmentModeState() {
        var slot = FindActionState(name: AttachmentModeStateName);

        if (slot < 0) {
            return;
        }

        ApplyRawState(
            reason: "attachment.mode",
            requested: FixedQ4816.FromInteger(value: (long)m_attachmentMode).Value,
            slot: slot,
            writer: "attachment"
        );
        MarkDurableDirty(slot: slot);
    }
}
