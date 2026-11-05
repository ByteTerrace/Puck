using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE CULL TABLES both walkers read, built from the reach certificates (SdfFieldEvaluator.Certificates.cs). A walk skips
// a slice of the stream only when it can prove the skip leaves its running answer exactly as walking the slice would:
//
// - An instance (m_cullBounds) joins the answer by hard union: its shapes compose by union outside a scope, and each
//   scope is one level deep and pops by union. A point walk skips it when every candidate it can offer is at least the
//   threshold: inside a scope a union is the lesser operand, a smooth union at least that less k/4, a run of them never
//   more than its widest radius below its least operand, a subtraction or intersection at least the accumulated
//   operand, each within ScopeBlendRoundingRaws of that bound, so the lowering members' certificates, less the scope's
//   slack and scaled by its pop, bound the instance. A box walk skips it when the lower end of every interval it can
//   join is at least the running upper end, which leaves the running interval bitwise; a scope's certificate carries
//   what its own blends can lose (ScopeBoxReach). The run: the exact smooth union of accumulator x and candidate c, of
//   radius k, is convex in its weight, so at any weight it is at least its least value over every weight: x once c lies
//   k above x, c once k below, at least c − k/4 from c ≤ x < c + k, and x − (k − t)²/(4k) ≥ c − k for t = c − x in
//   (0, k); never below min(x, c − k). By induction a run of unions and smooth unions is never below the least operand
//   it joined less its widest radius, beside the k/4 a blend that each blend's own bound gives.
// - A chain of shapes (m_segmentBounds) is skipped from its ResetPoint to its end when its certificate clears the
//   running answer (inside a scope, the scope's) by the chain's margin, the threshold at which every shape in it leaves
//   the answer and its material bitwise: a union at or above it is not taken, a smooth union at least k + k·2⁻¹⁵ + 16
//   raws past it rounds its blend weight to exactly one in either walk, a subtraction's negated candidate at or below it
//   is not taken.
// - Consecutive chains outside every cullable instance are grouped (m_segmentGroups, BuildSegmentGroups) so a point
//   walk skips a whole run of them with one test of the certificate around them all.
// - A relief inside a scope (NoiseDisplace) is charged to the scope's slack at its certified reach (ReliefLoweringRaws),
//   so a relieved instance is cullable by the point walk; it carries no box certificate.
// - When every compose outside a scope is a hard union (m_primeable), the answer is the least of the instances' own
//   answers. A walk then evaluates its nearest cullable instance first, and skips every instance whose candidates
//   exceed that instance's answer (a point walk) or reach its upper end (a box walk), wherever it falls in the stream.
//
// Among equal candidates the earlier keeps its material, which the point walk's strict test against the nearest
// instance preserves. Every skip leaves the distance, material, gradient and interval each walk would have answered.
public sealed partial class SdfFieldEvaluator {
    // The rounding allowance per soft or carving blend, in raws: a polynomial smooth minimum lies at or below the lesser
    // operand, and at most k/4 under it, before its three rounded steps (the lerp, the k·h product and its (1 − h)
    // product), each within half a raw. Its rounded weight h' leaves the exact h by ε ≤ 2⁻¹⁷(1 + 1/k), and the blend is
    // a convex quadratic in its weight with its minimum at h, so h' moves it by k·ε², at most 2⁻¹⁸(k + 2 + 1/k) raws:
    // under a tenth of a raw for every radius past SmoothRadiusMin, plus k·2⁻¹⁸ raws for a radius past one metre.
    private const long ScopeBlendRoundingRaws = 8L;

    // The rounding allowance, in raws, of one soft blend of radius k (see ScopeBlendRoundingRaws).
    private static long SmoothBlendRoundingRaws(FixedQ4816 k) =>
        (ScopeBlendRoundingRaws + (k.Value >> (FixedQ4816.FractionBitCount + 18)));

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

        var boundRows = bounds.ToArray();
        CountArrayPayload(rows: boundRows, work: PointWork, arrays: CullArraysCreated, bytes: CullPayloadCreated);
        var memberRows = members.ToArray();
        CountArrayPayload(rows: memberRows, work: PointWork, arrays: CullArraysCreated, bytes: CullPayloadCreated);
        return (boundRows, memberRows);
    }
    // Whether an instance joins the answer by hard union, every shape certified: outside a scope only point transforms
    // and union shapes; each scope one level deep, popping by union, holding union, smooth union, subtraction or
    // intersection shapes. Fills the lowering members' certificates in the answer's terms (a scope's slack subtracted
    // and its pop's scale applied), the instance's scope edges and shapes in stream order, and whether every shape has a
    // box certificate.
    private static bool TryCertifyInstance(CompiledInstruction[] compiled, SdfInstanceRange instance, ShapeReach?[] reaches, List<ShapeReach> lowering, List<ScopeStep> steps, out bool scoped, out bool boxed) {
        var depth = 0;
        var scopeStart = 0;
        // A scope's slack: its smooth unions' quarter radii summed, or their widest radius when that is less (a run of
        // smooth unions, see the type remarks), plus every soft blend's rounding allowance.
        var quarterRaws = 0L;
        var radiusRaws = 0L;
        var roundingRaws = 0L;

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
                        (quarterRaws, radiusRaws, roundingRaws) = (0L, 0L, 0L);
                        steps.Add(item: new ScopeStep(Blend: SdfBlendOp.Union, Op: SdfOp.PushField, Scale: 0.0, Shape: default, Smooth: 0.0));
                        continue;
                    }
                case SdfOp.PopField: {
                        if ((depth != 1) || (instruction.Blend != ((uint)SdfBlendOp.Union))) {
                            return false;
                        }

                        var scale = ((instruction.ScopeScale > 0L) ? (instruction.ScopeScale / ScopeScaleOne) : 0.0);
                        var slack = ((Math.Min(val1: quarterRaws, val2: radiusRaws) + roundingRaws) * RawUnit);

                        for (var member = scopeStart; (member < lowering.Count); member++) {
                            lowering[member] = lowering[member].Scaled(scale: scale, slack: slack);
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

                                        quarterRaws = checked((quarterRaws + ((k.Value + 3L) >> 2)));
                                        radiusRaws = Math.Max(val1: radiusRaws, val2: k.Value);
                                        roundingRaws = checked((roundingRaws + SmoothBlendRoundingRaws(k: k)));
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
                                        roundingRaws = checked((roundingRaws + SmoothBlendRoundingRaws(k: FixedQ4816.Max(x: instruction.Data1X, y: SmoothRadiusMin))));
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
                case SdfOp.NoiseDisplace: {
                        // A relief inside the scope lowers the scope's field by at most its certified reach, wherever it
                        // stands among the scope's shapes, so it joins the slack every lowering member is charged at the
                        // pop. Its box reach is not carried, so the box walk never skips the instance.
                        if ((depth != 1) || (ReliefLoweringRaws(instruction: in instruction) is not { } reliefRaws)) {
                            return false;
                        }

                        roundingRaws = checked((roundingRaws + reliefRaws));
                        boxed = false;
                        continue;
                    }
                default: {
                        return false;
                    }
            }
        }

        return ((depth == 0) && (lowering.Count > 0));
    }
    // The most a relief (EditField's NoiseDisplace) can lower the field it edits, in raws: its scale, rounded as the point
    // rule rounds it, times the octave sum enclosed over every lattice point (Pcg3dLatticeNoise.Fbm3Bounds over an
    // unbounded box), in outward-rounded interval arithmetic; the point rule's addition is exact. None when the
    // enclosure leaves the carrier.
    private static long? ReliefLoweringRaws(in CompiledInstruction instruction) {
        var relief = (FixedInterval.FromPoint(value: (instruction.Data0Y * instruction.Data1X)) * Pcg3dLatticeNoise.Fbm3Bounds(
            gain: instruction.Data0Z, lacunarity: instruction.Data0W, octaves: ((int)instruction.Blend), seed: instruction.Shape,
            x: FixedInterval.Entire, y: FixedInterval.Entire, z: FixedInterval.Entire));

        return (relief.IsUnbounded ? null : Math.Max(val1: 0L, val2: -relief.Lower.Value));
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
    // certificate's bound. Each shape's interval has its lower end at least L, the bound around all of them. Through a
    // scope the box walk keeps a lower bound L − Δ: a union, subtraction or intersection keeps
    // Δ (an intersection's lower end is the greater, a subtraction's at least the current one's). Its smooth union
    // (SmoothUnionBounds) has its lower end at the exact smooth union of the operands' lower ends less at most three
    // rounding allowances r, so a run of them (see the type remarks) adds the lesser of its quarter radii summed and its
    // widest radius to Δ, and every soft blend adds 3r; a smooth intersection or subtraction is at least its current
    // operand's lower end less 3r. A pop by union scales the scope's lower end by its candidate scale and rounds it down
    // by a raw.
    private static ShapeReach ScopeBoxReach((double X, double Y, double Z) centre, List<ScopeStep> steps) {
        var shapes = new List<ShapeReach>();

        foreach (var step in steps) {
            if (step.Op == SdfOp.ShapeBlend) {
                shapes.Add(item: step.Shape);
            }
        }

        var all = Enclose(centre: centre, members: shapes);

        var (gain, radius, spread) = (all.BoxGain, all.BoxRadius, all.BoxSpread);
        var (quarters, widest, roundings) = (0.0, 0.0, 0.0);

        foreach (var step in steps) {
            switch (step.Op) {
                case SdfOp.PushField: {
                        (quarters, widest, roundings) = (0.0, 0.0, 0.0);
                        break;
                    }
                case SdfOp.PopField: {
                        var scale = ((step.Scale > 0.0) ? step.Scale : 1.0);
                        var rounding = ((step.Scale > 0.0) ? RawUnit : 0.0);
                        var drop = (Math.Min(val1: quarters, val2: widest) + roundings);

                        gain = Math.Min(val1: gain, val2: (all.BoxGain * scale));
                        radius = Math.Max(val1: radius, val2: (((all.BoxRadius + drop) * scale) + rounding));
                        spread = Math.Max(val1: spread, val2: (all.BoxSpread * scale));
                        break;
                    }
                default: {
                        var k = step.Smooth;
                        // The blend's rounding allowance (SmoothBlendRoundingRaws), and k/4 rounded up to a raw, each a
                        // raw past the double's own rounding.
                        var allowance = (((ScopeBlendRoundingRaws + Math.Floor(d: (k / 262144.0))) + 1.0) * RawUnit);

                        if (step.Blend == SdfBlendOp.SmoothUnion) {
                            quarters += ((k / 4.0) + (2.0 * RawUnit));
                            widest = Math.Max(val1: widest, val2: (k + RawUnit));
                        }

                        if (step.Blend is SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction) {
                            roundings += (3.0 * allowance);
                        }

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
                case SdfOp.CellDisplace:
                case SdfOp.Displace:
                case SdfOp.NoiseDisplace: {
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
    private static (int[] Slots, SegmentBound[] Bounds, List<ShapeReach> Enclosures) BuildSegments(SdfProgram program, CompiledInstruction[] compiled, ShapeReach?[] reaches) {
        var count = compiled.Length;
        var slots = new int[count];
        CountArrayPayload(rows: slots, work: PointWork, arrays: CullArraysCreated, bytes: CullPayloadCreated);
        var bounds = new List<SegmentBound>();
        var enclosures = new List<ShapeReach>();
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

            if (!qualifies || (shapes.Count == 0)) {
                continue;
            }

            var enclosure = Enclose(members: shapes);

            if (!Reach.TryOf(reach: out var reach, shape: enclosure)) {
                continue;
            }

            slots[start] = bounds.Count;
            bounds.Add(item: new SegmentBound(Carves: carves, End: end, Margin: margin, Reach: reach));
            enclosures.Add(item: enclosure);
        }

        var segmentRows = bounds.ToArray();
        CountArrayPayload(rows: segmentRows, work: PointWork, arrays: CullArraysCreated, bytes: CullPayloadCreated);
        PointWork.Count(kind: CullTableBuilds);
        return (slots, segmentRows, enclosures);
    }
    // The segment hierarchy over every run of chains that follow one another with nothing between (each chain's end the
    // next one's ResetPoint) outside every cullable instance: nodes of up to SegmentGroupFanout consecutive chains, then
    // of nodes, each with the certificate around its members' (Enclose's triangle inequality holds for certificates as
    // for shapes), their widest margin and whether any carves. A node clearing the running answer by that margin proves
    // every chain under it clears by its own: the node's bound is at most each member's, its threshold at least each
    // member's, and a skipped chain leaves the answer bitwise, so each later member meets the same threshold. A walk at a
    // chain's ResetPoint tests the largest node starting there first (Heads), then the next smaller (Next), then the chain.
    // No node spans a cullable instance or a scope edge, so a skip passes no cull bound's First and no point read.
    private static (int[] Heads, SegmentBound[] Groups, int[] Next) BuildSegmentGroups(CompiledInstruction[] compiled, int[] slots, SegmentBound[] bounds, List<ShapeReach> enclosures, CullBound[] cullBounds) {
        var heads = new int[bounds.Length];
        var groups = new List<SegmentBound>();
        var next = new List<int>();
        var culled = new bool[compiled.Length];
        var starts = new int[bounds.Length];

        Array.Fill(array: heads, value: -1);
        foreach (var bound in cullBounds) {
            Array.Fill(array: culled, value: true, startIndex: bound.First, count: (bound.End - bound.First));
        }
        for (var index = 0; (index < slots.Length); index++) {
            if (slots[index] >= 0) { starts[slots[index]] = index; }
        }

        var level = new List<(int Slot, SegmentBound Bound, ShapeReach Enclosure)>();
        var above = new List<(int Slot, SegmentBound Bound, ShapeReach Enclosure)>();
        var members = new List<ShapeReach>();
        var slot = 0;

        while (slot < bounds.Length) {
            level.Clear();
            while ((slot < bounds.Length) && !culled[starts[slot]] && ((level.Count == 0) || (level[^1].Bound.End == starts[slot]))) {
                level.Add(item: (slot, bounds[slot], enclosures[slot]));
                slot++;
            }
            if (level.Count == 0) {
                slot++;
                continue;
            }
            while (level.Count > 1) {
                above.Clear();
                for (var first = 0; (first < level.Count); first += SegmentGroupFanout) {
                    var last = Math.Min(val1: level.Count, val2: (first + SegmentGroupFanout));

                    if ((last - first) < 2) {
                        above.Add(item: level[first]);
                        continue;
                    }

                    members.Clear();
                    var margin = FixedQ4816.Zero;
                    var carves = false;

                    for (var member = first; (member < last); member++) {
                        members.Add(item: level[member].Enclosure);
                        margin = FixedQ4816.Max(x: margin, y: level[member].Bound.Margin);
                        carves |= level[member].Bound.Carves;
                    }

                    var enclosure = Enclose(members: members);
                    var certified = Reach.TryOf(reach: out var reach, shape: enclosure);
                    var group = new SegmentBound(Carves: carves, End: level[(last - 1)].Bound.End, Margin: margin, Reach: reach);

                    if (certified) {
                        next.Add(item: heads[level[first].Slot]);
                        heads[level[first].Slot] = groups.Count;
                        groups.Add(item: group);
                    }
                    above.Add(item: (level[first].Slot, group, enclosure));
                }
                (level, above) = (above, level);
            }
        }

        var groupRows = groups.ToArray();
        CountArrayPayload(rows: groupRows, work: PointWork, arrays: CullArraysCreated, bytes: CullPayloadCreated);
        return (heads, groupRows, next.ToArray());
    }
    // How many consecutive chains, or nodes, one node of the segment hierarchy holds.
    private const int SegmentGroupFanout = 8;
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
    // One skippable chain: the instruction after its last, its certificate, the margin a smooth union in it needs above
    // the running answer to leave it bitwise, and whether a subtraction in it also needs its candidates past the answer's
    // negation.
    private readonly record struct SegmentBound(int End, Reach Reach, FixedQ4816 Margin, bool Carves) {
        public readonly Reach Reach = Reach;
    }
}
