using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // The attached tether's anchor kind and geometry. m_tether.HasValue is the single source of truth for "does this
    // body have a tether"; the two anchor fields below are meaningless while it is null, and m_tetherAnchorBodyIndex
    // doubles as the discriminator (-1 selects the world-point form, matching the -1-means-none convention every other
    // resolved-ordinal field in this class already uses).
    private FixedTetherConstraint? m_tether;
    private bool m_attachPreviousBit;
    private bool m_detachPreviousBit;
    private int m_tetherAnchorBodyIndex = -1;
    private FixedVector3 m_tetherAnchorPointOrLocalOffset;

    /// <summary>Gets the attached tether's current rope length, or <see langword="null"/> when no tether is attached.</summary>
    public FixedQ4816? TetherLength => ((m_tether is { } tether)
        ? tether.Length
        : null
    );
    /// <summary>Gets the attached tether's minimum rope length (the reel-in floor), or <see langword="null"/> when no
    /// tether is attached.</summary>
    public FixedQ4816? TetherMinLength => ((m_tether is { } tether)
        ? tether.MinLength
        : null
    );
    /// <summary>Gets the population index of this tether's anchor body, or <see langword="null"/> when no tether is
    /// attached or the attached tether anchors to a fixed world point instead of a body.</summary>
    public int? TetherAnchorBodyIndex => ((m_tether.HasValue && (m_tetherAnchorBodyIndex >= 0))
        ? m_tetherAnchorBodyIndex
        : null
    );
    /// <summary>Gets the attached tether's anchor point: a fixed world point when <see cref="TetherAnchorBodyIndex"/>
    /// is <see langword="null"/>, otherwise the local-frame offset <see cref="FixedTetherConstraint.ResolveAnchor"/>
    /// rotates by the anchor body's current orientation each tick. Meaningless while no tether is attached.</summary>
    public FixedVector3 TetherAnchorPointOrLocalOffset => m_tetherAnchorPointOrLocalOffset;
    /// <summary>Gets a value indicating whether this body's kit carries a tether facet — the console's
    /// "no tether" refusal reads this before <c>body.attach</c>/<c>body.detach</c>/<c>body.reel</c> touch a
    /// channel.</summary>
    public bool HasTetherFacet => (m_tetherFacet is not null);
    /// <summary>Gets the kit's compiled attach-channel ordinal, or <c>-1</c> when the kit carries no tether facet
    /// or declares no attach channel.</summary>
    public int TetherAttachChannelOrdinal => (m_tetherFacet?.AttachChannelOrdinal ?? -1);
    /// <summary>Gets the kit's compiled detach-channel ordinal, or <c>-1</c> when the kit carries no tether facet
    /// or declares no detach channel.</summary>
    public int TetherDetachChannelOrdinal => (m_tetherFacet?.DetachChannelOrdinal ?? -1);
    /// <summary>Gets the kit's compiled reel-channel ordinal, or <c>-1</c> when the kit carries no tether facet or
    /// declares no reel channel.</summary>
    public int TetherReelChannelOrdinal => (m_tetherFacet?.ReelChannelOrdinal ?? -1);

    /// <summary>Attaches a tether anchored to a fixed world point, replacing any tether already attached.</summary>
    /// <param name="anchor">The fixed world point.</param>
    /// <param name="length">The initial rope length. Must be at least <paramref name="minLength"/>.</param>
    /// <param name="minLength">The floor a reel-in clamps to. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minLength"/> is negative, or
    /// <paramref name="length"/> is less than <paramref name="minLength"/>.</exception>
    public void SetTetherToWorldPoint(FixedVector3 anchor, FixedQ4816 length, FixedQ4816 minLength) {
        m_tether = new FixedTetherConstraint(
            length: length,
            minLength: minLength
        );
        m_tetherAnchorBodyIndex = -1;
        m_tetherAnchorPointOrLocalOffset = anchor;
    }
    /// <summary>Attaches a tether anchored to another body's local frame, replacing any tether already attached. The
    /// anchor body's pose is resolved fresh every tick (<see cref="Puck.World.Server.WorldPopulation.ResolveTethers"/>)
    /// — this call captures only the offset, never a snapshot of the anchor's current pose.</summary>
    /// <param name="bodyIndex">The anchor body's population index. Must be non-negative.</param>
    /// <param name="localOffset">The anchor point in the anchor body's local frame.</param>
    /// <param name="length">The initial rope length. Must be at least <paramref name="minLength"/>.</param>
    /// <param name="minLength">The floor a reel-in clamps to. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bodyIndex"/> is negative, <paramref name="minLength"/>
    /// is negative, or <paramref name="length"/> is less than <paramref name="minLength"/>.</exception>
    public void SetTetherToBody(int bodyIndex, FixedVector3 localOffset, FixedQ4816 length, FixedQ4816 minLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: bodyIndex,
            paramName: nameof(bodyIndex)
        );

        m_tether = new FixedTetherConstraint(
            length: length,
            minLength: minLength
        );
        m_tetherAnchorBodyIndex = bodyIndex;
        m_tetherAnchorPointOrLocalOffset = localOffset;
    }
    /// <summary>Detaches this body's tether. A no-op when none is attached.</summary>
    public void ClearTether() {
        m_tether = null;
        m_tetherAnchorBodyIndex = -1;
        m_tetherAnchorPointOrLocalOffset = FixedVector3.Zero;
    }
    /// <summary>Advances the attached tether's rope length at a caller-supplied rate. A no-op when no tether is
    /// attached. Call before <see cref="SolveTether"/> in the same tick so the same tick's cap reflects the same
    /// tick's reel — see <see cref="FixedTetherConstraint.Reel"/>.</summary>
    /// <param name="ratePerSecond">The signed rate the rope length changes at.</param>
    /// <param name="elapsedTicks">The number of engine ticks this call advances.</param>
    public void ReelTether(FixedQ4816 ratePerSecond, ulong elapsedTicks) {
        if (m_tether is not { } tether) {
            return;
        }

        tether.Reel(
            elapsedTicks: elapsedTicks,
            ratePerSecond: ratePerSecond
        );
        m_tether = tether;
    }

    /// <summary>Solves the attached tether against this tick's resolved anchor position, applying the same late,
    /// already-integrated-state correction <see cref="ApplyDynamicContact"/> applies for a dynamic body contact — the
    /// combined planar/vertical velocity is decomposed, corrected, and written back exactly the same way. A no-op when
    /// no tether is attached.</summary>
    /// <param name="anchor">The resolved anchor position this tick (a fixed world point, or the anchor body's current
    /// pose transformed by <see cref="FixedTetherConstraint.ResolveAnchor"/>).</param>
    internal void SolveTether(FixedVector3 anchor) {
        if (m_tether is not { } tether) {
            return;
        }

        var velocity = (m_planarVelocity + (UnitY * m_verticalVelocity));
        var result = tether.Solve(
            anchor: in anchor,
            position: ref m_position,
            velocity: ref velocity
        );

        m_tether = tether;

        if (!result.Taut) {
            return;
        }

        m_planarVelocity = new FixedVector3(
            X: velocity.X,
            Y: FixedQ4816.Zero,
            Z: velocity.Z
        );

        if (m_verticalVelocity != velocity.Y) {
            m_verticalVelocity = velocity.Y;
            m_verticalVelocityAccumulator.Reset();
        }
    }

    // Reads the attach/detach channels DIRECTLY (never through the kit action table — see FixedWorldTether's own
    // remarks on why it carries its own thresholds), fires at most one transition per tick: a detach edge always
    // wins over an attach edge held the SAME tick, and a fresh attach only ever starts while untethered (a body
    // already tethered ignores a second attach press — detach first).
    private void ProcessTetherIntent(in PlayerIntent intent) {
        if (m_tetherFacet is not { } policy) {
            return;
        }

        var attachOrdinal = policy.AttachChannelOrdinal;
        var detachOrdinal = policy.DetachChannelOrdinal;
        var attachHeld = ((attachOrdinal >= 0) && (intent[attachOrdinal] >= policy.AttachThreshold));
        var detachHeld = ((detachOrdinal >= 0) && (intent[detachOrdinal] >= policy.DetachThreshold));
        var attachEdge = (attachHeld && !m_attachPreviousBit);
        var detachEdge = (detachHeld && !m_detachPreviousBit);

        m_attachPreviousBit = attachHeld;
        m_detachPreviousBit = detachHeld;

        if (
            detachEdge &&
            (m_tether is not null)
        ) {
            DetachTether();

            return;
        }
        if (
            attachEdge &&
            (m_tether is null)
        ) {
            TryAttachTether(policy: in policy);
        }
    }
    // The held reel channel — meaningful only while attached (an unattached body has no rope to reel). Reads a raw
    // bipolar value every tick and hands it straight to the slice-1 rope kernel through ReelTether, exactly the
    // caller-supplied-rate contract that kernel already documents.
    private void ProcessTetherReel(in PlayerIntent intent, ulong stepTicks) {
        if (
            (m_tether is null) ||
            (m_tetherFacet is not { } policy) ||
            (policy.ReelChannelOrdinal < 0)
        ) {
            return;
        }

        var direction = intent[policy.ReelChannelOrdinal];

        ReelTether(
            elapsedTicks: stepTicks,
            ratePerSecond: (policy.LengthRate * direction)
        );
    }
    // The body's own facing tries an anchor within the aim-assist cone (directed — FixedSurfaceQuery.TryNearestDirected).
    // No candidate is a silent no-op, the same "nothing to act on" shape every other action-track effect in this
    // codebase takes.
    private void TryAttachTether(in FixedWorldTether policy) {
        if (
            (m_contactField is not { } field) ||
            (policy.MaxAnchorDistance <= FixedQ4816.Zero)
        ) {
            return;
        }

        var facing = m_orientation.Rotate(vector: -UnitZ);

        if (field.TryNearestSurfaceAlongDirection(
            assistHalfAngle: policy.AimHalfAngle,
            candidate: out var anchorCandidate,
            direction: in facing,
            maxDistance: policy.MaxAnchorDistance,
            origin: in m_position
        )) {
            AttachTether(candidate: in anchorCandidate);
        }
    }
    // Attaches: the tether's initial length is the resolved anchor's ACTUAL distance (always <= MaxAnchorDistance,
    // since that bounded the query), never the authored ceiling itself — a closer anchor grants a shorter rope,
    // exactly the reach a body's current position earned. The reel-in floor clamps down to that same distance when
    // authored larger than what this attach actually found (FixedTetherConstraint's constructor requires
    // length >= minLength).
    private void AttachTether(in FixedSurfaceAttachCandidate candidate) {
        var minLength = ((m_tetherFacet!.Value.MinLength <= candidate.Distance)
            ? m_tetherFacet.Value.MinLength
            : candidate.Distance
        );

        SetTetherToWorldPoint(
            anchor: candidate.Point,
            length: candidate.Distance,
            minLength: minLength
        );
        WriteTetherModeState();
    }
    // Clears the tether and restores ordinary locomotion, carrying the release-velocity-scale field into the
    // velocity the attach leaves behind: gravity never stopped integrating the ordinary channels, so the scale
    // applies to them directly.
    private void DetachTether() {
        if (m_tether is null) {
            return;
        }

        var scale = (m_tetherFacet?.ReleaseVelocityScale ?? FixedQ4816.One);

        m_planarVelocity = (m_planarVelocity * scale);
        m_verticalVelocity = (m_verticalVelocity * scale);
        m_verticalVelocityAccumulator.Reset();
        ClearTether();
        WriteTetherModeState();
    }
    // Echoes whether this body is attached into the facet's OPTIONAL modeState slot, resolved to an ordinal at kit
    // compile time (FixedWorldTether.ModeStateOrdinal) — never a runtime name scan. The camera program's `select`
    // op keys off state.<row> exactly like it keys off any other body-owned state cell; a facet declaring no such
    // slot (or no facet at all) writes nothing.
    private void WriteTetherModeState() {
        var slot = (m_tetherFacet?.ModeStateOrdinal ?? -1);

        if (slot < 0) {
            return;
        }

        ApplyRawState(
            reason: "tether.mode",
            requested: FixedQ4816.FromInteger(value: (m_tether is not null ? 1L : 0L)).Value,
            slot: slot,
            writer: "tether"
        );
        MarkDurableDirty(slot: slot);
    }
}
