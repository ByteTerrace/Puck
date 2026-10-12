using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE CULL TABLES both walkers read. A point walk (TryDistance) and a box walk (BoundsOver) skip the same slices for the
// same reason: every candidate the slice could offer is at least a lower bound the table proves, and that bound already
// clears the answer, so the slice cannot move it.
//
// - An instance (m_cullBounds) is skipped whole when its sphere's reach clears the running answer. It qualifies when it
//   joins the answer by hard union: either every shape composes by plain union (IsPureUnionInstance), or its shapes sit
//   in one-level scopes that join through a hard-union pop (IsScopedUnionInstance), its radius widened by the scope's
//   blend slack.
// - A chain of shapes (m_segmentBounds) is skipped from its ResetPoint to its end when the sphere around the program's
//   own skip spheres of its shapes clears the running answer, which inside a scope is the scope's.
// - When every compose outside a scope is a hard union (m_primeable), the answer is the least of the instances' own
//   answers. A walk then evaluates its nearest cullable instance first, and skips every instance whose reach exceeds
//   that instance's answer (a point walk) or reaches its upper end (a box walk), wherever it falls in the stream.
//
// A point walk's skips move no distance, material or gradient: a skipped candidate could never have been strictly less
// than the answer it would have met, and among equal candidates the earlier one keeps its material, which the strict
// test against the nearest instance preserves. A box walk's skips keep its interval an enclosure of every point answer
// in the box, the contract BoundsOver states; they change the interval itself, since a walked instance's loose interval
// can lower the lower end.
public sealed partial class SdfFieldEvaluator {
    // The rounding allowance per soft or carving blend inside a scope, in raws: a polynomial smooth minimum lies at or
    // below the lesser operand before its three rounded steps (the lerp, the k·h product and its (1 − h) product), and
    // a rounded h moves it by k·ε², far below one raw, so each blend sits within three raws of its exact bound; eight
    // leaves room.
    private const long ScopeBlendRoundingRaws = 8L;

    // Every cullable instance, in First order: a hard-union instance, or a scoped instance joining through a hard-union
    // pop. A plane, or any compose the program itself marks as reaching every tile, has no finite bound however small
    // the instance declared; the program's own test is the one the GPU prepass trusts.
    private static (CullBound[] Bounds, MemberSphere[] Members) BuildCullBounds(SdfProgram program, CompiledInstruction[] compiled) {
        var instances = program.Instances;
        var instructions = program.Instructions;
        var result = new List<CullBound>(capacity: instances.Count);
        var members = new List<MemberSphere>();

        for (var index = 0; (index < instances.Count); index++) {
            var instance = instances[index];

            if (instance.IsDynamic || !program.InstanceHasDistanceLowerBound(index: index)) {
                continue;
            }

            if (IsPureUnionInstance(instance: instance, instructions: instructions)) {
                result.Add(item: CullBoundOf(candidateScale: FixedQ4816.Zero, instance: instance, memberCount: 0, memberFirst: 0, slack: FixedQ4816.Zero));
            } else if (IsScopedUnionInstance(candidateScale: out var candidateScale, compiled: compiled, instance: instance, slack: out var slack)) {
                var memberFirst = members.Count;
                var memberCount = (TryAddMembers(compiled: compiled, instance: instance, members: members, program: program) ? (members.Count - memberFirst) : 0);

                result.Add(item: CullBoundOf(candidateScale: candidateScale, instance: instance, memberCount: memberCount, memberFirst: memberFirst, slack: slack));
            }
        }

        return (result.ToArray(), members.ToArray());
    }
    private static CullBound CullBoundOf(SdfInstanceRange instance, FixedQ4816 candidateScale, FixedQ4816 slack, int memberFirst, int memberCount) => new(
        CandidateScale: candidateScale,
        CenterX: FixedQ4816.FromDouble(value: instance.Center.X),
        CenterY: FixedQ4816.FromDouble(value: instance.Center.Y),
        CenterZ: FixedQ4816.FromDouble(value: instance.Center.Z),
        End: instance.End,
        First: instance.First,
        MemberCount: memberCount,
        MemberFirst: memberFirst,
        Radius: (FixedQ4816.FromDouble(value: ((instance.Radius * CullBoundRadiusScale) + CullBoundRadiusPadding)) + slack),
        Slack: slack
    );
    // A scoped instance's members, the shapes whose candidates can lower its answer: each shape outside the scope, and
    // each union or smooth union inside it, every one with the program's reach sphere. A subtraction or intersection
    // inside the scope only raises it, so it bounds nothing. Without a sphere for every member the instance keeps its
    // one sphere, and the members it added are dropped.
    private static bool TryAddMembers(SdfProgram program, CompiledInstruction[] compiled, SdfInstanceRange instance, List<MemberSphere> members) {
        var first = members.Count;
        var depth = 0;

        for (var index = instance.First; (index < instance.End); index++) {
            ref readonly var instruction = ref compiled[index];

            if (instruction.Op == SdfOp.PushField) {
                depth++;
                continue;
            }

            if (instruction.Op == SdfOp.PopField) {
                depth--;
                continue;
            }

            if ((instruction.Op != SdfOp.ShapeBlend) || instruction.Detail || ((depth > 0) && (instruction.Blend is not (((uint)SdfBlendOp.Union) or ((uint)SdfBlendOp.SmoothUnion))))) {
                continue;
            }

            var sphere = program.ShapeReachSphere(instruction: index);

            if (!IsStaticSphere(sphere: sphere)) {
                members.RemoveRange(index: first, count: (members.Count - first));

                return false;
            }

            members.Add(item: new MemberSphere(
                CenterX: FixedQ4816.FromDouble(value: sphere.Center.X),
                CenterY: FixedQ4816.FromDouble(value: sphere.Center.Y),
                CenterZ: FixedQ4816.FromDouble(value: sphere.Center.Z),
                Radius: FixedQ4816.FromDouble(value: ((sphere.Radius * CullBoundRadiusScale) + CullBoundRadiusPadding)),
                Scoped: (depth > 0)
            ));
        }

        return (members.Count > first);
    }
    private static bool IsStaticSphere(SdfSkipSphere sphere) =>
        ((sphere.Mode == SdfProgram.BoundModeStatic) && float.IsFinite(f: sphere.Radius) && float.IsFinite(f: sphere.Center.X) && float.IsFinite(f: sphere.Center.Y) && float.IsFinite(f: sphere.Center.Z));
    // Whether every member's reach clears the threshold, which proves the least of them does: a scoped member's reach
    // less the scope's blend slack, scaled by the pop; a member outside the scope as it is. The largest axis distance is
    // tried before each root.
    private bool MembersClear(in CullBound bound, FixedVector3 worldPosition, FixedQ4816 threshold) {
        foreach (ref readonly var member in m_members.AsSpan(start: bound.MemberFirst, length: bound.MemberCount)) {
            var (x, y, z) = ((worldPosition.X - member.CenterX), (worldPosition.Y - member.CenterY), (worldPosition.Z - member.CenterZ));

            if ((MemberReach(bound: in bound, member: in member, distance: AxisDistance(x: x, y: y, z: z)) < threshold) &&
                !MemberLengthClears(bound: in bound, member: in member, threshold: threshold, x: x, y: y, z: z)) {
                return false;
            }
        }

        return true;
    }
    private static FixedQ4816 MemberReach(in CullBound bound, in MemberSphere member, FixedQ4816 distance) => (member.Scoped
        ? ScaledReach(bound: in bound, reach: ((distance - member.Radius) - bound.Slack))
        : (distance - member.Radius));
    // MemberReach of the rounded length clears the threshold: decided on the sum of squares while the reach is the
    // length less constants, through the root when a candidate scale rounds it.
    private static bool MemberLengthClears(in CullBound bound, in MemberSphere member, FixedQ4816 threshold, FixedQ4816 x, FixedQ4816 y, FixedQ4816 z) =>
        ((member.Scoped && (bound.CandidateScale > FixedQ4816.Zero))
            ? (MemberReach(bound: in bound, distance: new FixedVector3(X: x, Y: y, Z: z).Length, member: in member) >= threshold)
            : LengthAtLeast(least: ((((Int128)threshold.Value) + member.Radius.Value) + (member.Scoped ? bound.Slack.Value : 0L)), x: x, y: y, z: z));
    // Whether an instance's rounded-length reach clears the threshold, on the sum of squares unless a candidate scale
    // rounds the reach.
    private static bool PointLengthClears(in CullBound bound, FixedVector3 worldPosition, FixedQ4816 threshold) =>
        ((bound.CandidateScale > FixedQ4816.Zero)
            ? (PointReach(bound: in bound, worldPosition: worldPosition) >= threshold)
            : LengthAtLeast(least: (((Int128)threshold.Value) + bound.Radius.Value), x: (worldPosition.X - bound.CenterX), y: (worldPosition.Y - bound.CenterY), z: (worldPosition.Z - bound.CenterZ)));
    // Whether FixedVector3.Length of (x, y, z) is at least least raws, without its root. The length is the root of the
    // exact sum of squares S rounded to nearest, f + [S − f² > f] for f = ⌊√S⌋, so it reaches L ≥ 1 exactly when
    // S ≥ L² − L + 1; it saturates at the carrier's maximum, which no least above the maximum reaches.
    private static bool LengthAtLeast(FixedQ4816 x, FixedQ4816 y, FixedQ4816 z, Int128 least) {
        if (least <= Int128.Zero) {
            return true;
        }

        if (least > long.MaxValue) {
            return false;
        }

        var (mx, my, mz) = (FusedArithmetic.RawMagnitude(value: x.Value), FusedArithmetic.RawMagnitude(value: y.Value), FusedArithmetic.RawMagnitude(value: z.Value));
        var sum = ((Math.BigMul(a: mx, b: mx) + Math.BigMul(a: my, b: my)) + Math.BigMul(a: mz, b: mz));
        var length = ((UInt128)((ulong)least));

        return (sum >= (((length * length) - length) + UInt128.One));
    }
    // The least candidate an instance can offer a point: the true Euclidean distance from the point to any point the
    // instance's geometry can ever occupy is at least (distance-to-center - radius), the same sphere-containment
    // guarantee the GPU beam prepass relies on for its own tile cull (SdfInstanceRange's remarks). A scoped instance's
    // pop candidate is its scope's field times the pop's candidate scale, and rounding a product is monotone in either
    // positive factor, so the scaled reach bounds it; a shape outside the scope joins unscaled, so the lesser of the two
    // bounds the instance.
    private static FixedQ4816 PointReach(in CullBound bound, FixedVector3 worldPosition) => ScaledReach(
        bound: in bound,
        reach: (new FixedVector3(
            X: (worldPosition.X - bound.CenterX),
            Y: (worldPosition.Y - bound.CenterY),
            Z: (worldPosition.Z - bound.CenterZ)
        ).Length - bound.Radius)
    );
    // A reach no greater than PointReach, without its root: the largest axis distance is an integer no greater than the
    // exact length, and so no greater than its rounded root. A walk tests it first and takes the root only when it
    // does not clear, which decides every test exactly as the root alone would.
    private static FixedQ4816 CheapPointReach(in CullBound bound, FixedVector3 worldPosition) => ScaledReach(
        bound: in bound,
        reach: (AxisDistance(x: (worldPosition.X - bound.CenterX), y: (worldPosition.Y - bound.CenterY), z: (worldPosition.Z - bound.CenterZ)) - bound.Radius)
    );
    private static FixedQ4816 ScaledReach(in CullBound bound, FixedQ4816 reach) => ((bound.CandidateScale > FixedQ4816.Zero)
        ? FixedQ4816.Min(x: reach, y: (reach * bound.CandidateScale))
        : reach);
    // The largest axis distance, saturating where a magnitude passes the carrier as the rounded length does.
    private static FixedQ4816 AxisDistance(FixedQ4816 x, FixedQ4816 y, FixedQ4816 z) {
        var largest = Math.Max(val1: FusedArithmetic.RawMagnitude(value: x.Value), val2: Math.Max(val1: FusedArithmetic.RawMagnitude(value: y.Value), val2: FusedArithmetic.RawMagnitude(value: z.Value)));

        return FixedQ4816.FromRawBits(value: ((long)Math.Min(val1: largest, val2: ((ulong)long.MaxValue))));
    }
    // An instance is cullable only when every instruction it owns is either a rigid point transform that never reads
    // or writes the running accumulator (ResetPoint/Translate/Rotate/Scale/Repeat/RepeatLimited/SymmetryPlane/
    // Elongate) or a ShapeBlend composing by plain SdfBlendOp.Union. Under that restriction the instance's own bound
    // (a world-space sphere containing every shape it can ever place, however its local point transforms) proves
    // every candidate it can produce is at least the bound's own lower-bound distance from the query point — so a
    // union fold over any number of such candidates cannot lower the answer below what the reach tests.
    private static bool IsPureUnionInstance(SdfInstanceRange instance, IReadOnlyList<SdfInstruction> instructions) {
        for (var index = instance.First; (index < instance.End); index++) {
            switch (instructions[index].Op) {
                case SdfOp.ResetPoint:
                case SdfOp.Translate:
                case SdfOp.Rotate:
                case SdfOp.Scale:
                case SdfOp.Repeat:
                case SdfOp.RepeatLimited:
                case SdfOp.SymmetryPlane:
                case SdfOp.Elongate: {
                        continue;
                    }
                case SdfOp.ShapeBlend: {
                        if (instructions[index].Blend != ((uint)SdfBlendOp.Union)) {
                            return false;
                        }

                        continue;
                    }
                default: {
                        return false;
                    }
            }
        }

        return true;
    }
    // A scoped instance either walk may skip: the instance already admits the distance lower bound (similarity
    // transforms only, exact-gauge shapes, union, subtraction and intersection blends with their smooth forms), every
    // shape outside a scope composes by hard union, and every scope is one level deep, composes into the running answer
    // through a hard-union pop, and carries the same candidate scale. Inside a scope a hard union is the lesser
    // operand, a smooth union at least the lesser operand less k/4, and a subtraction or intersection at least the
    // accumulated operand, each within ScopeBlendRoundingRaws of that exact bound; so the scope's field is at least the
    // distance to the instance's geometry less the slack, the sum of those allowances.
    private static bool IsScopedUnionInstance(SdfInstanceRange instance, CompiledInstruction[] compiled, out FixedQ4816 slack, out FixedQ4816 candidateScale) {
        var depth = 0;
        var slackRaws = 0L;
        var popped = false;

        slack = FixedQ4816.Zero;
        candidateScale = FixedQ4816.Zero;

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
                        continue;
                    }
                case SdfOp.PopField: {
                        var scale = ((instruction.Data1Y > FixedQ4816.Zero) ? instruction.Data1Y : FixedQ4816.Zero);

                        if ((depth != 1) || (instruction.Blend != ((uint)SdfBlendOp.Union)) || (popped && (scale != candidateScale))) {
                            return false;
                        }

                        depth = 0;
                        popped = true;
                        candidateScale = scale;
                        continue;
                    }
                case SdfOp.ShapeBlend: {
                        if (instruction.Detail) {
                            continue;
                        }

                        if (depth == 0) {
                            if (instruction.Blend != ((uint)SdfBlendOp.Union)) {
                                return false;
                            }

                            continue;
                        }

                        switch ((SdfBlendOp)instruction.Blend) {
                            case SdfBlendOp.Union:
                            case SdfBlendOp.Subtraction:
                            case SdfBlendOp.Intersection: {
                                    continue;
                                }
                            case SdfBlendOp.SmoothUnion: {
                                    var k = FixedQ4816.Max(x: instruction.Data1X, y: SmoothRadiusMin);

                                    slackRaws = checked((slackRaws + (((k.Value + 3L) >> 2) + ScopeBlendRoundingRaws)));
                                    continue;
                                }
                            case SdfBlendOp.SmoothSubtraction:
                            case SdfBlendOp.SmoothIntersection: {
                                    slackRaws = checked((slackRaws + ScopeBlendRoundingRaws));
                                    continue;
                                }
                            default: {
                                    return false;
                                }
                        }
                    }
                default: {
                        return false;
                    }
            }
        }

        if (!popped || (depth != 0)) {
            return false;
        }

        slack = FixedQ4816.FromRawBits(value: slackRaws);

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
    // The segment table: for each ResetPoint opening a chain a walk may skip whole, the chain's end and one sphere
    // enclosing the program's reach sphere of every shape in it. A chain qualifies when it runs from its ResetPoint to
    // the next ResetPoint, instance boundary, scope edge or the stream's end through point transforms and shapes alone,
    // with no point read past a scope edge before the next reset (ClosesAtScopeEdge), and every
    // shape it evaluates carries a static reach sphere (the program's promise that the candidate is at least the
    // distance to it) and joins the answer by union, smooth union or subtraction. Each of those leaves the answer and
    // its material bitwise when its candidate is far enough: a union at or above the answer is not taken; a smooth
    // union at least k plus a few raws above it rounds h to one, so the lerp returns the answer and the k·h·(1 − h)
    // term is zero; a subtraction's negated candidate at or below the answer is not taken. So a chain whose sphere
    // clears the answer by its margin, and its negation too when it carves, leaves the answer as it was, shape after
    // shape. A chain never crosses an instance boundary, so a walk still meets every instance it tests in First order.
    private static (int[] Slots, SegmentBound[] Bounds) BuildSegments(SdfProgram program, CompiledInstruction[] compiled) {
        var count = compiled.Length;
        var slots = new int[count];
        var bounds = new List<SegmentBound>();
        var boundary = new bool[(count + 1)];
        var spheres = new List<SdfSkipSphere>();

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

            spheres.Clear();

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
                    var sphere = program.ShapeReachSphere(instruction: end);

                    if (!IsStaticSphere(sphere: sphere)) {
                        qualifies = false;
                        break;
                    }

                    switch ((SdfBlendOp)instruction.Blend) {
                        case SdfBlendOp.Union: {
                                break;
                            }
                        case SdfBlendOp.SmoothUnion: {
                                margin = FixedQ4816.Max(x: margin, y: (FixedQ4816.Max(x: instruction.Data1X, y: SmoothRadiusMin) + FixedQ4816.FromRawBits(value: ScopeBlendRoundingRaws)));
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

                    spheres.Add(item: sphere);
                }

                end++;
            }

            if (!qualifies || (spheres.Count == 0)) {
                continue;
            }

            slots[start] = bounds.Count;
            bounds.Add(item: SegmentBoundOf(end: end, spheres: spheres) with { Carves = carves, Margin = margin });
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
    // The sphere about the members' mean centre reaching every member sphere, in double, then padded and rounded the way
    // CullBoundOf pads an instance's radius.
    private static SegmentBound SegmentBoundOf(int end, List<SdfSkipSphere> spheres) {
        var (x, y, z) = (0.0, 0.0, 0.0);

        foreach (var sphere in spheres) {
            x += sphere.Center.X;
            y += sphere.Center.Y;
            z += sphere.Center.Z;
        }

        (x, y, z) = ((x / spheres.Count), (y / spheres.Count), (z / spheres.Count));

        var radius = 0.0;

        foreach (var sphere in spheres) {
            var (dx, dy, dz) = ((sphere.Center.X - x), (sphere.Center.Y - y), (sphere.Center.Z - z));

            radius = Math.Max(val1: radius, val2: (Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz))) + sphere.Radius));
        }

        return new SegmentBound(
            Carves: false,
            CenterX: FixedQ4816.FromDouble(value: x),
            CenterY: FixedQ4816.FromDouble(value: y),
            CenterZ: FixedQ4816.FromDouble(value: z),
            End: end,
            Margin: FixedQ4816.Zero,
            Radius: FixedQ4816.FromDouble(value: ((radius * CullBoundRadiusScale) + CullBoundRadiusPadding))
        );
    }
    // The reach a chain's sphere must clear to leave an answer bitwise: the answer plus the chain's margin, and the
    // answer's negation when it carves, taken in Int128 so neither wraps.
    private static Int128 SegmentThreshold(in SegmentBound bound, FixedQ4816 above, FixedQ4816 below) {
        var threshold = (((Int128)above.Value) + bound.Margin.Value);

        return (bound.Carves ? Int128.Max(x: threshold, y: -((Int128)below.Value)) : threshold);
    }
    // Whether a chain's sphere clears the running answer from a point, the largest axis distance tried before the root.
    private static bool PointClears(in SegmentBound bound, FixedVector3 worldPosition, FixedQ4816 resultDistance) {
        var (x, y, z) = ((worldPosition.X - bound.CenterX), (worldPosition.Y - bound.CenterY), (worldPosition.Z - bound.CenterZ));
        var threshold = SegmentThreshold(above: resultDistance, below: resultDistance, bound: in bound);

        return ((((Int128)(AxisDistance(x: x, y: y, z: z) - bound.Radius).Value) >= threshold) || LengthAtLeast(least: (threshold + bound.Radius.Value), x: x, y: y, z: z));
    }

    // One instance's conservative world-space bound, converted to FixedQ4816 ONCE at construction — the fixed-point,
    // safety-padded twin of SdfInstanceRange's float Center/Radius, widened by a scoped instance's blend slack.
    // CandidateScale is the scope's pop scale, or zero for a candidate taken as it is; a scoped instance's members are
    // m_members[MemberFirst, MemberFirst + MemberCount), none when MemberCount is zero.
    private readonly record struct CullBound(
        int First,
        int End,
        FixedQ4816 CenterX,
        FixedQ4816 CenterY,
        FixedQ4816 CenterZ,
        FixedQ4816 Radius,
        FixedQ4816 CandidateScale,
        FixedQ4816 Slack,
        int MemberFirst,
        int MemberCount
    );
    // One member shape's padded fixed-point reach sphere, and whether it sits inside the instance's scope.
    private readonly record struct MemberSphere(FixedQ4816 CenterX, FixedQ4816 CenterY, FixedQ4816 CenterZ, FixedQ4816 Radius, bool Scoped);
    // One skippable chain: the instruction after its last, its padded fixed-point sphere, the margin a smooth union in it
    // needs above the running answer to leave it bitwise, and whether a subtraction in it also needs the sphere to
    // clear the answer's negation.
    private readonly record struct SegmentBound(int End, FixedQ4816 CenterX, FixedQ4816 CenterY, FixedQ4816 CenterZ, FixedQ4816 Radius, FixedQ4816 Margin, bool Carves);
}
