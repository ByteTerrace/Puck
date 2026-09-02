using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The attachment primitive's mode — <see cref="None"/> is ordinary locomotion, unaffected by anything in
/// this file. <see cref="Grapple"/> is the distance-cap tether mode (gravity stays on; the body swings under
/// <see cref="Puck.Physics.FixedTetherConstraint"/>, solved every tick by
/// <see cref="WorldPopulation.ResolveTethers"/>).</summary>
public enum WorldBodyAttachmentMode : byte {
    /// <summary>No attachment — the body's ordinary program dispatch runs unmodified.</summary>
    None,

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

    /// <summary>Gets this body's current attachment mode.</summary>
    public WorldBodyAttachmentMode AttachmentMode => m_attachmentMode;
    /// <summary>Gets the active tether's anchor — its world point or body-local offset
    /// (<see cref="TetherAnchorPointOrLocalOffset"/>, meaningful as a world point only when
    /// <see cref="TetherAnchorBodyIndex"/> is <see langword="null"/>), or <see langword="null"/> under
    /// <see cref="WorldBodyAttachmentMode.None"/>.</summary>
    public FixedVector3? AttachmentAnchor => ((m_attachmentMode == WorldBodyAttachmentMode.Grapple)
        ? TetherAnchorPointOrLocalOffset
        : null
    );
    /// <summary>Gets the active grapple's current rope length, or <see langword="null"/> outside
    /// <see cref="WorldBodyAttachmentMode.Grapple"/>.</summary>
    public FixedQ4816? AttachmentRopeLength => ((m_attachmentMode == WorldBodyAttachmentMode.Grapple)
        ? TetherLength
        : null
    );

    /// <summary>Sets the compiled grapple policy this body's attach/detach/reel channels and rope tuning read —
    /// called on activation and on every live recompile, the same terms as <see cref="SetContactField"/>.</summary>
    internal void SetAttachmentPolicy(FixedWorldAttachment policy) {
        m_attachmentPolicy = policy;
    }

    // Reads the attach/detach channels DIRECTLY (never through the kit action table — see FixedWorldAttachment's own
    // remarks on why it carries its own thresholds), fires at most one transition per tick: a detach edge always
    // wins over an attach edge held the SAME tick, and a fresh attach only ever starts from None (a body already
    // attached ignores a second attach press — detach first).
    private void ProcessAttachmentIntent(in PlayerIntent intent) {
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
    // The held reel channel — meaningful only while grappling (an unattached body has no tether to reel). Reads a
    // raw bipolar value every tick and hands it straight to the slice-1 rope kernel through ReelTether, exactly the
    // caller-supplied-rate contract that kernel already documents.
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
    // The body's own facing tries a grapple anchor within the aim-assist cone (directed —
    // FixedSurfaceQuery.TryNearestDirected). No candidate is a silent no-op, the same "nothing to act on" shape
    // every other action-track effect in this codebase takes.
    private void TryAttach() {
        if (
            (m_contactField is not { } field) ||
            (m_attachmentPolicy.GrappleMaxDistance <= FixedQ4816.Zero)
        ) {
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
    // Enters GRAPPLE: the tether's initial length is the resolved anchor's ACTUAL distance (always <=
    // GrappleMaxDistance, since that bounded the query), never the authored ceiling itself — a closer anchor grants
    // a shorter rope, exactly the reach a body's current position earned. The reel-in floor clamps down to that
    // same distance when authored larger than what this attach actually found (FixedTetherConstraint's constructor
    // requires length >= minLength).
    private void EnterGrapple(in Puck.Physics.FixedSurfaceAttachCandidate candidate) {
        m_attachmentMode = WorldBodyAttachmentMode.Grapple;

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
    // Clears the tether and restores ordinary locomotion, carrying the release-momentum-scale field into the
    // velocity the mode leaves behind: gravity never stopped integrating the ordinary channels, so the scale applies
    // to them directly.
    private void Detach() {
        var scale = m_attachmentPolicy.ReleaseMomentumScale;

        if (m_attachmentMode == WorldBodyAttachmentMode.Grapple) {
            m_planarVelocity = (m_planarVelocity * scale);
            m_verticalVelocity = (m_verticalVelocity * scale);
            m_verticalVelocityAccumulator.Reset();
            ClearTether();
        }

        m_attachmentMode = WorldBodyAttachmentMode.None;
        WriteAttachmentModeState();
    }
    // The commanded WORLD move direction, unit length, or false when nothing is commanded. Reads the world-frame
    // MoveX/Y/Z triple when the world declares it (the seat has already resolved its camera into those axes), and
    // otherwise resolves the heading-framed advance/strafe pair against the body's own attitude — the same two
    // sources ComputePlanarTargetVelocity chooses between, so a hold taken on drive agrees with the walk that
    // produced it.
    private bool TryCommandedDirection(in PlayerIntent intent, out FixedVector3 direction) {
        direction = FixedVector3.Zero;

        var commanded = FixedVector3.Zero;

        if (m_roleOrdinals.HasMoveDirection) {
            commanded = new FixedVector3(
                X: Role(
                    intent: in intent,
                    role: ChannelRole.MoveX
                ),
                Y: Role(
                    intent: in intent,
                    role: ChannelRole.MoveY
                ),
                Z: Role(
                    intent: in intent,
                    role: ChannelRole.MoveZ
                )
            );
        }
        if (commanded.LengthSquared <= FixedQ4816.Zero) {
            var (forward, strafe) = PlanarIntent(intent: in intent);

            commanded = ((m_orientation.Rotate(vector: -UnitZ) * forward) + (m_orientation.Rotate(vector: UnitX) * strafe));
        }

        var length = commanded.Length;

        if (length <= FixedQ4816.Zero) {
            return false;
        }

        direction = (commanded / length);

        return true;
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
            requested: FixedQ4816.FromInteger(value: ((long)m_attachmentMode)).Value,
            slot: slot,
            writer: "attachment"
        );
        MarkDurableDirty(slot: slot);
    }
}
