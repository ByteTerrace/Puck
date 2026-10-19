using System.Numerics;
using Puck.Physics;

namespace Puck.World.Client;

/// <summary>
/// The body a chase camera frames, as the volume its eye keeps out of: the body's kit collider, placed at the pose the
/// body is drawn at and scaled by its live scale. Presentation-only and allocation-free; the collider's volumes are read
/// in place and converted to single precision per query.
/// </summary>
public readonly struct WorldChaseBody {
    // Below this a direction component counts as parallel to a slab or an axis.
    private const float ParallelEpsilon = 1e-6f;

    private readonly FixedWorldCollider m_collider;
    private readonly Quaternion m_inverseOrientation;
    private readonly Vector3 m_position;
    private readonly float m_scale;

    /// <summary>Initializes a new instance of the <see cref="WorldChaseBody"/> struct.</summary>
    /// <param name="collider">The body's compiled kit collider, or <see langword="null"/> for a body with none, which
    /// keeps the eye out of nothing.</param>
    /// <param name="position">The body root's presented position, in world units.</param>
    /// <param name="orientation">The body's presented orientation.</param>
    /// <param name="scale">The body's live uniform scale; a value that is not positive and finite counts as 1.</param>
    public WorldChaseBody(FixedWorldCollider? collider, Vector3 position, Quaternion orientation, float scale) {
        m_collider = collider.GetValueOrDefault();
        m_inverseOrientation = Quaternion.Inverse(value: ((orientation.LengthSquared() > 0f)
            ? Quaternion.Normalize(value: orientation)
            : Quaternion.Identity
        ));
        m_position = position;
        m_scale = ((float.IsFinite(f: scale) && (scale > 0f))
            ? scale
            : 1f
        );
    }

    // The interval of t over which origin + t·direction lies within radius of a sphere, or false when it never does.
    private static bool SphereInterval(Vector3 origin, Vector3 direction, Vector3 center, float radius, out float enter, out float exit) {
        var relative = (origin - center);
        var half = Vector3.Dot(
            vector1: relative,
            vector2: direction
        );
        var discriminant = ((half * half) - (relative.LengthSquared() - (radius * radius)));

        if (discriminant < 0f) {
            enter = 0f;
            exit = 0f;

            return false;
        }

        var root = MathF.Sqrt(x: discriminant);

        enter = (-half - root);
        exit = (-half + root);

        return true;
    }
    // The last t at which the ray is inside the capsule from lower to upper inflated to radius: the capsule is the union
    // of its two end spheres and the finite cylinder between them, and the last point inside a union is the latest of the
    // last points inside its pieces.
    private static float CapsuleExit(Vector3 origin, Vector3 direction, Vector3 lower, Vector3 upper, float radius) {
        var exit = float.NegativeInfinity;

        if (SphereInterval(
            center: lower,
            direction: direction,
            enter: out _,
            exit: out var lowerExit,
            origin: origin,
            radius: radius
        )) {
            exit = lowerExit;
        }

        if (SphereInterval(
            center: upper,
            direction: direction,
            enter: out _,
            exit: out var upperExit,
            origin: origin,
            radius: radius
        )) {
            exit = MathF.Max(
                x: exit,
                y: upperExit
            );
        }

        var axis = (upper - lower);
        var axisLength = axis.Length();

        if (axisLength <= ParallelEpsilon) {
            return exit;
        }

        axis /= axisLength;

        // The finite cylinder: the infinite cylinder's interval clipped to the slab between the end caps.
        var relative = (origin - lower);
        var alongDirection = Vector3.Dot(
            vector1: direction,
            vector2: axis
        );
        var alongRelative = Vector3.Dot(
            vector1: relative,
            vector2: axis
        );
        var radialDirection = (direction - (axis * alongDirection));
        var radialRelative = (relative - (axis * alongRelative));
        var a = radialDirection.LengthSquared();
        float cylinderEnter;
        float cylinderExit;

        if (a <= ParallelEpsilon) {
            if (radialRelative.LengthSquared() > (radius * radius)) {
                return exit;
            }

            cylinderEnter = float.NegativeInfinity;
            cylinderExit = float.PositiveInfinity;
        } else {
            var half = Vector3.Dot(
                vector1: radialDirection,
                vector2: radialRelative
            );
            var discriminant = ((half * half) - (a * (radialRelative.LengthSquared() - (radius * radius))));

            if (discriminant < 0f) {
                return exit;
            }

            var root = MathF.Sqrt(x: discriminant);

            cylinderEnter = ((-half - root) / a);
            cylinderExit = ((-half + root) / a);
        }

        if (!SlabInterval(
            enter: out var slabEnter,
            exit: out var slabExit,
            maximum: axisLength,
            minimum: 0f,
            origin: alongRelative,
            rate: alongDirection
        )) {
            return exit;
        }

        var pieceEnter = MathF.Max(
            x: cylinderEnter,
            y: slabEnter
        );
        var pieceExit = MathF.Min(
            x: cylinderExit,
            y: slabExit
        );

        return ((pieceEnter <= pieceExit)
            ? MathF.Max(
                x: exit,
                y: pieceExit
            )
            : exit
        );
    }
    // The interval of t over which origin + t·rate lies in [minimum, maximum], or false when it never does.
    private static bool SlabInterval(float origin, float rate, float minimum, float maximum, out float enter, out float exit) {
        if (MathF.Abs(x: rate) <= ParallelEpsilon) {
            enter = float.NegativeInfinity;
            exit = float.PositiveInfinity;

            return ((origin >= minimum) && (origin <= maximum));
        }

        var first = ((minimum - origin) / rate);
        var second = ((maximum - origin) / rate);

        enter = MathF.Min(
            x: first,
            y: second
        );
        exit = MathF.Max(
            x: first,
            y: second
        );

        return true;
    }
    // The last t at which the ray is inside the box grown by radius on every face, a slight overestimate of the rounded
    // box the sphere actually sweeps out at its corners and edges.
    private static float BoxExit(Vector3 origin, Vector3 direction, Vector3 center, Vector3 halfExtents, Quaternion rotation, float radius) {
        var inverse = Quaternion.Inverse(value: rotation);
        var localOrigin = Vector3.Transform(
            rotation: inverse,
            value: (origin - center)
        );
        var localDirection = Vector3.Transform(
            rotation: inverse,
            value: direction
        );
        var grown = (halfExtents + new Vector3(value: radius));
        var enter = float.NegativeInfinity;
        var exit = float.PositiveInfinity;

        for (var axis = 0; (axis < 3); axis++) {
            if (!SlabInterval(
                enter: out var axisEnter,
                exit: out var axisExit,
                maximum: grown[axis],
                minimum: -grown[axis],
                origin: localOrigin[axis],
                rate: localDirection[axis]
            )) {
                return float.NegativeInfinity;
            }

            enter = MathF.Max(
                x: enter,
                y: axisEnter
            );
            exit = MathF.Min(
                x: exit,
                y: axisExit
            );
        }

        return ((enter <= exit)
            ? exit
            : float.NegativeInfinity
        );
    }

    /// <summary>Returns how far along a ray a sphere must travel before it clears every volume of the body the ray
    /// passes through: the latest point at which the swept sphere still touches any of them.</summary>
    /// <param name="origin">The ray's origin, in world units; the chase target, which sits inside the body.</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <param name="radius">The radius of the sphere the eye keeps clear, in world units; at least zero.</param>
    /// <returns>The distance from <paramref name="origin"/>, in world units; zero when the ray meets no volume ahead of
    /// its origin, including for a body with no collider.</returns>
    public float Exit(Vector3 origin, Vector3 direction, float radius) {
        var volumes = m_collider.Volumes;

        if (volumes.IsEmpty) {
            return 0f;
        }

        var localOrigin = (Vector3.Transform(
            rotation: m_inverseOrientation,
            value: (origin - m_position)
        ) / m_scale);
        var localDirection = Vector3.Normalize(value: Vector3.Transform(
            rotation: m_inverseOrientation,
            value: direction
        ));
        var localRadius = (MathF.Max(
            x: radius,
            y: 0f
        ) / m_scale);
        var exit = 0f;

        foreach (ref readonly var volume in volumes) {
            var volumeRadius = (localRadius + ((float)(double)volume.Radius));
            var volumeExit = (volume.Kind switch {
                FixedBodyColliderKind.Sphere => (SphereInterval(
                    center: volume.Center.ToVector3(),
                    direction: localDirection,
                    enter: out _,
                    exit: out var sphereExit,
                    origin: localOrigin,
                    radius: volumeRadius
                )
                    ? sphereExit
                    : float.NegativeInfinity
                ),
                FixedBodyColliderKind.Capsule => CapsuleExit(
                    direction: localDirection,
                    lower: volume.Center.ToVector3(),
                    origin: localOrigin,
                    radius: volumeRadius,
                    upper: volume.Endpoint.ToVector3()
                ),
                _ => BoxExit(
                    center: volume.Center.ToVector3(),
                    direction: localDirection,
                    halfExtents: volume.HalfExtents.ToVector3(),
                    origin: localOrigin,
                    radius: volumeRadius,
                    rotation: volume.Rotation.ToQuaternion()
                ),
            });

            exit = MathF.Max(
                x: exit,
                y: volumeExit
            );
        }

        return (exit * m_scale);
    }
}
