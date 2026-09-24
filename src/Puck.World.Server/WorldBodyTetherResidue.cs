using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>The checkpoint-only tether state that remains meaningful only inside the same authoritative world's
/// coordinate frame. It is intentionally not part of <see cref="WorldBodyTransferState"/>: a cross-world transfer cannot
/// carry a tether anchor whose geometry belongs to the source authority.</summary>
/// <param name="AttachPreviousBit">The attach channel's previous threshold-crossing image.</param>
/// <param name="DetachPreviousBit">The detach channel's previous threshold-crossing image.</param>
/// <param name="Tether">The complete rope constraint state, or <see langword="null"/> when none is attached —
/// the single source of truth for whether this body is currently tethered.</param>
/// <param name="TetherAnchorBodyIndex">The anchor body index, or <c>-1</c> for a world-point tether.</param>
/// <param name="TetherAnchorPointOrLocalOffset">The world point or body-local anchor offset.</param>
public readonly record struct WorldBodyTetherResidue(
    bool AttachPreviousBit,
    bool DetachPreviousBit,
    FixedTetherConstraintState? Tether,
    int TetherAnchorBodyIndex,
    FixedVector3 TetherAnchorPointOrLocalOffset
);
