using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE INTERVAL INTERPRETER's rules for the render reading's ops (SdfFieldEvaluator.RenderOps.cs): each encloses its
// point rule over a box rather than mirroring it step by step, since a hashed cell, a sector seam or a shell round can
// fall anywhere inside the box. A rule reads the point rule's own bound on how far it moves the point or the field.
public sealed partial class SdfFieldEvaluator {
    // The raws a transcendental endpoint is widened by: the tables' half-ULP envelope plus the products about it.
    private static readonly FixedQ4816 RenderBoundSlack = FixedQ4816.FromRawBits(value: 8L);

    // The render reading's point ops over a box (RenderWarpPoint's inclusion rules).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IntervalVector3 RenderWarpBounds(in CompiledInstruction instruction, IntervalVector3 local, ref FixedInterval distanceScale) {
        switch (instruction.Op) {
            case SdfOp.Shear: {
                    // The target moves by the cubic of the driver, taken over the driver's interval.
                    var target = (int)instruction.Shape;
                    var t = Component(axis: (int)instruction.Blend, box: local);
                    var offset = ((((Point(value: instruction.Data0Z) * t) + Point(value: instruction.Data0Y)) * t) + Point(value: instruction.Data0X)) * t;

                    return WithComponent(axis: target, box: local, value: (Component(axis: target, box: local) + offset));
                }
            case SdfOp.GaussianPush: {
                    // The weight lies in [0, 1], so each component moves by at most its push.
                    return Widen(box: local, reach: new FixedVector3(X: FixedQ4816.Abs(value: instruction.Data0W), Y: FixedQ4816.Abs(value: instruction.Data1W), Z: FixedQ4816.Abs(value: instruction.Aux)));
                }
            case SdfOp.DomainWarp: {
                    // Each sine lies in [-1, 1], so each component moves by at most the amplitude.
                    var amplitude = FixedQ4816.Abs(value: instruction.Data0W);

                    return Widen(box: local, reach: new FixedVector3(X: amplitude, Y: amplitude, Z: amplitude));
                }
            case SdfOp.RepeatPolar: {
                    // A turn and a reflection in the fold plane keep the radius: both folded coordinates lie within the
                    // largest radius the box's (u, v) rectangle reaches.
                    var (u, v) = PolarAxes(axis: instruction.Shape);
                    var turned = Spread(magnitude: FixedInterval.Magnitude(x: Component(axis: u, box: local), y: Component(axis: v, box: local)));

                    return WithComponents(box: local, u: u, uValue: turned, v: v, vValue: turned);
                }
            case SdfOp.WallpaperFold: {
                    return WallpaperBounds(instruction: in instruction, local: local);
                }
            case SdfOp.LogSphere: {
                    return LogSphereBounds(distanceScale: ref distanceScale, instruction: in instruction, local: local);
                }
            default: {
                    // The Repeat fold, then a displacement of at most half the jitter per axis, then a rotation, which
                    // keeps the length: every folded coordinate lies within the largest length the jittered box reaches.
                    var spacing = Vector(instruction: instruction);
                    var inverseSpacing = Vector1(instruction: instruction);
                    var jitter = (FixedQ4816.Abs(value: instruction.Data0W) * Half);
                    var folded = Widen(box: new IntervalVector3(
                        X: (local.X - (Point(value: spacing.X) * FixedInterval.Round(value: (local.X * Point(value: inverseSpacing.X))))),
                        Y: (local.Y - (Point(value: spacing.Y) * FixedInterval.Round(value: (local.Y * Point(value: inverseSpacing.Y))))),
                        Z: (local.Z - (Point(value: spacing.Z) * FixedInterval.Round(value: (local.Z * Point(value: inverseSpacing.Z)))))
                    ), reach: new FixedVector3(X: jitter, Y: jitter, Z: jitter));

                    if (instruction.Data1W <= FixedQ4816.Zero) {
                        return folded;
                    }

                    var tumbled = Spread(magnitude: folded.Length);

                    return new IntervalVector3(X: tumbled, Y: tumbled, Z: tumbled);
                }
        }
    }
    // The in-plane point reduced to its cell, where the reduction leaves it within the cell's extents about the nearest
    // lattice point, and then folded by isometries of the plane, which keep its length: both folded coordinates lie
    // within that length. A square group's limit leaves a point past the clamped cells unreduced, so the reduction is
    // taken over the clamped index; P4G's offset mirror can add half a cell.
    private static IntervalVector3 WallpaperBounds(in CompiledInstruction instruction, IntervalVector3 local) {
        var group = (SdfWallpaperGroup)instruction.Shape;
        var (axisA, axisB) = (((instruction.Blend == ((uint)Puck.SignedDistance.SdfPlane.YZ)) ? 1 : 0), ((instruction.Blend == ((uint)Puck.SignedDistance.SdfPlane.XY)) ? 1 : 2));
        var qa = Component(axis: axisA, box: local);
        var qb = Component(axis: axisB, box: local);
        FixedInterval folded;

        if (group >= SdfWallpaperGroup.P3) {
            // The nearest hex lattice point lies within a pitch of the point on each axis.
            var pitch = FixedQ4816.Abs(value: instruction.Data0X);

            folded = Spread(magnitude: FixedInterval.Magnitude(x: new FixedInterval(lower: -pitch, upper: pitch), y: new FixedInterval(lower: -pitch, upper: pitch)));
        } else {
            var cell = new FixedVector2(X: instruction.Data0X, Y: instruction.Data0Y);
            var inverseCell = new FixedVector2(X: instruction.Data0Z, Y: instruction.Data0W);
            var ra = (qa - (Point(value: cell.X) * FixedInterval.Clamp(maximum: instruction.Data1X, minimum: -instruction.Data1X, value: FixedInterval.Round(value: (qa * Point(value: inverseCell.X))))));
            var rb = (qb - (Point(value: cell.Y) * FixedInterval.Clamp(maximum: instruction.Data1Y, minimum: -instruction.Data1Y, value: FixedInterval.Round(value: (qb * Point(value: inverseCell.Y))))));
            var shift = ((group == SdfWallpaperGroup.P4G) ? FixedQ4816.Abs(value: cell.X) : FixedQ4816.Zero);

            folded = (Spread(magnitude: FixedInterval.Magnitude(x: ra, y: rb)) + new FixedInterval(lower: -shift, upper: shift));
        }

        return WithComponents(box: local, u: axisA, uValue: folded, v: axisB, vValue: folded);
    }
    // The radius folded to its shell: the shell index runs between the shells of the box's least and greatest radius,
    // the folded point lies within the prototype shell's outer radius, and the candidate scale runs over those shells.
    private static IntervalVector3 LogSphereBounds(in CompiledInstruction instruction, IntervalVector3 local, ref FixedInterval distanceScale) {
        var radius = local.Length;

        if (radius.IsUnbounded) {
            distanceScale = FixedInterval.Entire;

            return new IntervalVector3(X: FixedInterval.Entire, Y: FixedInterval.Entire, Z: FixedInterval.Entire);
        }

        var w = instruction.Data0X;
        var lowShell = FixedQ4816.Round(value: ((FixedQ4816.Log2(value: FixedQ4816.Max(x: radius.Lower, y: LogSphereMinRadius)) * Ln2) * instruction.Data0Z));
        var highShell = FixedQ4816.Round(value: ((FixedQ4816.Log2(value: FixedQ4816.Max(x: radius.Upper, y: LogSphereMinRadius)) * Ln2) * instruction.Data0Z));
        // A shell either side, since the rounding of the folded logarithm can land a point on a neighbour.
        var scales = new FixedInterval(
            lower: FixedQ4816.Max(x: FixedQ4816.Zero, y: (Exp(value: ((lowShell - FixedQ4816.One) * w)) - RenderBoundSlack)),
            upper: (Exp(value: ((highShell + FixedQ4816.One) * w)) + RenderBoundSlack)
        );
        var outer = (Exp(value: (w * Half)) + RenderBoundSlack);
        var folded = new FixedInterval(lower: -outer, upper: outer);

        distanceScale *= scales;

        return new IntervalVector3(X: folded, Y: folded, Z: folded);
    }
    // The render reading's field ops over a box: a relief of at most its amplitude either way.
    private static FixedInterval RenderEditFieldBounds(in CompiledInstruction instruction, FixedInterval result) {
        var amplitude = (FixedQ4816.Abs(value: ((instruction.Op == SdfOp.Displace) ? instruction.Data0W : instruction.Data0Y)) + RenderBoundSlack);

        return (result + new FixedInterval(lower: -amplitude, upper: amplitude));
    }
    // A LaneErode's effect on the next shape over a box: skipped outright, or eroded by any share of its reach.
    private static FixedInterval? LaneErodeBounds(in CompiledInstruction instruction) {
        var t = FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: ((FixedQ4816.Zero - instruction.Data0Y) / (instruction.Data0Z - instruction.Data0Y)));

        return ((t >= FixedQ4816.One) ? null : new FixedInterval(lower: FixedQ4816.Zero, upper: (FixedQ4816.Abs(value: instruction.Data1X) + RenderBoundSlack)));
    }
    // The interval a coordinate turned or reflected about the origin lies in: within the largest magnitude either way.
    private static FixedInterval Spread(FixedInterval magnitude) {
        if (magnitude.IsUnbounded) {
            return FixedInterval.Entire;
        }

        var reach = (magnitude.Upper + RenderBoundSlack);

        return new FixedInterval(lower: -reach, upper: reach);
    }
    private static IntervalVector3 Widen(IntervalVector3 box, FixedVector3 reach) => new(
        X: (box.X + new FixedInterval(lower: -reach.X, upper: reach.X)),
        Y: (box.Y + new FixedInterval(lower: -reach.Y, upper: reach.Y)),
        Z: (box.Z + new FixedInterval(lower: -reach.Z, upper: reach.Z))
    );
    private static IntervalVector3 WithComponent(IntervalVector3 box, int axis, FixedInterval value) => new(
        X: ((axis == 0) ? value : box.X),
        Y: ((axis == 1) ? value : box.Y),
        Z: ((axis == 2) ? value : box.Z)
    );
    private static IntervalVector3 WithComponents(IntervalVector3 box, int u, FixedInterval uValue, int v, FixedInterval vValue) => new(
        X: ((u == 0) ? uValue : ((v == 0) ? vValue : box.X)),
        Y: ((u == 1) ? uValue : ((v == 1) ? vValue : box.Y)),
        Z: ((u == 2) ? uValue : ((v == 2) ? vValue : box.Z))
    );
}
