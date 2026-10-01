using Puck.SignedDistance;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>Counted work of one or more point evaluations.</summary>
public struct SdfStudyWork {
    /// <summary>The segments entered (not masked out).</summary>
    public long Segments;
    /// <summary>The segments a bounding sphere skipped per sample.</summary>
    public long SegmentSkips;
    /// <summary>The instructions dispatched.</summary>
    public long Ops;
    /// <summary>The primitive evaluations.</summary>
    public long Shapes;
    /// <summary>The shapes a per-shape bounding sphere skipped per sample.</summary>
    public long ShapeSkips;
    /// <summary>The evaluated shapes whose compose changed the accumulator (a hard blend's loser leaves it unchanged,
    /// so only these need a gradient in a forward-mode walk that selects the winner's).</summary>
    public long Influential;
    /// <summary>The evaluated shapes whose gradient the kernels take by a shape-local four-tap difference.</summary>
    public long TailShapes;
    /// <summary>The evaluated four-tap-gradient shapes whose compose changed the accumulator.</summary>
    public long TailInfluential;

    /// <summary>Adds another tally.</summary>
    /// <param name="other">The other tally.</param>
    public void Add(in SdfStudyWork other) {
        Segments += other.Segments;
        SegmentSkips += other.SegmentSkips;
        Ops += other.Ops;
        Shapes += other.Shapes;
        ShapeSkips += other.ShapeSkips;
        Influential += other.Influential;
        TailShapes += other.TailShapes;
        TailInfluential += other.TailInfluential;
    }
}
/// <summary>How a pruned tape rewrites a live compose whose earlier accumulator was pruned away: the accumulator is
/// then still its seed, so an intersection-like compose must become a copy of the candidate and a subtraction-like
/// compose a copy of its negation (Fidget rewrites a decided min/max into a copy the same way).</summary>
public enum SdfStudyRewrite : byte {
    /// <summary>The compose runs as authored.</summary>
    None = 0,
    /// <summary>The compose writes the candidate.</summary>
    Copy = 1,
    /// <summary>The compose writes the negated candidate.</summary>
    CopyNegated = 2,
}
/// <summary>A double-precision transcription of <c>mapCore</c>'s distance walk over a <see cref="SdfStudyTape"/>:
/// the masked segment merge, the per-segment and per-shape bounding-sphere skips, the transform chain, field scopes,
/// field ops and the blend tail. Materials are not tracked. Rigid-leaf plans and part programs are walked as their
/// authored instructions with the per-shape spheres standing in for the leaf spheres.</summary>
public static class SdfStudyPoint {
    /// <summary>Returns whether every op and shape on <paramref name="tape"/> has a faithful transcription here.
    /// A wallpaper fold evaluates its lattice repeat only (not its in-cell group symmetry), and is reported unfaithful.</summary>
    /// <param name="tape">The tape.</param>
    /// <param name="first">The first unfaithful op or shape, or empty.</param>
    /// <returns>Whether the transcription is faithful.</returns>
    public static bool IsFaithful(SdfStudyTape tape, out string first) {
        foreach (var instruction in tape.Instructions) {
            var supported = instruction.Op switch {
                SdfOp.ShapeBlend => SdfStudyShapes.Supports(shape: ((SdfShapeType)instruction.Shape)),
                SdfOp.ResetPoint or SdfOp.Translate or SdfOp.Rotate or SdfOp.Scale or SdfOp.TransformDynamic or SdfOp.SymmetryPlane
                    or SdfOp.Repeat or SdfOp.RepeatLimited or SdfOp.RepeatPolar or SdfOp.Elongate or SdfOp.Onion or SdfOp.Dilate
                    or SdfOp.Displace or SdfOp.NoiseDisplace or SdfOp.CellDisplace or SdfOp.PushField or SdfOp.PopField => true,
                _ => false,
            };

            if (!supported) {
                first = ((instruction.Op == SdfOp.ShapeBlend) ? ((SdfShapeType)instruction.Shape).ToString() : instruction.Op.ToString());

                return false;
            }
        }
        first = string.Empty;

        return true;
    }
    /// <summary>Evaluates the field at a world point.</summary>
    /// <param name="tape">The tape.</param>
    /// <param name="segments">The segments to walk, ascending: the world segments and the visible instances'.</param>
    /// <param name="p">The world point.</param>
    /// <param name="live">The pruned tape's live instructions, or <see langword="null"/> for every instruction.</param>
    /// <param name="rewrites">The pruned tape's compose rewrites, or <see langword="null"/>.</param>
    /// <param name="sphereSkips">Whether the per-sample segment and shape bounding-sphere skips run.</param>
    /// <param name="work">The tally the walk adds to.</param>
    /// <returns>The field value after the program's step scale.</returns>
    public static double Evaluate(SdfStudyTape tape, ReadOnlySpan<int> segments, V3 p, bool[]? live, SdfStudyRewrite[]? rewrites, bool sphereSkips, ref SdfStudyWork work) {
        const double Far = SdfStudyShapes.FarDistance;
        var acc = Far;
        var saved = Far;
        var local = p;
        var scale = 1.0;

        foreach (var segmentIndex in segments) {
            var segment = tape.Segments[segmentIndex];

            work.Segments++;
            if (sphereSkips && (segment.Bound.Mode != SdfProgram.BoundModeNone) && (acc <= Far)) {
                var toCenter = (p - tape.CenterOf(bound: segment.Bound));
                var clearance = Math.Max(val1: (acc + segment.Bound.Radius), val2: 0.0);

                if (V3.Dot(a: toCenter, b: toCenter) >= (clearance * clearance)) {
                    work.SegmentSkips++;

                    continue;
                }
            }
            if (tape.RigidPlans[segmentIndex] is { Leaves.Length: > 0 } plan) {
                acc = Rigid(acc: acc, live: live, p: p, plan: plan, rewrites: rewrites, sphereSkips: sphereSkips, tape: tape, work: ref work);

                continue;
            }
            for (var index = segment.First; (index < segment.End); index++) {
                if ((live is not null) && !live[index]) {
                    continue;
                }

                var ins = tape.Instructions[index];

                work.Ops++;
                switch (ins.Op) {
                    case SdfOp.ResetPoint:
                        local = p;
                        scale = 1.0;
                        break;
                    case SdfOp.Translate:
                        local -= new V3(X: ins.Data0.X, Y: ins.Data0.Y, Z: ins.Data0.Z);
                        break;
                    case SdfOp.Rotate:
                        local = SdfStudyShapes.RotateInverse(p: local, q: ins.Data0);
                        break;
                    case SdfOp.Scale:
                        local = new V3(X: (local.X / ins.Data0.X), Y: (local.Y / ins.Data0.Y), Z: (local.Z / ins.Data0.Z));
                        scale *= ins.Data0.W;
                        break;
                    case SdfOp.TransformDynamic: {
                            var transform = tape.Transforms[((int)ins.Data0.X)];

                            local = SdfStudyShapes.RotateInverse(
                                p: (local - new V3(X: transform.Position.X, Y: transform.Position.Y, Z: transform.Position.Z)),
                                q: (transform.Orientation.X, transform.Orientation.Y, transform.Orientation.Z, transform.Orientation.W)
                            );
                            break;
                        }
                    case SdfOp.SymmetryPlane: {
                            var normal = new V3(X: ins.Data0.X, Y: ins.Data0.Y, Z: ins.Data0.Z);
                            var side = (V3.Dot(a: local, b: normal) + ins.Data0.W);

                            local -= (normal * (2.0 * Math.Min(val1: side, val2: 0.0)));
                            break;
                        }
                    case SdfOp.Repeat:
                        local -= new V3(
                            X: (ins.Data0.X * Round(x: (local.X * ins.Data1.X))),
                            Y: (ins.Data0.Y * Round(x: (local.Y * ins.Data1.Y))),
                            Z: (ins.Data0.Z * Round(x: (local.Z * ins.Data1.Z)))
                        );
                        break;
                    case SdfOp.RepeatLimited:
                        local -= new V3(
                            X: (ins.Data0.X * Math.Clamp(value: Round(x: (local.X / ins.Data0.X)), min: -ins.Data1.X, max: ins.Data1.X)),
                            Y: (ins.Data0.Y * Math.Clamp(value: Round(x: (local.Y / ins.Data0.Y)), min: -ins.Data1.Y, max: ins.Data1.Y)),
                            Z: (ins.Data0.Z * Math.Clamp(value: Round(x: (local.Z / ins.Data0.Z)), min: -ins.Data1.Z, max: ins.Data1.Z))
                        );
                        break;
                    case SdfOp.RepeatPolar:
                        local = PolarFold(ins: ins, p: local);
                        break;
                    case SdfOp.Elongate:
                        local -= new V3(
                            X: Math.Clamp(value: local.X, min: -ins.Data0.X, max: ins.Data0.X),
                            Y: Math.Clamp(value: local.Y, min: -ins.Data0.Y, max: ins.Data0.Y),
                            Z: Math.Clamp(value: local.Z, min: -ins.Data0.Z, max: ins.Data0.Z)
                        );
                        break;
                    case SdfOp.WallpaperFold:
                        local = WallpaperLattice(ins: ins, p: local);
                        break;
                    case SdfOp.Onion:
                        acc = (Math.Abs(value: acc) - ins.Data0.X);
                        break;
                    case SdfOp.Dilate:
                        acc -= ins.Data0.X;
                        break;
                    case SdfOp.Displace:
                        acc += (ins.Data0.W * ((Math.Sin(a: (ins.Data0.X * local.X)) * Math.Sin(a: (ins.Data0.Y * local.Y))) * Math.Sin(a: (ins.Data0.Z * local.Z))));
                        break;
                    case SdfOp.NoiseDisplace:
                        acc += ((ins.Data0.Y * ins.Data1.X) * SdfStudyShapes.NoiseSum(q: (local * ins.Data0.X), seed: ins.Shape, octaves: ins.Blend, gain: ins.Data0.Z, lacunarity: ins.Data0.W));
                        break;
                    case SdfOp.CellDisplace:
                        acc += (ins.Data0.Y * (SdfStudyShapes.CellDistance(q: (local * ins.Data0.X), seed: ins.Shape, mode: ins.Blend, randomness: ins.Data0.Z) - 0.5));
                        break;
                    case SdfOp.PushField:
                        saved = acc;
                        acc = Far;
                        break;
                    case SdfOp.PopField: {
                            var candidate = acc;

                            if (ins.Data1.Y > 0.0) {
                                candidate *= ins.Data1.Y;
                            }
                            acc = Compose(current: saved, candidate: candidate, blend: ((SdfBlendOp)ins.Blend), smooth: ins.Data1.X, rewrite: Rewrite(index: index, rewrites: rewrites));
                            break;
                        }
                    case SdfOp.ShapeBlend: {
                            var shapeBound = tape.ShapeBounds[index];

                            if (sphereSkips && (shapeBound.Mode != SdfProgram.BoundModeNone) && (acc <= Far)) {
                                var toCenter = (p - tape.CenterOf(bound: shapeBound));
                                var clearance = Math.Max(val1: (acc + shapeBound.Radius), val2: 0.0);

                                if (V3.Dot(a: toCenter, b: toCenter) >= (clearance * clearance)) {
                                    work.ShapeSkips++;

                                    break;
                                }
                            }
                            work.Shapes++;

                            var candidate = (SdfStudyShapes.Evaluate(shape: ((SdfShapeType)ins.Shape), p: local, d0: ins.Data0, d1: ins.Data1) * scale);
                            var before = acc;
                            var tail = !SdfStudyShapes.HasAnalyticGradient(shape: ((SdfShapeType)ins.Shape));

                            acc = Compose(current: acc, candidate: candidate, blend: ((SdfBlendOp)ins.Blend), smooth: ins.Data1.X, rewrite: Rewrite(index: index, rewrites: rewrites));
                            work.Influential += ((acc != before) ? 1 : 0);
                            work.TailShapes += (tail ? 1 : 0);
                            work.TailInfluential += ((tail && (acc != before)) ? 1 : 0);
                            break;
                        }
                    default:
                        throw new NotSupportedException(message: $"op {ins.Op}");
                }
            }
        }

        return (acc * tape.StepScale);
    }

    // mapCore's rigid-leaf walk: each leaf posed from the chain's base frame, rejected by its tight sphere first. A
    // pruned tape drops the leaves of dead shapes.
    private static double Rigid(SdfStudyTape tape, SdfStudyTape.RigidPlan plan, V3 p, double acc, bool[]? live, SdfStudyRewrite[]? rewrites, bool sphereSkips, ref SdfStudyWork work) {
        var basePosition = p;

        if (plan.DynamicSlot >= 0) {
            var transform = tape.Transforms[plan.DynamicSlot];

            basePosition = SdfStudyShapes.RotateInverse(
                p: (p - new V3(X: transform.Position.X, Y: transform.Position.Y, Z: transform.Position.Z)),
                q: (transform.Orientation.X, transform.Orientation.Y, transform.Orientation.Z, transform.Orientation.W)
            );
        }
        foreach (var leaf in plan.Leaves) {
            if ((live is not null) && !live[leaf.Shape]) {
                continue;
            }
            if (sphereSkips && (leaf.Bound.Radius >= 0.0) && (acc <= SdfStudyShapes.FarDistance)) {
                var toCenter = (basePosition - leaf.Bound.Center);
                var clearance = Math.Max(val1: (acc + leaf.Bound.Radius), val2: 0.0);

                if (V3.Dot(a: toCenter, b: toCenter) >= (clearance * clearance)) {
                    work.ShapeSkips++;

                    continue;
                }
            }

            var leafBase = basePosition;

            if (leaf.Fold is { } fold) {
                leafBase -= fold.Position;
                if (fold.Rotation is { } prefix) {
                    leafBase = SdfStudyShapes.RotateInverse(p: leafBase, q: prefix);
                }
                for (var step = 0; (step < fold.Count); step++) {
                    leafBase = FoldStep(p: leafBase, ins: tape.Instructions[(fold.First + step)]);
                }
            }

            var position = (leafBase - leaf.Position);

            if (leaf.Rotation is { } rotation) {
                position = SdfStudyShapes.RotateInverse(p: position, q: rotation);
            }

            var ins = tape.Instructions[leaf.Shape];
            var candidate = SdfStudyShapes.Evaluate(shape: ((SdfShapeType)ins.Shape), p: position, d0: ins.Data0, d1: ins.Data1);
            var before = acc;
            var tail = !SdfStudyShapes.HasAnalyticGradient(shape: ((SdfShapeType)ins.Shape));

            work.Ops++;
            work.Shapes++;
            acc = Compose(current: acc, candidate: candidate, blend: ((SdfBlendOp)ins.Blend), smooth: ins.Data1.X, rewrite: Rewrite(rewrites, leaf.Shape));
            work.Influential += ((acc != before) ? 1 : 0);
            work.TailShapes += (tail ? 1 : 0);
            work.TailInfluential += ((tail && (acc != before)) ? 1 : 0);
        }

        return acc;
    }
    private static V3 FoldStep(V3 p, in SdfStudyTape.Instruction ins) {
        switch (ins.Op) {
            case SdfOp.SymmetryPlane: {
                    var normal = new V3(X: ins.Data0.X, Y: ins.Data0.Y, Z: ins.Data0.Z);

                    return (p - (normal * (2.0 * Math.Min(val1: (V3.Dot(a: p, b: normal) + ins.Data0.W), val2: 0.0))));
                }
            case SdfOp.Repeat:
                return (p - new V3(X: (ins.Data0.X * Round(x: (p.X * ins.Data1.X))), Y: (ins.Data0.Y * Round(x: (p.Y * ins.Data1.Y))), Z: (ins.Data0.Z * Round(x: (p.Z * ins.Data1.Z)))));
            case SdfOp.RepeatLimited:
                return (p - new V3(
                    X: (ins.Data0.X * Math.Clamp(value: Round(x: (p.X / ins.Data0.X)), min: -ins.Data1.X, max: ins.Data1.X)),
                    Y: (ins.Data0.Y * Math.Clamp(value: Round(x: (p.Y / ins.Data0.Y)), min: -ins.Data1.Y, max: ins.Data1.Y)),
                    Z: (ins.Data0.Z * Math.Clamp(value: Round(x: (p.Z / ins.Data0.Z)), min: -ins.Data1.Z, max: ins.Data1.Z))
                ));
            case SdfOp.Translate:
                return (p - new V3(X: ins.Data0.X, Y: ins.Data0.Y, Z: ins.Data0.Z));
            case SdfOp.Rotate:
                return SdfStudyShapes.RotateInverse(p: p, q: ins.Data0);
            default:
                return p;
        }
    }

    /// <summary>Rounds to the nearest integer, ties to even, as DXIL's <c>round_ne</c> does.</summary>
    /// <param name="x">The value.</param>
    /// <returns>The rounded value.</returns>
    public static double Round(double x) => Math.Round(mode: MidpointRounding.ToEven, value: x);
    /// <summary>Folds a point into the base sector of <c>SDF_OP_REPEAT_POLAR</c>.</summary>
    /// <param name="p">The local point.</param>
    /// <param name="ins">The instruction.</param>
    /// <returns>The folded point.</returns>
    public static V3 PolarFold(V3 p, in SdfStudyTape.Instruction ins) {
        var (u, v) = PolarPlane(axis: ins.Shape);
        var sectorAngle = ins.Data0.X;
        var a = (Math.Atan2(y: p.At(axis: v), x: p.At(axis: u)) + (0.5 * sectorAngle));
        var r = Math.Sqrt(d: ((p.At(axis: u) * p.At(axis: u)) + (p.At(axis: v) * p.At(axis: v))));
        var sector = Math.Floor(d: (a * ins.Data0.Y));

        a = ((a - (sectorAngle * sector)) - (0.5 * sectorAngle));
        if (ins.Blend != 0u) {
            a = Math.Abs(value: a);
        }

        return p.With(axis: u, value: (Math.Cos(d: a) * r)).With(axis: v, value: (Math.Sin(a: a) * r));
    }
    /// <summary>Returns the fold plane's two axes for a polar repeat about <paramref name="axis"/>.</summary>
    /// <param name="axis">The axis lane.</param>
    /// <returns>The plane's first and second axes.</returns>
    public static (int U, int V) PolarPlane(uint axis) => (axis switch { 0u => (1, 2), 2u => (0, 1), _ => (0, 2) });

    private static SdfStudyRewrite Rewrite(SdfStudyRewrite[]? rewrites, int index) => ((rewrites is null) ? SdfStudyRewrite.None : rewrites[index]);
    private static double Compose(double current, double candidate, SdfBlendOp blend, double smooth, SdfStudyRewrite rewrite) => rewrite switch {
        SdfStudyRewrite.Copy => candidate,
        SdfStudyRewrite.CopyNegated => -candidate,
        _ => SdfStudyShapes.Blend(blend: blend, candidate: candidate, current: current, smooth: smooth),
    };
    // The rectangular lattice of a wallpaper fold without its in-cell symmetry: an approximation, reported by IsFaithful.
    private static V3 WallpaperLattice(V3 p, in SdfStudyTape.Instruction ins) {
        var axisA = ((ins.Blend == 2u) ? 1 : 0);
        var axisB = ((ins.Blend == 1u) ? 1 : 2);
        var ia = Math.Clamp(value: Round(x: (p.At(axis: axisA) * ins.Data0.Z)), min: -ins.Data1.X, max: ins.Data1.X);
        var ib = Math.Clamp(value: Round(x: (p.At(axis: axisB) * ins.Data0.W)), min: -ins.Data1.Y, max: ins.Data1.Y);

        return p.With(axis: axisA, value: (p.At(axis: axisA) - (ins.Data0.X * ia))).With(axis: axisB, value: (p.At(axis: axisB) - (ins.Data0.Y * ib)));
    }
}
