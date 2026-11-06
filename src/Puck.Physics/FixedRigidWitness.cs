using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// Computes the true support ("witness") point of a body-local collider volume along a contact normal, oriented by
/// the body's own quaternion — the actual point a sphere, capsule, box or ellipsoid touches a surface at, rather than
/// a single point read off the body's conservative bounding sphere along the normal. A normal impulse applied at a
/// witness point carries a lever arm (<c>r = anchor</c>, relative to the body's centre of mass) away from that
/// centre whenever the true contact sits off-axis, so it imparts torque; the bounding-sphere approximation always
/// anchors a normal impulse on the line through the centre, which can carry none.
/// </summary>
public static class FixedRigidWitness {
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
            case FixedBodyColliderKind.Ellipsoid: {
                    var shape = FixedStaticCollider.Superellipsoid(FixedVector3.Zero, FixedQuaternion.Identity,
                        volume.HalfExtents, FixedQ4816.FromInteger(value: 2));
                    var support = FixedConvexGeometry.Support(collider: in shape, direction: volume.Rotation.RotateInverse(vector: unit));

                    return (volume.Center + volume.Rotation.Rotate(vector: support));
                }
            case FixedBodyColliderKind.Sphere:
                return (volume.Center + (unit * volume.Radius));

            case FixedBodyColliderKind.Capsule: {
                    var axis = (volume.Endpoint - volume.Center);
                    var axisPoint = ((FixedVector3.Dot(
                        left: unit,
                        right: axis
                    ) > FixedQ4816.Zero)
                        ? volume.Endpoint
                        : volume.Center
                    );

                    return (axisPoint + (unit * volume.Radius));
                }

            case FixedBodyColliderKind.Box: {
                    var boxLocal = volume.Rotation.RotateInverse(vector: unit);
                    var corner = new FixedVector3(
                        X: ((boxLocal.X >= FixedQ4816.Zero)
                        ? volume.HalfExtents.X
                        : -volume.HalfExtents.X),
                        Y: ((boxLocal.Y >= FixedQ4816.Zero)
                        ? volume.HalfExtents.Y
                        : -volume.HalfExtents.Y),
                        Z: ((boxLocal.Z >= FixedQ4816.Zero)
                        ? volume.HalfExtents.Z
                        : -volume.HalfExtents.Z)
                    );

                    return (volume.Center + volume.Rotation.Rotate(vector: corner));
                }

            default:
                return volume.Center;
        }
    }
    /// <summary>The largest number of points <see cref="SupportManifold"/> writes.</summary>
    public const int MaximumSupportPoints = 5;
    // The tilt of an ellipsoid's ring points from straight into the contact: cos and sin of 20 degrees.
    private static readonly FixedQ4816 RingCosine = FixedQ4816.FromDouble(value: 0.93969262078590838d);
    private static readonly FixedQ4816 RingSine = FixedQ4816.FromDouble(value: 0.34202014332566873d);

    /// <summary>Builds the support manifold of <paramref name="volume"/> facing a contact: for a box, the four corners of
    /// the face whose outward normal is closest to the direction into the contact, each named by the box corner it is
    /// (bit 2 set for the positive X half extent, bit 1 for Y, bit 0 for Z), so a corner keeps its identity while the face
    /// it belongs to changes; for a capsule with a length, the support points of both end spheres (feature 0 for the
    /// lower endpoint, 1 for the upper); for an ellipsoid, its support point (feature 0) and a ring of four support points
    /// along directions tilted 20 degrees from it toward each side (features 1 to 4), so a flat ellipsoid that rocks meets
    /// its surface within the step rather than pivoting on one point until the next; one support point, feature 0, for a
    /// sphere. Each anchor is on the same
    /// terms as <see cref="Anchor"/>: relative to the body's centre of mass, in world axes. Points that do not reach the
    /// contact are written too — a caller gives each its own separation, so a corner lifted off the surface is a
    /// speculative contact rather than a missing one.</summary>
    /// <param name="volume">The body-local collider volume, already scaled for the body's live scale.</param>
    /// <param name="centerOffset">The collider's own centre-of-mass offset from the body root, body-local axes.</param>
    /// <param name="orientation">The body's current world orientation.</param>
    /// <param name="normal">The contact normal, world axes, pointing away from the contact toward the body.</param>
    /// <param name="anchors">The destination for the anchors; holds at least <see cref="MaximumSupportPoints"/>.</param>
    /// <param name="features">The destination for each anchor's feature id; holds at least
    /// <see cref="MaximumSupportPoints"/>.</param>
    /// <returns>The number of points written: 5 for an ellipsoid, 4 for a box, 2 for a capsule with a length, 1 otherwise.</returns>
    public static int SupportManifold(FixedBodyColliderVolume volume, FixedVector3 centerOffset, FixedQuaternion orientation, FixedVector3 normal, Span<FixedVector3> anchors, Span<int> features) {
        var into = -normal;

        switch (volume.Kind) {
            case FixedBodyColliderKind.Box: {
                    var boxInto = volume.Rotation.RotateInverse(vector: orientation.RotateInverse(vector: into));
                    var absX = FixedQ4816.Abs(value: boxInto.X);
                    var absY = FixedQ4816.Abs(value: boxInto.Y);
                    var absZ = FixedQ4816.Abs(value: boxInto.Z);
                    // The face's fixed axis and its sign; the other two axes run over both signs.
                    var axis = (((absX >= absY) && (absX >= absZ))
                        ? 0
                        : ((absY >= absZ) ? 1 : 2)
                    );
                    var positive = (axis switch {
                        0 => (boxInto.X >= FixedQ4816.Zero),
                        1 => (boxInto.Y >= FixedQ4816.Zero),
                        _ => (boxInto.Z >= FixedQ4816.Zero),
                    });
                    var written = 0;

                    for (var corner = 0; (corner < 8); corner++) {
                        var bit = (1 << (2 - axis));

                        if (((corner & bit) != 0) != positive) {
                            continue;
                        }

                        var local = new FixedVector3(
                            X: (((corner & 4) != 0) ? volume.HalfExtents.X : -volume.HalfExtents.X),
                            Y: (((corner & 2) != 0) ? volume.HalfExtents.Y : -volume.HalfExtents.Y),
                            Z: (((corner & 1) != 0) ? volume.HalfExtents.Z : -volume.HalfExtents.Z)
                        );

                        anchors[written] = orientation.Rotate(vector: ((volume.Center + volume.Rotation.Rotate(vector: local)) - centerOffset));
                        features[written] = corner;
                        written++;
                    }

                    return written;
                }

            case FixedBodyColliderKind.Ellipsoid: {
                    anchors[0] = Anchor(
                        centerOffset: centerOffset,
                        orientation: orientation,
                        volume: volume,
                        worldDirection: into
                    );
                    features[0] = 0;
                    FixedVector3.OrthonormalBasis(
                        normal: into,
                        tangent1: out var tangent1,
                        tangent2: out var tangent2
                    );

                    for (var ring = 0; (ring < 4); ring++) {
                        var side = (ring switch {
                            0 => tangent1,
                            1 => -tangent1,
                            2 => tangent2,
                            _ => -tangent2,
                        });

                        anchors[(ring + 1)] = Anchor(
                            centerOffset: centerOffset,
                            orientation: orientation,
                            volume: volume,
                            worldDirection: ((into * RingCosine) + (side * RingSine))
                        );
                        features[(ring + 1)] = (ring + 1);
                    }

                    return 5;
                }

            case FixedBodyColliderKind.Capsule when (volume.Endpoint != volume.Center): {
                    var capOffset = (orientation.RotateInverse(vector: into).Normalize() * volume.Radius);

                    anchors[0] = orientation.Rotate(vector: ((volume.Center + capOffset) - centerOffset));
                    features[0] = 0;
                    anchors[1] = orientation.Rotate(vector: ((volume.Endpoint + capOffset) - centerOffset));
                    features[1] = 1;

                    return 2;
                }

            default:
                anchors[0] = Anchor(
                    centerOffset: centerOffset,
                    orientation: orientation,
                    volume: volume,
                    worldDirection: into
                );
                features[0] = 0;

                return 1;
        }
    }
    /// <summary>Returns the point of a box volume grown by a margin on every axis nearest a world point: the point
    /// itself when it lies inside, otherwise the point clamped onto the grown box along the box's own axes.</summary>
    /// <param name="volume">The body-local box volume.</param>
    /// <param name="root">The body root's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="point">The world point.</param>
    /// <param name="margin">The non-negative growth on every axis.</param>
    /// <returns>The clamped world point.</returns>
    public static FixedVector3 BoxClamp(FixedBodyColliderVolume volume, FixedVector3 root, FixedQuaternion orientation, FixedVector3 point, FixedQ4816 margin) {
        var local = volume.Rotation.RotateInverse(vector: (orientation.RotateInverse(vector: (point - root)) - volume.Center));
        var limit = (volume.HalfExtents + new FixedVector3(X: margin, Y: margin, Z: margin));
        var clamped = new FixedVector3(
            X: FixedQ4816.Clamp(value: local.X, minimum: -limit.X, maximum: limit.X),
            Y: FixedQ4816.Clamp(value: local.Y, minimum: -limit.Y, maximum: limit.Y),
            Z: FixedQ4816.Clamp(value: local.Z, minimum: -limit.Z, maximum: limit.Z)
        );

        return ((clamped == local)
            ? point
            : (root + orientation.Rotate(vector: (volume.Center + volume.Rotation.Rotate(vector: clamped))))
        );
    }
}
