using System.Numerics;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

namespace Puck.World.Server;

// A creation whose contact shapes do not meet as a plain union of primitives (a smooth or carving blend joins them, or a
// noise facet relieves them) collides as the surface its bake draws: one field collider per stamped copy, whose field
// is the creation's own fixed-point program (its contact shapes, their blends and any relief over their union, in one
// scope), read by the evaluator the field provider uses. A collider follows the drawn solid
// (docs/decisions/engine-design.md); its primitives' analytic colliders stand as the diagnostic's witnesses, and an
// unrelieved creation's exact union pieces answer for the field wherever its partition proves no carve reaches
// (FixedFieldPartition).
public sealed partial class WorldColliderSet {
    // The gradient taps' half-span: the field's default, or a sixteenth of the relief's finest wavelength when that is
    // shorter, and never under a 1024th of a metre.
    private const float ReliefGradientFloor = (1f / 1024f);

    /// <summary>Returns whether a creation carries relief: a noise facet of nonzero amplitude.</summary>
    /// <param name="document">The creation.</param>
    /// <returns><see langword="true"/> when the creation has a noise facet of nonzero amplitude.</returns>
    internal static bool Relieves(CreationDocument document) => CreationStampEmitter.HasRelief(document);
    /// <summary>Returns whether a creation's solid collides as its composed field rather than as its primitives: it
    /// carries relief, or a shape contact keeps joins the others by any blend but a hard union, whose surface the
    /// independent primitives do not draw.</summary>
    /// <param name="document">The creation.</param>
    /// <returns><see langword="true"/> when the creation is relieved or a contact shape blends other than by union.</returns>
    internal static bool CollidesAsField(CreationDocument document) {
        if (Relieves(document: document)) {
            return true;
        }

        foreach (var shape in (document.Shapes ?? [])) {
            if (CreationStampEmitter.EmitsContact(shape: shape) && ((shape.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union)) {
                return true;
            }
        }

        return false;
    }

    // One placement copy's analytic primitive colliders, the witnesses a relieved field projects, and the world box that
    // holds its relieved surface: each primitive's own drawn bound (never a witness collider's inset core), grown by the
    // relief's reach, which the composed field reads at most `growth` below its shapes' union. Within an exact primitive
    // that reading is within `growth` of it; a superellipsoid's field is its gauge times its least radius, so it grows by
    // 1 + growth over that radius.
    //
    // With `partitioned`, also the copy's FixedFieldPartition, whose invariant (FixedFieldPartition's remarks) rests on
    // what this walk establishes: every copy joins by hard union (a piece) or hard subtraction (a carve), any other blend
    // leaving the creation without a partition; each copy's reach is its drawn oriented bound grown by the margin its
    // contact primitive is dilated by (FixedFieldReach.Oriented pads it past rounding), so its own field reads above zero
    // outside it; and a piece's exact collider is the copy's own contact surface (ExactCollider). The caller asks for
    // none over relief, which displaces the union everywhere.
    private static List<FixedStaticCollider> PrimitiveColliders(CreationDocument document, in FixedCreationStampTransform transform, FixedQ4816 margin,
        FixedQ4816 growth, bool partitioned, out bool unbounded, out (FixedVector3 Center, FixedVector3 HalfExtent) bounds, out FixedFieldPartition? partition) {
        var colliders = new List<FixedStaticCollider>();
        var plane = false;
        var low = new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.MaxValue, Z: FixedQ4816.MaxValue);
        var high = new FixedVector3(X: FixedQ4816.MinValue, Y: FixedQ4816.MinValue, Z: FixedQ4816.MinValue);
        var reach = (growth + margin);
        var pad = new FixedVector3(X: FixedQ4816.Epsilon, Y: FixedQ4816.Epsilon, Z: FixedQ4816.Epsilon);
        var pieces = (partitioned ? new List<FixedFieldPiece>() : null);
        var carves = new List<FixedFieldReach>();
        var pieceGrowth = FixedQ4816.Max(x: margin, y: FixedQ4816.Zero);

        CreationStampEmitter.VisitFixedPrimitiveCopies(
            document: document,
            transform: transform,
            visitor: copy => {
                var witness = CopyCollider(copy: copy, margin: margin);

                colliders.Add(item: witness);

                if (pieces is not null) {
                    var copyReach = ((copy.Shape.Type == SdfSolidPrimitive.Plane)
                        ? FixedFieldReach.Unbounded(center: copy.Center)
                        : FixedFieldReach.Oriented(center: copy.Center, rotation: copy.Rotation,
                            halfExtent: (FixedVector3.Abs(value: copy.LocalHalfExtents) + new FixedVector3(X: pieceGrowth, Y: pieceGrowth, Z: pieceGrowth))));

                    switch (copy.Shape.Blend ?? SdfBlendOp.Union) {
                        case SdfBlendOp.Union: {
                                pieces.Add(item: new FixedFieldPiece(Exact: ExactCollider(collider: in witness, copy: in copy, margin: margin), Reach: copyReach));
                                break;
                            }
                        case SdfBlendOp.Subtraction: {
                                carves.Add(item: copyReach);
                                break;
                            }
                        default: {
                                pieces = null;
                                break;
                            }
                    }
                }

                if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
                    plane = true;
                    return;
                }

                FixedVector3 half;

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
        partition = ((pieces is null) ? null : new FixedFieldPartition(carves: carves, pieces: pieces));
        return colliders;
    }
    // A copy's analytic collider when its surface is the copy's own surface in the contact field: a sphere, a Box's
    // rounded or chamfered box, a round capsule and a half-space are the drawn primitive dilated by the margin, as the
    // field's margined primitive is. A cylinder is exact only undilated and with no rim inset, since a dilated cylinder's
    // rims round where its collider's stay sharp; an oriented-box stand-in, an inset core and a superellipsoid (whose
    // field dilates by its gauge, not by distance) only bound theirs. A negative margin erodes, which no collider here
    // mirrors.
    private static FixedStaticCollider? ExactCollider(in FixedStaticCollider collider, in FixedCreationStampPrimitiveCopy copy, FixedQ4816 margin) {
        if (margin < FixedQ4816.Zero) {
            return null;
        }

        var exact = collider.Kind switch {
            FixedStaticColliderKind.Sphere or FixedStaticColliderKind.RoundedBox or FixedStaticColliderKind.ChamferedBox or FixedStaticColliderKind.HalfSpace => true,
            FixedStaticColliderKind.Cylinder => ((margin == FixedQ4816.Zero) && (CylinderInset(copy: in copy) == default)),
            _ => false,
        };

        return (exact ? collider : null);
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
        IReadOnlyList<FixedStaticCollider> bases, FixedFieldPartition? partition) {
        var relieved = Relieves(document: document);
        // The finest octave's wavelength in world units: the creation's units scaled by the stamp; none without relief.
        var finest = (relieved ? MathF.Abs(x: document.Noise!.Frequency) : 0f);

        for (var octave = 1; (relieved && (octave < Math.Clamp(value: (document.Noise!.Octaves ?? 4), min: 1, max: SdfProgramBuilder.MaxNoiseOctaves))); octave++) {
            finest *= (document.Noise!.Lacunarity ?? 2f);
        }

        var wavelength = ((finest > 0f) ? (((float)transform.Scale) / finest) : float.PositiveInfinity);
        var step = Math.Max(val1: ReliefGradientFloor, val2: Math.Min(val1: ((float)FixedColliderField.DefaultGradientStep), val2: (wavelength / 16f)));

        var evaluator = new SdfFieldEvaluator(program: ReliefProgram(document: document, margin: margin, relieved: relieved, transform: transform));

        return new FixedColliderField(
            field: evaluator,
            gradientStep: FixedQ4816.FromDouble(value: step),
            bounded: bounded
        ) {
            Bases = bases,
            Bounds = new SdfBoundsCache(field: evaluator),
            Partition = partition,
        };
    }
    // A creation copy's contact shapes in one scope, with its relief over their union or without it.
    private static SdfProgram ReliefProgram(CreationDocument document, in FixedCreationStampTransform transform, float margin, bool relieved) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.EmitContact(builder, document, transform, material, margin, applyRelief: relieved);
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
        var relieved = Relieves(document: document);
        var growth = ((relieved ? FixedQ4816.FromDouble(value: MathF.Abs(x: document.Noise!.Amplitude)) : FixedQ4816.Zero) + BlendGrowth(program: envelope));
        var primitives = PrimitiveColliders(bounds: out var box, document: document, growth: growth, margin: fixedMargin, partition: out var partition,
            partitioned: !relieved, transform: transform, unbounded: out var unbounded);
        var (center, half) = (unbounded ? (transform.Origin, FixedVector3.Zero) : box);
        var local = (transform with { Origin = (transform.Origin - center) });
        var field = ReliefField(bases: primitives, bounded: !unbounded, document: document, margin: margin, partition: partition, transform: local);

        return FixedStaticCollider.FieldSurface(center: center, rotation: FixedQuaternion.Identity, halfExtent: half, field: field);
    }
}
