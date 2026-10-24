using System.Numerics;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The body a chase camera frames, as the volume its eye keeps out of: the body's kit collider and the shapes it is drawn
/// with, placed at the pose the body is drawn at and scaled by its live scale. The drawn shapes reach past the collider
/// (armour, a helmet, a pack), and an eye inside one starts its view inside her, so both count. Presentation-only and
/// allocation-free; the collider's volumes are read in place and converted to single precision per query.
/// </summary>
public readonly struct WorldChaseBody {
    // Below this a direction component counts as parallel to a slab or an axis.
    private const float ParallelEpsilon = 1e-6f;

    private readonly FixedWorldCollider m_collider;
    private readonly ReadOnlyMemory<Vector4> m_drawn;
    private readonly Quaternion m_inverseOrientation;
    private readonly Vector3 m_position;
    private readonly float m_scale;

    /// <summary>Places an authored full-size local point at this body's presented pose and live scale.</summary>
    public Vector3 Point(Vector3 local) => (m_position + Vector3.Transform(local * m_scale, Quaternion.Conjugate(m_inverseOrientation)));

    /// <summary>Initializes a new instance of the <see cref="WorldChaseBody"/> struct.</summary>
    /// <param name="collider">The body's compiled kit collider, or <see langword="null"/> for a body with none.</param>
    /// <param name="position">The body root's presented position, in world units.</param>
    /// <param name="orientation">The body's presented orientation.</param>
    /// <param name="scale">The body's live uniform scale; a value that is not positive and finite counts as 1.</param>
    /// <param name="drawn">The bounding spheres of the shapes the body is drawn with (<see cref="DrawnVolumes"/>), in
    /// the body's own frame at its unscaled size: the centre in X, Y and Z and the radius in W. Empty keeps the eye out
    /// of the collider alone.</param>
    public WorldChaseBody(FixedWorldCollider? collider, Vector3 position, Quaternion orientation, float scale, ReadOnlyMemory<Vector4> drawn = default) {
        m_collider = collider.GetValueOrDefault();
        m_drawn = drawn;
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

    /// <summary>Returns the bounding sphere of every shape a creation draws, at its rest pose and stamped at
    /// <paramref name="scale"/>: the volume a chase eye keeps out of beside the collider.</summary>
    /// <param name="document">The creation, in the engine frame (<see cref="WorldPrototype.EngineDocument"/>).</param>
    /// <param name="scale">The creation's stamp scale on the body (the placement and look scales, without the live
    /// body scale).</param>
    /// <returns>One sphere per shape that adds material: the centre in X, Y and Z and the radius in W. A shape that only
    /// cuts or clips the shapes before it (an intersection or a subtraction, <see cref="SdfBoundAlgebra"/>) adds none and
    /// has no sphere, and a shape folded across a domain is charged its fold's reach, since its copies leave the shape's
    /// own sphere.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static Vector4[] DrawnVolumes(CreationDocument document, float scale) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var shapes = (document.Shapes ?? []);
        var volumes = new List<Vector4>(capacity: shapes.Count);
        var transform = new CreationStampTransform(
            Origin: Vector3.Zero,
            ReflectionNormal: null,
            Rotation: Quaternion.Identity,
            Scale: scale
        );

        for (var index = 0; (index < shapes.Count); index++) {
            var blend = (shapes[index].Blend ?? SdfBlendOp.Union);

            if (
                SdfBoundAlgebra.IsIntersection(blend: blend) ||
                SdfBoundAlgebra.IsSubtraction(blend: blend)
            ) {
                continue;
            }

            var (center, radius) = CreationStampEmitter.ShapeStampBound(
                document: document,
                shapeIndex: index,
                transform: transform
            );

            volumes.Add(item: new Vector4(
                value: center,
                w: (radius + (ShapeDomainOps.Reach(domain: shapes[index].Domain) * scale))
            ));
        }

        return [.. volumes];
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

    /// <summary>Returns the nearest depth, along a view direction, at which any volume of the body begins: how far a near
    /// plane in front of <paramref name="eye"/> may stand before it cuts into the body anywhere in the view, not only
    /// on the sightline.</summary>
    /// <param name="eye">The eye, in world units.</param>
    /// <param name="forward">The unit view direction.</param>
    /// <returns>The depth, in world units, of the body's nearest volume in front of the eye, measured along
    /// <paramref name="forward"/> and bounded by each volume's sphere; positive infinity for a body with no volumes.
    /// A box counts as the sphere about its corners.</returns>
    public float NearestDepth(Vector3 eye, Vector3 forward) {
        var nearest = float.PositiveInfinity;
        var localEye = (Vector3.Transform(
            rotation: m_inverseOrientation,
            value: (eye - m_position)
        ) / m_scale);
        var localForward = Vector3.Normalize(value: Vector3.Transform(
            rotation: m_inverseOrientation,
            value: forward
        ));

        foreach (ref readonly var volume in m_collider.Volumes) {
            var radius = ((float)(double)volume.Radius);
            var center = volume.Center.ToVector3();

            nearest = (volume.Kind switch {
                FixedBodyColliderKind.Sphere => Depth(center: center, radius: radius),
                FixedBodyColliderKind.Capsule => MathF.Min(
                    x: Depth(center: center, radius: radius),
                    y: Depth(center: volume.Endpoint.ToVector3(), radius: radius)
                ),
                _ => Depth(center: center, radius: (radius + volume.HalfExtents.ToVector3().Length())),
            });
        }

        foreach (var sphere in m_drawn.Span) {
            nearest = Depth(
                center: new Vector3(
                    x: sphere.X,
                    y: sphere.Y,
                    z: sphere.Z
                ),
                radius: sphere.W
            );
        }

        return (nearest * m_scale);

        float Depth(Vector3 center, float radius) => MathF.Min(
            x: nearest,
            y: (Vector3.Dot(
                vector1: (center - localEye),
                vector2: localForward
            ) - radius)
        );
    }
    /// <summary>Returns how far along a ray a sphere must travel before it clears every volume of the body the ray
    /// passes through: the latest point at which the swept sphere still touches any of them.</summary>
    /// <param name="origin">The ray's origin, in world units; the chase target, which sits inside the body.</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <param name="radius">The radius of the sphere the eye keeps clear, in world units; at least zero.</param>
    /// <returns>The distance from <paramref name="origin"/>, in world units; zero when the ray meets no volume ahead of
    /// its origin, including for a body with no collider and no drawn shapes.</returns>
    public float Exit(Vector3 origin, Vector3 direction, float radius) {
        var volumes = m_collider.Volumes;
        var drawn = m_drawn.Span;

        if (
            volumes.IsEmpty &&
            drawn.IsEmpty
        ) {
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

        foreach (var sphere in drawn) {
            if (SphereInterval(
                center: new Vector3(
                    x: sphere.X,
                    y: sphere.Y,
                    z: sphere.Z
                ),
                direction: localDirection,
                enter: out _,
                exit: out var sphereExit,
                origin: localOrigin,
                radius: (localRadius + sphere.W)
            )) {
                exit = MathF.Max(
                    x: exit,
                    y: sphereExit
                );
            }
        }

        return (exit * m_scale);
    }
}
