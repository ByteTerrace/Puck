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
// (docs/decisions/engine-design.md); its primitives' analytic colliders stand only as the diagnostic's witnesses.
public sealed partial class WorldColliderSet {
    // The gradient taps' half-span: the field's default, or a sixteenth of the relief's finest wavelength when that is
    // shorter, and never under a 1024th of a metre.
    private const float ReliefGradientFloor = (1f / 1024f);

    /// <summary>Returns whether a creation's solid collides as a field: it carries relief the analytic primitives do not
    /// draw.</summary>
    /// <param name="document">The creation.</param>
    /// <returns><see langword="true"/> when the creation has a noise facet of nonzero amplitude.</returns>
    internal static bool Relieves(CreationDocument document) => (document.Noise is { } noise) && (noise.Amplitude != 0f);

    // One placement copy's analytic primitive colliders, the witnesses a relieved field projects, and the world box that
    // holds its relieved surface: each primitive's own drawn bound (never a witness collider's inset core), grown by the
    // relief's reach, which the composed field reads at most `growth` below its shapes' union. Within an exact primitive
    // that reading is within `growth` of it; a superellipsoid's field is its gauge times its least radius, so it grows by
    // 1 + growth over that radius.
    private static List<FixedStaticCollider> PrimitiveColliders(CreationDocument document, in FixedCreationStampTransform transform, FixedQ4816 margin,
        FixedQ4816 growth, out bool unbounded, out (FixedVector3 Center, FixedVector3 HalfExtent) bounds) {
        var colliders = new List<FixedStaticCollider>();
        var plane = false;
        var low = new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.MaxValue, Z: FixedQ4816.MaxValue);
        var high = new FixedVector3(X: FixedQ4816.MinValue, Y: FixedQ4816.MinValue, Z: FixedQ4816.MinValue);
        var reach = (growth + margin);
        var pad = new FixedVector3(X: FixedQ4816.Epsilon, Y: FixedQ4816.Epsilon, Z: FixedQ4816.Epsilon);

        CreationStampEmitter.VisitFixedPrimitiveCopies(
            document: document,
            transform: transform,
            visitor: copy => {
                if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
                    plane = true;
                    colliders.Add(item: FixedStaticCollider.HalfSpace(point: (copy.Center + (copy.PlaneNormal * margin)), normal: copy.PlaneNormal));
                    return;
                }

                var witness = (((copy.Shape.Type == SdfSolidPrimitive.Sphere) && (copy.UniformScale > FixedQ4816.Zero))
                    ? FixedStaticCollider.Sphere(center: copy.Center,
                        radius: ((FixedQ4816.FromDouble(value: SdfSolidGeometry.GetLocalBounds(type: SdfSolidPrimitive.Sphere).HalfExtents.X) * copy.UniformScale) + margin))
                    : PlacementFinite(copy: copy, margin: margin));
                FixedVector3 half;

                colliders.Add(item: witness);

                if (witness.Kind == FixedStaticColliderKind.Superellipsoid) {
                    // The witness is the drawn superellipsoid itself, uninset; a convex solid's support along each world
                    // axis is its exact world box.
                    var least = FixedQ4816.Max(x: FixedQ4816.Epsilon, y: FixedQ4816.Min(x: witness.Extent.X, y: FixedQ4816.Min(x: witness.Extent.Y, y: witness.Extent.Z)));
                    var grown = (witness with { Extent = (witness.Extent * (FixedQ4816.One + (reach / least))), Radius = FixedQ4816.Zero });

                    half = (new FixedVector3(
                        X: (grown.SupportPoint(direction: FixedVector3.UnitX).X - grown.Center.X),
                        Y: (grown.SupportPoint(direction: FixedVector3.UnitY).Y - grown.Center.Y),
                        Z: (grown.SupportPoint(direction: FixedVector3.UnitZ).Z - grown.Center.Z)
                    ) + pad);
                } else {
                    half = ((copy.HalfExtents + new FixedVector3(X: reach, Y: reach, Z: reach)) + pad);
                }

                low = -FixedVector3.Max(left: -low, right: (half - copy.Center));
                high = FixedVector3.Max(left: high, right: (copy.Center + half));
            }
        );

        unbounded = plane;
        bounds = (((low + high) / FixedQ4816.FromInteger(value: 2)), ((high - low) / FixedQ4816.FromInteger(value: 2)));
        return colliders;
    }
    // How far below the least of its shapes' readings a composed program can read: each blend's growth past its operands
    // (SdfBoundAlgebra.Growth), summed down the stream, since each compose sags from the least of what it joins.
    private static FixedQ4816 BlendGrowth(SdfProgram program) {
        var growth = 0.0;

        foreach (var instruction in program.Instructions) {
            if (((instruction.Op == SdfOp.ShapeBlend) && !instruction.Detail) || (instruction.Op == SdfOp.PopField)) {
                growth += SdfBoundAlgebra.Growth(blend: ((SdfBlendOp)instruction.Blend), radius: instruction.Data1.X);
            }
        }

        return (FixedDirectedRounding.TryCeilingFromDouble(result: out var ceiling, value: growth) ? ceiling : FixedQ4816.MaxValue);
    }
    // The relieved program of one copy, its frame origin at offset from the copy's stamp origin, read as contact reads it;
    // its evaluator's own box bounds prove a body clear, an identical box reusing its answer (a body at rest asks the same
    // box every tick).
    private static FixedColliderField ReliefField(CreationDocument document, in FixedCreationStampTransform transform, float margin, bool bounded,
        IReadOnlyList<FixedStaticCollider> bases) {
        var noise = document.Noise!;
        // The finest octave's wavelength in world units: the creation's units scaled by the stamp.
        var finest = MathF.Abs(x: noise.Frequency);

        for (var octave = 1; (octave < Math.Clamp(value: (noise.Octaves ?? 4), min: 1, max: SdfProgramBuilder.MaxNoiseOctaves)); octave++) {
            finest *= (noise.Lacunarity ?? 2f);
        }

        var wavelength = ((finest > 0f) ? (((float)transform.Scale) / finest) : float.PositiveInfinity);
        var step = Math.Max(val1: ReliefGradientFloor, val2: Math.Min(val1: ((float)FixedColliderField.DefaultGradientStep), val2: (wavelength / 16f)));

        var evaluator = new SdfFieldEvaluator(program: ReliefProgram(document: document, margin: margin, relieved: true, transform: transform));

        return new FixedColliderField(
            field: evaluator,
            gradientStep: FixedQ4816.FromDouble(value: step),
            bounded: bounded
        ) {
            Bases = bases,
            Bounds = new SdfBoundsCache(field: evaluator),
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
    // One static copy's field collider: its frame is the world box's centre, unturned, so the program carries the stamp's
    // own translation less that centre, and its rotation. Null for a copy contact keeps no shape of.
    private static FixedStaticCollider? ReliefCollider(CreationDocument document, FixedCreationStampTransform transform, float margin) {
        if (!CreationStampEmitter.EmitsContact(document: document)) {
            return null;
        }

        var fixedMargin = FixedQ4816.FromDouble(value: margin);
        // The unrelieved program, at the stamp's own origin: its blends bound how far the relief's union reaches.
        var envelope = ReliefProgram(document: document, margin: margin, relieved: false, transform: transform);
        var growth = (FixedQ4816.FromDouble(value: MathF.Abs(x: document.Noise!.Amplitude)) + BlendGrowth(program: envelope));
        var primitives = PrimitiveColliders(bounds: out var box, document: document, growth: growth, margin: fixedMargin, transform: transform,
            unbounded: out var unbounded);
        var (center, half) = (unbounded ? (transform.Origin, FixedVector3.Zero) : box);
        var local = (transform with { Origin = (transform.Origin - center) });
        var field = ReliefField(bases: primitives, bounded: !unbounded, document: document, margin: margin, transform: local);

        return FixedStaticCollider.FieldSurface(center: center, rotation: FixedQuaternion.Identity, halfExtent: half, field: field);
    }
}
