using System.Numerics;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

namespace Puck.World.Server;

// A relieved creation (one carrying a noise facet) collides as the surface its bake draws: one field collider per
// stamped copy, whose field is the creation's own fixed-point program (its contact shapes and the relief over their
// union, in one scope), read by the evaluator the field provider uses. A collider follows the drawn solid
// (docs/decisions/engine-design.md); its primitives' analytic colliders stand only as its bounds and the diagnostic's
// witnesses.
public sealed partial class WorldColliderSet {
    // The gradient taps' half-span: a thirty-second of a metre, or a sixteenth of the relief's finest wavelength when that
    // is shorter, and never under a 1024th of a metre.
    private const float ReliefGradientStep = (1f / 32f);
    private const float ReliefGradientFloor = (1f / 1024f);

    /// <summary>Returns whether a creation's solid collides as a field: it carries relief the analytic primitives do not
    /// draw.</summary>
    /// <param name="document">The creation.</param>
    /// <returns><see langword="true"/> when the creation has a noise facet of nonzero amplitude.</returns>
    internal static bool Relieves(CreationDocument document) => (document.Noise is { } noise) && (noise.Amplitude != 0f);

    // One placement copy's analytic primitive colliders: the witnesses a relieved field projects, and its bounds.
    private static List<FixedStaticCollider> PrimitiveColliders(CreationDocument document, in FixedCreationStampTransform transform, FixedQ4816 margin, out bool unbounded) {
        var colliders = new List<FixedStaticCollider>();
        var plane = false;

        CreationStampEmitter.VisitFixedPrimitiveCopies(
            document: document,
            transform: transform,
            visitor: copy => {
                if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
                    plane = true;
                    colliders.Add(item: FixedStaticCollider.HalfSpace(point: (copy.Center + (copy.PlaneNormal * margin)), normal: copy.PlaneNormal));
                } else if ((copy.Shape.Type == SdfSolidPrimitive.Sphere) && (copy.UniformScale > FixedQ4816.Zero)) {
                    colliders.Add(item: FixedStaticCollider.Sphere(center: copy.Center,
                        radius: ((FixedQ4816.FromDouble(value: SdfSolidGeometry.GetLocalBounds(type: SdfSolidPrimitive.Sphere).HalfExtents.X) * copy.UniformScale) + margin)));
                } else {
                    colliders.Add(item: PlacementFinite(copy: copy, margin: margin));
                }
            }
        );
        unbounded = plane;
        return colliders;
    }
    // The world box that holds a relieved copy's surface. The relief moves the surface to where the union's field reads
    // its negation, at most the amplitude: within the amplitude of an exact primitive, and within a superellipsoid's
    // gauge scaled by 1 + amplitude over its least radius, since its field is that gauge times its least radius.
    private static (FixedVector3 Center, FixedVector3 HalfExtent) ReliefBounds(List<FixedStaticCollider> primitives, FixedQ4816 amplitude) {
        var low = new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.MaxValue, Z: FixedQ4816.MaxValue);
        var high = new FixedVector3(X: FixedQ4816.MinValue, Y: FixedQ4816.MinValue, Z: FixedQ4816.MinValue);

        foreach (var primitive in primitives) {
            FixedVector3 half;

            if (primitive.Kind == FixedStaticColliderKind.Superellipsoid) {
                var least = FixedQ4816.Min(x: primitive.Extent.X, y: FixedQ4816.Min(x: primitive.Extent.Y, y: primitive.Extent.Z));
                var grown = (primitive with { Extent = (primitive.Extent * (FixedQ4816.One + (amplitude / FixedQ4816.Max(x: least, y: FixedQ4816.Epsilon)))) });

                // A convex solid's support along each world axis is its exact world box.
                half = new FixedVector3(
                    X: (grown.SupportPoint(direction: FixedVector3.UnitX).X - grown.Center.X),
                    Y: (grown.SupportPoint(direction: FixedVector3.UnitY).Y - grown.Center.Y),
                    Z: (grown.SupportPoint(direction: FixedVector3.UnitZ).Z - grown.Center.Z)
                ) + new FixedVector3(X: FixedQ4816.Epsilon, Y: FixedQ4816.Epsilon, Z: FixedQ4816.Epsilon);
            } else {
                half = (primitive.WorldHalfExtent()!.Value + new FixedVector3(X: amplitude, Y: amplitude, Z: amplitude));
            }

            low = new FixedVector3(X: FixedQ4816.Min(x: low.X, y: (primitive.Center.X - half.X)), Y: FixedQ4816.Min(x: low.Y, y: (primitive.Center.Y - half.Y)),
                Z: FixedQ4816.Min(x: low.Z, y: (primitive.Center.Z - half.Z)));
            high = FixedVector3.Max(left: high, right: (primitive.Center + half));
        }

        var two = FixedQ4816.FromInteger(value: 2);

        return (((low + high) / two), ((high - low) / two));
    }
    // The relieved program of one copy, its frame origin at offset from the copy's stamp origin, read as contact reads it.
    private static FixedColliderField ReliefField(CreationDocument document, in FixedCreationStampTransform transform, float margin, bool bounded, IReadOnlyList<FixedStaticCollider> bases) {
        var noise = document.Noise!;
        var program = ReliefProgram(document: document, margin: margin, relieved: true, transform: transform);
        // The shapes without their relief bound it: the surface lies where their union reads at most the amplitude.
        var envelope = ReliefProgram(document: document, margin: margin, relieved: false, transform: transform);
        var envelopeScale = BoundScale(program: envelope);
        // The finest octave's wavelength in world units: the creation's units scaled by the stamp.
        var finest = MathF.Abs(x: noise.Frequency);

        for (var octave = 1; (octave < Math.Clamp(value: (noise.Octaves ?? 4), min: 1, max: SdfProgramBuilder.MaxNoiseOctaves)); octave++) {
            finest *= (noise.Lacunarity ?? 2f);
        }

        var wavelength = ((finest > 0f) ? (((float)transform.Scale) / finest) : float.PositiveInfinity);
        var step = Math.Max(val1: ReliefGradientFloor, val2: Math.Min(val1: ReliefGradientStep, val2: (wavelength / 16f)));

        return new FixedColliderField(
            field: new SdfFieldEvaluator(program: program),
            boundScale: BoundScale(program: program),
            gradientStep: FixedQ4816.FromDouble(value: step),
            bounded: bounded
        ) {
            Bases = bases,
            Envelope = new SdfFieldEvaluator(program: envelope),
            EnvelopeScale = envelopeScale,
            EnvelopeGrowth = (envelopeScale * FixedQ4816.FromDouble(value: MathF.Abs(x: noise.Amplitude))),
        };
    }
    // A creation copy's contact shapes in one scope, with its relief over their union or without it.
    private static SdfProgram ReliefProgram(CreationDocument document, in FixedCreationStampTransform transform, float margin, bool relieved) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.PushField(compose: SdfBlendOp.Union);
        CreationStampEmitter.EmitFixed(builder: builder, document: document, transform: transform, materialFor: _ => material,
            contactMargin: margin);
        if (relieved) {
            CreationStampEmitter.EmitNoise(builder: builder, noise: document.Noise!, transform: transform);
        }
        _ = builder.PopField();
        WorldBootWork.Count(kind: WorldBootWork.ShapeBuilds);
        return builder.Build();
    }
    // The program's step scale floored onto the fixed grid, so the scaled field never reads above the distance.
    private static FixedQ4816 BoundScale(SdfProgram program) =>
        (FixedDirectedRounding.TryFloorFromDouble(result: out var floored, value: program.StepScale)
            ? FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Epsilon, value: floored)
            : FixedQ4816.Epsilon);
    // One static copy's field collider: its frame is the world box's centre, unturned, so the program carries the stamp's
    // own translation less that centre, and its rotation.
    private static FixedStaticCollider ReliefCollider(CreationDocument document, FixedCreationStampTransform transform, float margin) {
        var fixedMargin = FixedQ4816.FromDouble(value: margin);
        var primitives = PrimitiveColliders(document: document, transform: transform, margin: fixedMargin, unbounded: out var unbounded);
        var (center, half) = (unbounded
            ? (transform.Origin, FixedVector3.Zero)
            : ReliefBounds(primitives: primitives, amplitude: FixedQ4816.FromDouble(value: MathF.Abs(x: document.Noise!.Amplitude))));
        var field = ReliefField(document: document, transform: (transform with { Origin = (transform.Origin - center) }), margin: margin,
            bounded: !unbounded, bases: primitives);

        return FixedStaticCollider.FieldSurface(center: center, rotation: FixedQuaternion.Identity, halfExtent: half, field: field);
    }
}
