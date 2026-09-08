using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// Computes the true support ("witness") point of a body-local collider volume along a contact normal, oriented by
/// the body's own quaternion — the actual point a sphere, capsule, or box collider touches a surface at, rather than
/// a single point read off the body's conservative bounding sphere along the normal. A normal impulse applied at a
/// witness point carries a lever arm (<c>r = anchor</c>, relative to the body's centre of mass) away from that
/// centre whenever the true contact sits off-axis, so it imparts torque; the bounding-sphere approximation always
/// anchors a normal impulse on the line through the centre, which can carry none.
/// </summary>
public static class FixedRigidWitness {
    /// <summary>The point of <paramref name="volume"/>, in the volume's own declared local frame (the body-root-
    /// relative frame its <c>Center</c>/<c>Endpoint</c>/<c>HalfExtents</c> are authored in — see
    /// <see cref="FixedBodyColliderVolume"/>), furthest along <paramref name="localDirection"/>. Exact for a sphere
    /// and a capsule — the true extreme point of each round shape; for a box, the extreme CORNER along the
    /// direction — the true support point everywhere the direction is not itself face-aligned, and a legitimate
    /// point ON the support face even where it is (the manifold build enumerates every corner independently, so it
    /// never depends on this one landing on a particular corner).</summary>
    /// <param name="volume">The body-local collider volume, already scaled for the body's live scale.</param>
    /// <param name="localDirection">The direction, in the volume's own local frame; need not be unit length. A zero
    /// direction normalizes to <see cref="FixedVector3.Zero"/> and falls back to the volume's own local center — a
    /// degenerate caller (a zero contact normal) has no better answer.</param>
    public static FixedVector3 LocalSupportPoint(FixedBodyColliderVolume volume, FixedVector3 localDirection) {
        var unit = localDirection.Normalize();

        switch (volume.Kind) {
            case FixedBodyColliderKind.Sphere:
                return (volume.Center + (unit * volume.Radius));

            case FixedBodyColliderKind.Capsule: {
                var axis = (volume.Endpoint - volume.Center);
                var axisPoint = ((FixedVector3.Dot(left: unit, right: axis) > FixedQ4816.Zero)
                    ? volume.Endpoint
                    : volume.Center
                );

                return (axisPoint + (unit * volume.Radius));
            }

            case FixedBodyColliderKind.Box: {
                var boxLocal = volume.Rotation.RotateInverse(vector: unit);
                var corner = new FixedVector3(
                    X: ((boxLocal.X >= FixedQ4816.Zero) ? volume.HalfExtents.X : -volume.HalfExtents.X),
                    Y: ((boxLocal.Y >= FixedQ4816.Zero) ? volume.HalfExtents.Y : -volume.HalfExtents.Y),
                    Z: ((boxLocal.Z >= FixedQ4816.Zero) ? volume.HalfExtents.Z : -volume.HalfExtents.Z)
                );

                return (volume.Center + volume.Rotation.Rotate(vector: corner));
            }

            default:
                return volume.Center;
        }
    }

    /// <summary>The witness-point contact anchor, RELATIVE TO THE BODY'S CENTRE OF MASS, in world axes — the value
    /// <see cref="FixedTwoBodyKernel"/>'s own <c>anchorA</c>/<c>anchorB</c> parameters want, and what
    /// <see cref="FixedRigidWorld"/>'s own pair slots carry. Rotates <paramref name="worldDirection"/> into the
    /// body's local frame, finds <see cref="LocalSupportPoint"/> along it, subtracts the collider's own centre-of-
    /// mass offset (also body-local), then rotates the remainder back to world axes.</summary>
    /// <param name="volume">The body-local collider volume (already scaled for the body's live scale).</param>
    /// <param name="centerOffset">The collider's own centre-of-mass offset from the body root, body-local axes,
    /// scale-consistent with <paramref name="volume"/>.</param>
    /// <param name="orientation">The body's current world orientation.</param>
    /// <param name="worldDirection">The direction, in world axes, the support point is sought along — the direction
    /// FROM the body's interior TOWARD the contact (e.g. <c>-normal</c> for a body resting on a normal that points
    /// away from the surface toward the body; <c>+normal</c> for the body the normal points toward).</param>
    public static FixedVector3 Anchor(FixedBodyColliderVolume volume, FixedVector3 centerOffset, FixedQuaternion orientation, FixedVector3 worldDirection) {
        var localDirection = orientation.RotateInverse(vector: worldDirection);
        var localSupport = LocalSupportPoint(
            localDirection: localDirection,
            volume: volume
        );

        return orientation.Rotate(vector: (localSupport - centerOffset));
    }

    // cos(45deg) at Q16: a capsule axis within this alignment of the contact normal reads as "standing" (one cap
    // touches); further from it reads as "lying on its side" (both caps touch a flat support at once). A geometric
    // classification threshold, not an authored tunable — every capsule reads the same boundary between one contact
    // point and two.
    private static readonly FixedQ4816 LyingOnSideAlignmentCeiling = FixedQ4816.FromDouble(value: 0.70710678d);

    // The fraction of a box's own half-extent span (HalfExtents.Length) two candidate corners' depths along the
    // contact normal may differ by and still count as the same support point. A geometric classification threshold,
    // not an authored tunable, on the same terms as LyingOnSideAlignmentCeiling above — scaled by the shape rather
    // than a flat world-unit slack so it never swallows a thin body's own critical tipping range.
    private static readonly FixedQ4816 DepthToleranceFraction = FixedQ4816.FromDouble(value: 0.0625d);

    /// <summary>Builds the resting support manifold for <paramref name="volume"/> against a near-horizontal contact
    /// <paramref name="normal"/>: the box's own true support corners near its face closest to the surface (up to
    /// four; fewer once the body is tilted enough that not every corner of that face is still the deepest — see
    /// <see cref="DepthToleranceFraction"/>) or, for a capsule lying on its side, its two end-sphere centres offset
    /// out to the surface — each anchor on the same terms as <see cref="Anchor"/> (relative to the body's centre of
    /// mass, world axes). A standing capsule, a sphere, or any other volume kind writes exactly the single point
    /// <see cref="Anchor"/> itself would compute — a manifold caller can always read the returned count as "the
    /// manifold, one point or many" without a separate branch.</summary>
    /// <param name="volume">The body-local collider volume (already scaled for the body's live scale).</param>
    /// <param name="centerOffset">The collider's own centre-of-mass offset from the body root, body-local axes.</param>
    /// <param name="orientation">The body's current world orientation.</param>
    /// <param name="normal">The contact normal, world axes, pointing away from the surface toward the body.</param>
    /// <param name="anchors">Receives the manifold's anchors; must hold at least 4.</param>
    /// <returns>The number of anchors written: 1-4 for a box (fewer once tilted), 2 for a side-lying capsule, 1
    /// otherwise.</returns>
    public static int SupportManifold(FixedBodyColliderVolume volume, FixedVector3 centerOffset, FixedQuaternion orientation, FixedVector3 normal, Span<FixedVector3> anchors) {
        var into = -normal;
        var localInto = orientation.RotateInverse(vector: into).Normalize();

        switch (volume.Kind) {
            case FixedBodyColliderKind.Box: {
                var half = volume.HalfExtents;
                var absX = FixedQ4816.Abs(value: localInto.X);
                var absY = FixedQ4816.Abs(value: localInto.Y);
                var absZ = FixedQ4816.Abs(value: localInto.Z);
                Span<FixedVector3> corners = stackalloc FixedVector3[4];

                if ((absX >= absY) && (absX >= absZ)) {
                    var x = ((localInto.X >= FixedQ4816.Zero) ? half.X : -half.X);

                    corners[0] = new FixedVector3(X: x, Y: half.Y, Z: half.Z);
                    corners[1] = new FixedVector3(X: x, Y: half.Y, Z: -half.Z);
                    corners[2] = new FixedVector3(X: x, Y: -half.Y, Z: half.Z);
                    corners[3] = new FixedVector3(X: x, Y: -half.Y, Z: -half.Z);
                } else if (absY >= absZ) {
                    var y = ((localInto.Y >= FixedQ4816.Zero) ? half.Y : -half.Y);

                    corners[0] = new FixedVector3(X: half.X, Y: y, Z: half.Z);
                    corners[1] = new FixedVector3(X: half.X, Y: y, Z: -half.Z);
                    corners[2] = new FixedVector3(X: -half.X, Y: y, Z: half.Z);
                    corners[3] = new FixedVector3(X: -half.X, Y: y, Z: -half.Z);
                } else {
                    var z = ((localInto.Z >= FixedQ4816.Zero) ? half.Z : -half.Z);

                    corners[0] = new FixedVector3(X: half.X, Y: half.Y, Z: z);
                    corners[1] = new FixedVector3(X: half.X, Y: -half.Y, Z: z);
                    corners[2] = new FixedVector3(X: -half.X, Y: half.Y, Z: z);
                    corners[3] = new FixedVector3(X: -half.X, Y: -half.Y, Z: z);
                }

                // The four corners share the box's dominant-axis coordinate, so they are coplanar in the box's LOCAL
                // frame; under any tilt they sit at different world depths along `into`, and only the deepest one
                // (or, for a single-axis tilt, the deepest edge pair) is the true support point — the others are a
                // torque-free rectangle only at exact rest. Keep candidates within DepthToleranceFraction of the
                // deepest, scaled by the box's own half-extent span rather than a fixed world-unit slack (which
                // would cover a thin body's whole tipping range — see DepthToleranceFraction).
                Span<FixedVector3> candidates = stackalloc FixedVector3[4];
                Span<FixedQ4816> depths = stackalloc FixedQ4816[4];
                var deepest = FixedQ4816.MinValue;

                for (var index = 0; (index < 4); index++) {
                    var local = (volume.Center + volume.Rotation.Rotate(vector: corners[index]));
                    var anchor = orientation.Rotate(vector: (local - centerOffset));
                    var depth = FixedVector3.Dot(left: anchor, right: into);

                    candidates[index] = anchor;
                    depths[index] = depth;
                    deepest = FixedQ4816.Max(x: deepest, y: depth);
                }

                var depthTolerance = (half.Length * DepthToleranceFraction);
                var written = 0;

                for (var index = 0; (index < 4); index++) {
                    if ((deepest - depths[index]) <= depthTolerance) {
                        anchors[written] = candidates[index];
                        written++;
                    }
                }

                return written;
            }

            case FixedBodyColliderKind.Capsule: {
                var axis = (volume.Endpoint - volume.Center);
                var axisLength = axis.Length;

                if (axisLength <= FixedQ4816.Zero) {
                    anchors[0] = Anchor(volume: volume, centerOffset: centerOffset, orientation: orientation, worldDirection: into);
                    return 1;
                }

                var axisUnit = (axis / axisLength);
                var alignment = FixedQ4816.Abs(value: FixedVector3.Dot(left: axisUnit, right: localInto));

                if (alignment >= LyingOnSideAlignmentCeiling) {
                    anchors[0] = Anchor(volume: volume, centerOffset: centerOffset, orientation: orientation, worldDirection: into);
                    return 1;
                }

                var capOffset = (localInto * volume.Radius);

                anchors[0] = orientation.Rotate(vector: ((volume.Center + capOffset) - centerOffset));
                anchors[1] = orientation.Rotate(vector: ((volume.Endpoint + capOffset) - centerOffset));
                return 2;
            }

            default:
                anchors[0] = Anchor(volume: volume, centerOffset: centerOffset, orientation: orientation, worldDirection: into);
                return 1;
        }
    }
}
