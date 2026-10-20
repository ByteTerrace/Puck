using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE CULL TABLES both walkers read, built from the reach certificates (SdfFieldEvaluator.Certificates.cs). A walk skips
// a slice of the stream only when it can prove the skip leaves its running answer exactly as walking the slice would:
//
// - An instance (m_cullBounds) joins the answer by hard union: its shapes compose by union outside a scope, and each
//   scope is one level deep and pops by union. A point walk skips it when every candidate it can offer is at least the
//   threshold: inside a scope a union is the lesser operand, a smooth union at least that less k/4, a subtraction or
//   intersection at least the accumulated operand, each within ScopeBlendRoundingRaws of that bound, so the lowering
//   members' certificates, less the scope's slack and scaled by its pop, bound the instance. A box walk skips it when
//   the lower end of every interval it can join is at least the running upper end, which leaves the running interval
//   bitwise; the interval blends inside a scope read their operands more than once, so a scope's certificate carries
//   what its own blends can lose (ScopeBoxReach).
// - A chain of shapes (m_segmentBounds) is skipped from its ResetPoint to its end when its certificate clears the
//   running answer (inside a scope, the scope's) by the chain's margin, the threshold at which every shape in it leaves
//   the answer and its material bitwise: a union at or above it is not taken, a smooth union at least k + k·2⁻¹⁵ + 16
//   raws past it rounds its blend weight to exactly one in either walk, a subtraction's negated candidate at or below it
//   is not taken.
// - When every compose outside a scope is a hard union (m_primeable), the answer is the least of the instances' own
//   answers. A walk then evaluates its nearest cullable instance first, and skips every instance whose candidates
//   exceed that instance's answer (a point walk) or reach its upper end (a box walk), wherever it falls in the stream.
//
// Among equal candidates the earlier keeps its material, which the point walk's strict test against the nearest
// instance preserves. Every skip leaves the distance, material, gradient and interval each walk would have answered.
public sealed partial class SdfFieldEvaluator {
    // The rounding allowance per soft or carving blend inside a scope, in raws: a polynomial smooth minimum lies at or
    // below the lesser operand before its three rounded steps (the lerp, the k·h product and its (1 − h) product), and
    // a rounded h moves it by k·ε², far below one raw, so each blend sits within three raws of its exact bound; eight
    // leaves room.
    private const long ScopeBlendRoundingRaws = 8L;

    // Every cullable instance, in First order, with its certificate over the candidates it can join the answer with and
    // its lowering members' certificates (m_members). Both certificates of an instance share one centre, the mean of its
    // shapes'.
    private static (CullBound[] Bounds, Reach[] Members) BuildCullBounds(SdfProgram program, CompiledInstruction[] compiled, ShapeReach?[] reaches) {
        var bounds = new List<CullBound>(capacity: program.Instances.Count);
        var members = new List<Reach>();
        var lowering = new List<ShapeReach>();
        var steps = new List<ScopeStep>();

        foreach (var instance in program.Instances) {
            if (instance.IsDynamic || !TryCertifyInstance(boxed: out var boxed, compiled: compiled, instance: instance, lowering: lowering, reaches: reaches, scoped: out var scoped, steps: steps)) {
                continue;
            }

            var centre = MeanCentre(steps: steps);
            var point = Enclose(centre: centre, members: lowering);
            var box = ((boxed && scoped) ? ScopeBoxReach(centre: centre, steps: steps) : point);

            if (!Reach.TryOf(reach: out var reach, shape: (point with { BoxCentred = box.BoxCentred, BoxGain = box.BoxGain, BoxRadius = box.BoxRadius, BoxSpread = box.BoxSpread, Boxed = (boxed && (box.BoxGain > 0.0)) }))) {
                continue;
            }

            var memberFirst = members.Count;

            foreach (var member in lowering) {
                if (Reach.TryOf(reach: out var memberReach, shape: member)) {
                    members.Add(item: memberReach);
                } else {
                    members.RemoveRange(index: memberFirst, count: (members.Count - memberFirst));

                    break;
                }
            }

            bounds.Add(item: new CullBound(
                End: instance.End,
                First: instance.First,
                MemberCount: (members.Count - memberFirst),
                MemberFirst: memberFirst,
                Reach: reach,
                Scoped: scoped
            ));
        }

        return (bounds.ToArray(), members.ToArray());
    }
    // Whether an instance joins the answer by hard union, every shape certified: outside a scope only point transforms
    // and union shapes; each scope one level deep, popping by union, holding union, smooth union, subtraction or
    // intersection shapes. Fills the lowering members' certificates in the answer's terms (a scope's slack subtracted
    // and its pop's scale applied), the instance's scope edges and shapes in stream order, and whether every shape has a
    // box certificate.
    private static bool TryCertifyInstance(CompiledInstruction[] compiled, SdfInstanceRange instance, ShapeReach?[] reaches, List<ShapeReach> lowering, List<ScopeStep> steps, out bool scoped, out bool boxed) {
        var depth = 0;
        var scopeStart = 0;
        var slackRaws = 0L;

        lowering.Clear();
        steps.Clear();
        scoped = false;
        boxed = true;

        for (var index = instance.First; (index < instance.End); index++) {
            ref readonly var instruction = ref compiled[index];

            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                case SdfOp.Translate:
                case SdfOp.Rotate:
                case SdfOp.Scale: {
                        continue;
                    }
                case SdfOp.PushField: {
                        if (depth != 0) {
                            return false;
                        }

                        depth = 1;
                        scoped = true;
                        scopeStart = lowering.Count;
                        slackRaws = 0L;
                        steps.Add(item: new ScopeStep(Blend: SdfBlendOp.Union, Op: SdfOp.PushField, Scale: 0.0, Shape: default, Smooth: 0.0));
                        continue;
                    }
                case SdfOp.PopField: {
                        if ((depth != 1) || (instruction.Blend != ((uint)SdfBlendOp.Union))) {
                            return false;
                        }

                        var scale = ((instruction.Data1Y > FixedQ4816.Zero) ? ((double)instruction.Data1Y) : 0.0);

                        for (var member = scopeStart; (member < lowering.Count); member++) {
                            lowering[member] = lowering[member].Scaled(scale: scale, slack: (slackRaws * RawUnit));
                        }

                        depth = 0;
                        steps.Add(item: new ScopeStep(Blend: SdfBlendOp.Union, Op: SdfOp.PopField, Scale: scale, Shape: default, Smooth: 0.0));
                        continue;
                    }
                case SdfOp.ShapeBlend: {
                        if (instruction.Detail) {
                            continue;
                        }

                        if (reaches[index] is not { } shape) {
                            return false;
                        }

                        var lowers = true;

                        if (depth == 0) {
                            if (instruction.Blend != ((uint)SdfBlendOp.Union)) {
                                return false;
                            }
                        } else {
                            switch ((SdfBlendOp)instruction.Blend) {
                                case SdfBlendOp.Union: {
                                        break;
                                    }
                                case SdfBlendOp.SmoothUnion: {
                                        var k = FixedQ4816.Max(x: instruction.Data1X, y: SmoothRadiusMin);

                                        slackRaws = checked((slackRaws + (((k.Value + 3L) >> 2) + ScopeBlendRoundingRaws)));
                                        break;
                                    }
                                case SdfBlendOp.Subtraction:
                                case SdfBlendOp.Intersection: {
                                        lowers = false;
                                        break;
                                    }
                                case SdfBlendOp.SmoothSubtraction:
                                case SdfBlendOp.SmoothIntersection: {
                                        lowers = false;
                                        slackRaws = checked((slackRaws + ScopeBlendRoundingRaws));
                                        break;
                                    }
                                default: {
                                        return false;
                                    }
                            }
                        }

                        if (lowers) {
                            lowering.Add(item: shape);
                        }

                        boxed &= shape.Boxed;
                        steps.Add(item: new ScopeStep(Blend: ((SdfBlendOp)instruction.Blend), Op: SdfOp.ShapeBlend, Scale: 0.0, Shape: shape, Smooth: ((double)FixedQ4816.Max(x: instruction.Data1X, y: SmoothRadiusMin))));
                        continue;
                    }
                default: {
                        return false;
                    }
            }
        }

        return ((depth == 0) && (lowering.Count > 0));
    }
    // The mean of an instance's shapes' centres.
    private static (double X, double Y, double Z) MeanCentre(List<ScopeStep> steps) {
        var (x, y, z, count) = (0.0, 0.0, 0.0, 0);

        foreach (var step in steps) {
            if (step.Op == SdfOp.ShapeBlend) {
                (x, y, z, count) = ((x + step.Shape.CenterX), (y + step.Shape.CenterY), (z + step.Shape.CenterZ), (count + 1));
            }
        }

        return ((x / count), (y / count), (z / count));
    }
    // A scoped instance's box certificate: every interval it joins the answer with has its lower end at least the
    // certificate's bound. Each shape's interval has its lower end at least L, the bound around all of them, and a width
    // at most a linear form in the box's distance d and half-diagonal ω (its far distance is at most d + 2ω plus its
    // centre's offset). Through a scope, BlendBounds keeps a lower bound L − Δ and a width bound W, both linear forms:
    // a union, subtraction or intersection keeps Δ (an intersection's lower end is the greater, a subtraction's at least
    // the current one's) and takes the greater width. Its smooth union (SmoothUnionBounds) of a current a and candidate b
    // of widths wa and wb is a itself when b lies at least k above a, b itself when at least k below, and otherwise reads
    // b − a once in its lerp, so its lower end is at least the lesser lower end less wa + k, its upper end at most b's
    // upper end plus wa + k, and its width at most 3wa + 2wb + 3k. The smooth intersection and subtraction negate it, so
    // their lower ends are at least b's less wa + k, and a's less wb + k. Each step also absorbs its outward roundings,
    // which sixteen raws and 2⁻¹⁴ of k and the widths cover, with room for the roundings of h near one. A pop by union
    // scales the scope's lower end by its candidate scale and rounds it down by a raw.
    private static ShapeReach ScopeBoxReach((double X, double Y, double Z) centre, List<ScopeStep> steps) {
        var shapes = new List<ShapeReach>();

        foreach (var step in steps) {
            if (step.Op == SdfOp.ShapeBlend) {
                shapes.Add(item: step.Shape);
            }
        }

        var all = Enclose(centre: centre, members: shapes);

        var (gain, radius, spread) = (all.BoxGain, all.BoxRadius, all.BoxSpread);
        var drop = LinearForm.Zero;
        var width = LinearForm.Zero;

        foreach (var step in steps) {
            switch (step.Op) {
                case SdfOp.PushField: {
                        drop = LinearForm.Zero;
                        width = LinearForm.Zero;
                        break;
                    }
                case SdfOp.PopField: {
                        var scale = ((step.Scale > 0.0) ? step.Scale : 1.0);
                        var rounding = ((step.Scale > 0.0) ? RawUnit : 0.0);

                        gain = Math.Min(val1: gain, val2: ((all.BoxGain - drop.Distance) * scale));
                        radius = Math.Max(val1: radius, val2: (((all.BoxRadius + drop.Constant) * scale) + rounding));
                        spread = Math.Max(val1: spread, val2: ((all.BoxSpread + drop.Spread) * scale));
                        break;
                    }
                default: {
                        var shape = step.Shape;
                        var offset = Length(x: (shape.CenterX - centre.X), y: (shape.CenterY - centre.Y), z: (shape.CenterZ - centre.Z));
                        var candidate = new LinearForm(
                            Constant: (shape.WidthConstant + (shape.WidthFar * offset)),
                            Distance: shape.WidthFar,
                            Spread: (shape.WidthSpread + (2.0 * shape.WidthFar))
                        );
                        var k = step.Smooth;
                        var slack = (new LinearForm(Constant: ((16.0 * RawUnit) + (k / 16384.0)), Distance: 0.0, Spread: 0.0) + ((width + candidate) * (1.0 / 16384.0)));
                        var blended = ((((width * 3.0) + (candidate * 2.0)) + new LinearForm(Constant: (3.0 * k), Distance: 0.0, Spread: 0.0)) + (slack * 2.0));

                        (drop, width) = step.Blend switch {
                            SdfBlendOp.SmoothUnion => (((drop + width) + slack).Plus(constant: k), blended),
                            SdfBlendOp.SmoothIntersection => ((width + slack).Plus(constant: k), blended),
                            SdfBlendOp.SmoothSubtraction => (((drop + candidate) + slack).Plus(constant: k), ((((candidate * 3.0) + (width * 2.0)) + new LinearForm(Constant: (3.0 * k), Distance: 0.0, Spread: 0.0)) + (slack * 2.0))),
                            _ => (drop, LinearForm.Greater(first: width, second: candidate)),
                        };
                        break;
                    }
            }
        }

        return all with { BoxGain = gain, BoxRadius = radius, BoxSpread = spread };
    }
    // Whether every lowering member's certificate clears the threshold, which proves the least of them does.
    private bool MembersClear(in CullBound bound, FixedVector3 worldPosition, Int128 threshold) {
        foreach (ref readonly var member in m_members.AsSpan(start: bound.MemberFirst, length: bound.MemberCount)) {
            if (!member.PointClears(point: worldPosition, threshold: threshold)) {
                return false;
            }
        }

        return true;
    }
    // Whether every compose outside a scope is a hard union and nothing outside a scope edits the accumulator: then the
    // answer is the least of the top-level candidates, each instance's own answer is the least of its own, and any one
    // of them bounds the answer from above.
    private static bool ComposesByHardUnion(CompiledInstruction[] compiled) {
        var depth = 0;

        foreach (ref readonly var instruction in compiled.AsSpan()) {
            switch (instruction.Op) {
                case SdfOp.PushField: {
                        depth++;
                        break;
                    }
                case SdfOp.PopField: {
                        depth--;

                        if ((depth == 0) && (instruction.Blend != ((uint)SdfBlendOp.Union))) {
                            return false;
                        }

                        break;
                    }
                case SdfOp.ShapeBlend: {
                        if ((depth == 0) && !instruction.Detail && (instruction.Blend != ((uint)SdfBlendOp.Union))) {
                            return false;
                        }

                        break;
                    }
                case SdfOp.Onion:
                case SdfOp.Dilate:
                case SdfOp.CellDisplace: {
                        if (depth == 0) {
                            return false;
                        }

                        break;
                    }
            }
        }

        return true;
    }
    // The segment table: for each ResetPoint opening a chain a walk may skip whole, the chain's end and the certificate
    // around its shapes'. A chain qualifies when it runs from its ResetPoint to the next ResetPoint, instance boundary,
    // scope edge or the stream's end through point transforms and shapes alone, with no point read past a scope edge
    // before the next reset (ClosesAtScopeEdge), every shape certified and joining the answer by union, smooth union or
    // subtraction. Its margin is the most any smooth union in it needs, k + k·2⁻¹⁵ + 16 raws, at which both walks round
    // the blend weight to exactly one, and a chain that carves also needs its candidates past the answer's negation. A
    // chain never crosses an instance boundary, so a walk still meets every instance it tests in First order.
    private static (int[] Slots, SegmentBound[] Bounds) BuildSegments(SdfProgram program, CompiledInstruction[] compiled, ShapeReach?[] reaches) {
        var count = compiled.Length;
        var slots = new int[count];
        var bounds = new List<SegmentBound>();
        var boundary = new bool[(count + 1)];
        var shapes = new List<ShapeReach>();

        foreach (var instance in program.Instances) {
            boundary[instance.First] = true;
            boundary[instance.End] = true;
        }

        Array.Fill(array: slots, value: -1);

        for (var start = 0; (start < count); start++) {
            if (compiled[start].Op != SdfOp.ResetPoint) {
                continue;
            }

            var end = (start + 1);
            var qualifies = true;
            var margin = FixedQ4816.Zero;
            var carves = false;

            shapes.Clear();

            while ((end < count) && !boundary[end] && (compiled[end].Op != SdfOp.ResetPoint)) {
                ref readonly var instruction = ref compiled[end];

                if (instruction.Op is SdfOp.Translate or SdfOp.Rotate or SdfOp.Scale) {
                    end++;
                    continue;
                }

                // A scope edge ends the chain only when nothing past it reads the point before the next reset.
                if (instruction.Op is SdfOp.PushField or SdfOp.PopField) {
                    qualifies = ClosesAtScopeEdge(boundary: boundary, compiled: compiled, edge: end);
                    break;
                }

                if (instruction.Op != SdfOp.ShapeBlend) {
                    qualifies = false;
                    break;
                }

                if (!instruction.Detail) {
                    if (reaches[end] is not { } shape) {
                        qualifies = false;
                        break;
                    }

                    switch ((SdfBlendOp)instruction.Blend) {
                        case SdfBlendOp.Union: {
                                break;
                            }
                        case SdfBlendOp.SmoothUnion: {
                                var k = FixedQ4816.Max(x: instruction.Data1X, y: SmoothRadiusMin);

                                margin = FixedQ4816.Max(x: margin, y: FixedQ4816.FromRawBits(value: ((k.Value + (k.Value >> 15)) + 16L)));
                                break;
                            }
                        case SdfBlendOp.Subtraction: {
                                carves = true;
                                break;
                            }
                        default: {
                                qualifies = false;
                                break;
                            }
                    }

                    if (!qualifies) {
                        break;
                    }

                    shapes.Add(item: shape);
                }

                end++;
            }

            if (!qualifies || (shapes.Count == 0) || !Reach.TryOf(reach: out var reach, shape: Enclose(members: shapes))) {
                continue;
            }

            slots[start] = bounds.Count;
            bounds.Add(item: new SegmentBound(Carves: carves, End: end, Margin: margin, Reach: reach));
        }

        return (slots, bounds.ToArray());
    }
    // Whether only scope edges stand between a chain's end and the next ResetPoint, instance boundary or the stream's
    // end: a skipped chain leaves the point where its ResetPoint put it, which no instruction after it may read.
    private static bool ClosesAtScopeEdge(CompiledInstruction[] compiled, bool[] boundary, int edge) {
        for (var index = edge; ((index < compiled.Length) && !boundary[index] && (compiled[index].Op != SdfOp.ResetPoint)); index++) {
            if (compiled[index].Op is not (SdfOp.PushField or SdfOp.PopField)) {
                return false;
            }
        }

        return true;
    }
    // The reach a chain's candidates must clear to leave an answer bitwise: the answer plus the chain's margin, and the
    // answer's negation when it carves, taken in Int128 so neither wraps.
    private static Int128 SegmentThreshold(in SegmentBound bound, FixedQ4816 above, FixedQ4816 below) {
        var threshold = (((Int128)above.Value) + bound.Margin.Value);

        return (bound.Carves ? Int128.Max(x: threshold, y: -((Int128)below.Value)) : threshold);
    }

    // One cullable instance: its stream range, the certificate over every candidate it can join the answer with, whether
    // it holds a scope, and its run of lowering members in m_members. The certificate is a field, so a walk reads it in
    // place rather than through a copying property.
    private readonly record struct CullBound(int First, int End, Reach Reach, bool Scoped, int MemberFirst, int MemberCount) {
        public readonly Reach Reach = Reach;
    }
    // One step of a cullable instance in stream order: a scope's push, a shape with its blend, smooth radius in units and
    // certificate, or a pop with its candidate scale (zero for none).
    private readonly record struct ScopeStep(SdfOp Op, SdfBlendOp Blend, double Smooth, double Scale, ShapeReach Shape);
    // A bound linear in a box's distance and half-diagonal, in units: Constant + Distance·d + Spread·ω.
    private readonly record struct LinearForm(double Constant, double Distance, double Spread) {
        public static LinearForm Zero => default;

        public static LinearForm operator +(LinearForm left, LinearForm right) =>
            new(Constant: (left.Constant + right.Constant), Distance: (left.Distance + right.Distance), Spread: (left.Spread + right.Spread));
        public static LinearForm operator *(LinearForm form, double factor) =>
            new(Constant: (form.Constant * factor), Distance: (form.Distance * factor), Spread: (form.Spread * factor));

        // The form at least both, term by term, which bounds the greater of the two at every distance and half-diagonal.
        public static LinearForm Greater(LinearForm first, LinearForm second) =>
            new(Constant: Math.Max(val1: first.Constant, val2: second.Constant), Distance: Math.Max(val1: first.Distance, val2: second.Distance), Spread: Math.Max(val1: first.Spread, val2: second.Spread));
        public LinearForm Plus(double constant) => this with { Constant = (Constant + constant) };
    }
    // One skippable chain: the instruction after its last, its certificate, the margin a smooth union in it needs above
    // the running answer to leave it bitwise, and whether a subtraction in it also needs its candidates past the answer's
    // negation.
    private readonly record struct SegmentBound(int End, Reach Reach, FixedQ4816 Margin, bool Carves) {
        public readonly Reach Reach = Reach;
    }
}
