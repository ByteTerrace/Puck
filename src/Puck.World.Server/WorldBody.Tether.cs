using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // The attached tether's anchor kind and geometry. m_tether.HasValue is the single source of truth for "does this
    // body have a tether"; the two anchor fields below are meaningless while it is null, and m_tetherAnchorBodyIndex
    // doubles as the discriminator (-1 selects the world-point form, matching the -1-means-none convention every other
    // resolved-ordinal field in this class already uses).
    private FixedTetherConstraint? m_tether;
    private int m_tetherAnchorBodyIndex = -1;
    private FixedVector3 m_tetherAnchorPointOrLocalOffset;

    /// <summary>Gets the attached tether's current rope length, or <see langword="null"/> when no tether is attached.</summary>
    public FixedQ4816? TetherLength => (m_tether is { } tether
        ? tether.Length
        : null
    );
    /// <summary>Gets the attached tether's minimum rope length (the reel-in floor), or <see langword="null"/> when no
    /// tether is attached.</summary>
    public FixedQ4816? TetherMinLength => (m_tether is { } tether
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

    /// <summary>Attaches a tether anchored to a fixed world point, replacing any tether already attached.</summary>
    /// <param name="anchor">The fixed world point.</param>
    /// <param name="length">The initial rope length. Must be at least <paramref name="minLength"/>.</param>
    /// <param name="minLength">The floor a reel-in clamps to. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minLength"/> is negative, or
    /// <paramref name="length"/> is less than <paramref name="minLength"/>.</exception>
    internal void SetTetherToWorldPoint(FixedVector3 anchor, FixedQ4816 length, FixedQ4816 minLength) {
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
    internal void SetTetherToBody(int bodyIndex, FixedVector3 localOffset, FixedQ4816 length, FixedQ4816 minLength) {
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
    internal void ClearTether() {
        m_tether = null;
        m_tetherAnchorBodyIndex = -1;
        m_tetherAnchorPointOrLocalOffset = FixedVector3.Zero;
    }
    /// <summary>Advances the attached tether's rope length at a caller-supplied rate. A no-op when no tether is
    /// attached. Call before <see cref="SolveTether"/> in the same tick so the same tick's cap reflects the same
    /// tick's reel — see <see cref="FixedTetherConstraint.Reel"/>.</summary>
    /// <param name="ratePerSecond">The signed rate the rope length changes at.</param>
    /// <param name="elapsedTicks">The number of engine ticks this call advances.</param>
    internal void ReelTether(FixedQ4816 ratePerSecond, ulong elapsedTicks) {
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
}
