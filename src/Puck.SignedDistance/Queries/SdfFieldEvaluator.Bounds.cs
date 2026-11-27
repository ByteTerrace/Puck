using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE INTERVAL INTERPRETER: a third reading of the same compiled stream TryDistance walks, over a box of points instead
// of one. Every point step is a faithful rounding of an exact expression (one raw or less from it, on the Q16 grid), and
// each step here encloses that exact expression over the box with outward rounding, so the box's interval holds every
// TryDistance answer inside it: by induction over the stream, each point value lies in its interval, and a faithful
// rounding of a value inside a grid-ended interval stays inside it. Where the point code branches, the interval takes
// every branch the box can reach and joins them. Each rule mirrors its TryDistance or EvaluateShape step; a step changed
// there is a rule changed here, and SdfFieldBoundsLawTests holds every op, shape and blend's point answers inside these
// bounds. Two rules enclose rather than mirror: Sweep encloses whatever parameter its point search picks, and a
// Superellipsoid of any exponent but two raises through the interval power. A shape with no rule answers the unbounded
// interval, never an approximation, so it can only shrink the frame.
//
// Every rule reads an instruction's parameters as their enclosures (CompiledInstruction's THE PARAMETER ENCLOSURES), so
// under the render reading a bound also holds the field whose parameters are the packed floats the kernels evaluate:
// each rule encloses its exact expression over every parameter its enclosures hold, which includes both the raw the point
// walk reads and the float. A rule that tests a parameter's value (an identity turn, a unit scale, a branch on a lane)
// tests its enclosure, and the affine reading carries a lane off the grid as noise or takes the interval rule's answer.
public sealed partial class SdfFieldEvaluator {
    private static readonly FixedQ4816 SuperellipsoidSphereExponent = FixedQ4816.FromInteger(value: 2L);
    // The sweep's parameter runs over the whole curve: every closest parameter the point search can pick lies in it.
    private static readonly FixedInterval SweepParameter = new(lower: FixedQ4816.Zero, upper: FixedQ4816.One);
    // A unit vector's component, and a cross product of two unit vectors' component, each rounded: inside [−2, 2].
    private static readonly FixedInterval SweepFrameComponent = new(lower: FixedQ4816.FromInteger(value: -2L), upper: FixedQ4816.FromInteger(value: 2L));
    // A sine or cosine: SinCos answers inside [−1, 1] for every angle.
    private static readonly FixedInterval UnitRange = new(lower: -FixedQ4816.One, upper: FixedQ4816.One);

    // The raws by which a fixed-point rotation can miss its exact linear image (see RotateBoundsByInverseQuaternion).
    private const long LinearRotationSlackRaws = 4L;

    // The frame's half-width in raws (see Frame), found once at construction.
    private readonly long m_frameRaw;

    // Per thread, the reach of every cullable instance from the box being walked, filled once per walk.
    [ThreadStatic]
    private static FixedQ4816[]? BoxReachScratch;

    // Per instruction, the slot of its RotationBounds in m_rotationBounds; minus one for every other op.
    private readonly int[] m_rotationSlots;
    private readonly RotationBounds[] m_rotationBounds;

    /// <summary>Encloses every distance <see cref="TryDistance(FixedPosition, out FixedQ4816, out int)"/> answers at a point of the box
    /// <c>[<paramref name="lower"/>, <paramref name="upper"/>]</c>, corners included.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner, at or above <paramref name="lower"/> on every axis.</param>
    /// <param name="distance">The enclosing interval on success; zero on failure.</param>
    /// <returns><see langword="false"/> when the program has no shape or a corner leaves the evaluator's frame, as
    /// <see cref="TryDistance(FixedPosition, out FixedQ4816, out int)"/> refuses; <see langword="true"/> otherwise.</returns>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/> on an
    /// axis.</exception>
    /// <remarks>The interval is certified, not estimated: its lower endpoint is at or below the least point answer in the
    /// box and its upper at or above the greatest. It is a conservative hull, so it widens with the box, and a rotation
    /// or a fold that reuses a coordinate widens it further; a smaller box answers tighter.</remarks>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) =>
        TryDistanceBounds(
            distance: out distance,
            instructionsWalked: out _,
            lower: lower,
            upper: upper
        );
    /// <summary>Encloses every distance <see cref="TryDistance(FixedPosition, out FixedQ4816, out int)"/> answers at a point of the box, and counts the work: the
    /// instructions the walk visited, after the instance cull skipped every instance the box cannot reach.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner, at or above <paramref name="lower"/> on every axis.</param>
    /// <param name="distance">The enclosing interval on success; zero on failure.</param>
    /// <param name="instructionsWalked">The instructions the walk visited; zero on failure.</param>
    /// <returns><see langword="false"/> when the program has no shape or a corner leaves the evaluator's frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/> on an
    /// axis.</exception>
    /// <remarks>The walk skips an instance or chain of shapes only where a certificate proves the lower end of every
    /// interval it could join lies at or beyond the running interval's upper end, or the nearest instance's, so the skip
    /// leaves the interval exactly as walking every instruction answers it. An evaluator that indexes regions
    /// (<see cref="IndexesRegions"/>) walks the box in parts, each over its own region's part of the program, and answers
    /// the hull of the parts: an enclosure as certain and a function of the program and the box alone, which need not be
    /// the interval an evaluator without the index answers.</remarks>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance, out int instructionsWalked) =>
        TryDistanceBounds(distance: out distance, instructionsWalked: out instructionsWalked, lower: lower, rotationsExpanded: out _, upper: upper);
    /// <summary>Encloses the box and counts visited instructions and full interval rotation expansions.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner.</param>
    /// <param name="distance">The enclosing interval on success.</param>
    /// <param name="instructionsWalked">The instructions visited after culling.</param>
    /// <param name="rotationsExpanded">The rotations evaluated through the full interval formula.</param>
    /// <returns>Whether the field can enclose this box.</returns>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance, out int instructionsWalked, out int rotationsExpanded) =>
        TryDistanceBounds(distance: out distance, instructionsWalked: out instructionsWalked, lower: lower, reading: SdfBoundsReading.Interval, rotationsExpanded: out rotationsExpanded, upper: upper);
    /// <summary>Encloses every distance <see cref="TryDistance(FixedPosition, out FixedQ4816, out int)"/> answers at a point of the box, in a chosen
    /// reading: the interval rules alone, or those rules met at every step with the affine reading (SdfFormula's affine
    /// lowering), which carries the box's three axes as shared symbols so a rotated box stays rotated and a blend reads
    /// its operands' difference narrow.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner, at or above <paramref name="lower"/> on every axis.</param>
    /// <param name="reading">The reading: <see cref="SdfBoundsReading.Affine"/> answers an interval inside the one
    /// <see cref="SdfBoundsReading.Interval"/> answers, at more work an instruction.</param>
    /// <param name="distance">The enclosing interval on success; zero on failure.</param>
    /// <param name="instructionsWalked">The instructions the walk visited; zero on failure.</param>
    /// <returns><see langword="false"/> when the program has no shape or a corner leaves the evaluator's frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/> on an
    /// axis.</exception>
    /// <remarks>Both readings are certified: each step's affine form and its interval each hold every point answer, so
    /// their meet does. An evaluator that indexes regions (<see cref="IndexesRegions"/>) answers its interval reading
    /// whichever reading is asked.</remarks>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, SdfBoundsReading reading, out FixedInterval distance, out int instructionsWalked) =>
        TryDistanceBounds(distance: out distance, instructionsWalked: out instructionsWalked, lower: lower, reading: reading, rotationsExpanded: out _, upper: upper);

    private bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, SdfBoundsReading reading, out FixedInterval distance, out int instructionsWalked, out int rotationsExpanded) {
        distance = FixedInterval.FromPoint(value: FixedQ4816.Zero);
        instructionsWalked = 0;
        rotationsExpanded = 0;

        if (
            !m_hasShape ||
            !lower.TryDelta(delta: out var lowerPoint, origin: FixedPosition.Zero) ||
            !upper.TryDelta(delta: out var upperPoint, origin: FixedPosition.Zero)
        ) {
            return false;
        }

        if ((lowerPoint.X > upperPoint.X) || (lowerPoint.Y > upperPoint.Y) || (lowerPoint.Z > upperPoint.Z)) {
            throw new ArgumentException(message: $"The box's lower corner {lowerPoint} exceeds its upper corner {upperPoint}.", paramName: nameof(lower));
        }

        // A box reaching outside the frame holds points TryDistance refuses, so the bounds refuse it as well.
        if (!IsInFrame(point: lowerPoint) || !IsInFrame(point: upperPoint)) {
            return false;
        }

        var world = new IntervalVector3(
            X: new FixedInterval(lower: lowerPoint.X, upper: upperPoint.X),
            Y: new FixedInterval(lower: lowerPoint.Y, upper: upperPoint.Y),
            Z: new FixedInterval(lower: lowerPoint.Z, upper: upperPoint.Z)
        );

        // A stream too short to divide a region has nothing an index could drop.
        distance = (((m_regions is null) || (m_instructions.Length <= RegionShortTape))
            ? BoundsOver(affine: (reading == SdfBoundsReading.Affine), instructionsWalked: out instructionsWalked, rotationsExpanded: out rotationsExpanded, world: world)
            : BoundsOverRegions(instructionsWalked: out instructionsWalked, rotationsExpanded: out rotationsExpanded, world: world));

        return true;
    }

    /// <summary>Gets the evaluator's frame: the half-width, in each world axis, of the cube about the origin inside
    /// which <see cref="TryDistance(FixedPosition, out FixedQ4816, out int)"/> answers and outside which it refuses. Inside it, no step of the evaluation can
    /// leave the carrier. Negative when no position can be answered.</summary>
    /// <remarks>Found once, at construction, by the bounds interpreter: the frame is the widest power-of-two cube over
    /// which the bounds of the whole program stay bounded. A bounded interval proves that no step of the interval walk
    /// left the carrier, and every point step that reads the position lies inside its step's interval, so none can wrap
    /// inside the cube. Every instruction has an inclusion rule; one without would answer the unbounded interval, which
    /// can only shrink the frame, never widen it.</remarks>
    public FixedQ4816 Frame => FixedQ4816.FromRawBits(value: m_frameRaw);

    // Whether every axis of a point lies inside the frame (the carrier's minimum, whose magnitude has no raw, never does).
    private bool IsInFrame(FixedVector3 point) =>
        (WithinRaw(raw: point.X.Value, limit: m_frameRaw) && WithinRaw(raw: point.Y.Value, limit: m_frameRaw) && WithinRaw(raw: point.Z.Value, limit: m_frameRaw));
    private static bool WithinRaw(long raw, long limit) =>
        ((raw != long.MinValue) && (Math.Abs(value: raw) <= limit));
    // The widest power-of-two cube whose bounds stay bounded, capped so the cull's subtraction of each certificate's
    // centre stays inside the carrier; minus one raw when not even a one-raw cube is bounded. Every instruction joins the
    // proof: one whose bounds were ever missing would answer the unbounded interval, which shrinks the frame, and never
    // grants one.
    private static long FindFrame(SdfFieldEvaluator evaluator) {
        var centre = 0UL;

        foreach (var bound in evaluator.m_cullBounds) {
            centre = Math.Max(val1: centre, val2: CentreMagnitude(reach: in bound.Reach));
        }

        foreach (var bound in evaluator.m_segmentBounds) {
            centre = Math.Max(val1: centre, val2: CentreMagnitude(reach: in bound.Reach));
        }

        foreach (var member in evaluator.m_members) {
            centre = Math.Max(val1: centre, val2: CentreMagnitude(reach: in member));
        }

        var ceiling = ((centre >= ((ulong)long.MaxValue)) ? 0L : (long.MaxValue - ((long)centre)));
        var frame = -1L;
        var low = 0;
        var high = 62;

        // Bounded over a cube implies bounded over every cube inside it (the bounds are inclusion isotonic), so the
        // bounded cubes are an initial run of the powers of two.
        while (low <= high) {
            var middle = ((low + high) / 2);
            var radius = Math.Min(val1: (1L << middle), val2: ceiling);
            var side = new FixedInterval(lower: FixedQ4816.FromRawBits(value: -radius), upper: FixedQ4816.FromRawBits(value: radius));

            if (!evaluator.BoundsOver(cull: false, instructionsWalked: out _, rotationsExpanded: out _, world: new IntervalVector3(X: side, Y: side, Z: side)).IsUnbounded) {
                frame = radius;
                low = (middle + 1);
            } else {
                high = (middle - 1);
            }
        }

        return frame;
    }
    // This thread's reach scratch, long enough to hold a reach for every cullable instance of this evaluator.
    private FixedQ4816[] BoxReaches() => (((BoxReachScratch is { } scratch) && (scratch.Length >= m_cullBounds.Length))
        ? scratch
        : (BoxReachScratch = CreateReachScratch(length: m_cullBounds.Length, work: SdfBoundsCache.ProcessWork, arrays: SdfBoundsCache.ScratchArraysCreated, bytes: SdfBoundsCache.ScratchPayloadCreated)));
    // The bounds over a box whose every point the frame holds, and the instructions the walk visited. A culling walk
    // skips what the cull tables prove cannot change its interval (SdfFieldEvaluator.Cull.cs); the frame's own walks
    // skip nothing, so a frame rests on every instruction.
    private FixedInterval BoundsOver(IntervalVector3 world, out int instructionsWalked, out int rotationsExpanded, bool cull = true, bool affine = false) {
        var cullBounds = m_cullBounds;
        var reaches = BoxReaches();
        var primer = BoxPrimer.None;
        var walked = 0;
        var query = new BoxQuery(box: world);

        rotationsExpanded = 0;

        if (cull) {
            SdfBoundsCache.ProcessWork.Add(kind: SdfBoundsCache.PrepassBounds, amount: cullBounds.Length);
            for (var bound = 0; (bound < cullBounds.Length); bound++) {
                RankBoxReach(bound: bound, primer: ref primer, query: in query, reaches: reaches);
            }
        }

        var result = WalkBounds(
            affine: affine,
            cull: cull,
            end: m_instructions.Length,
            first: 0,
            primer: ref primer,
            reaches: (cull ? reaches.AsSpan(start: 0, length: cullBounds.Length) : []),
            query: in query,
            rotationsExpanded: ref rotationsExpanded,
            walked: ref walked,
            world: world
        );

        instructionsWalked = walked;

        return result;
    }
    // Files one cullable instance's cheap reach from a box, and takes it as the primer's candidate when it is the least
    // so far and the program is primeable. A regional walk files the point certificate's reach over the box.
    private void RankBoxReach(int bound, in BoxQuery query, FixedQ4816[] reaches, ref BoxPrimer primer, bool regional = false) {
        ref readonly var reach = ref m_cullBounds[bound].Reach;

        // An instance with no box certificate is never skipped, and its key only orders the primer's choice.
        reaches[bound] = FixedQ4816.FromRawBits(value: (regional
            ? reach.CheapRegionReach(query: in query)
            : (reach.Boxed ? reach.CheapBoxReach(query: in query) : reach.CheapPointReach(point: Corner(box: query.Box)))));

        if (m_primeable && ((primer.Candidate < 0) || (reaches[bound] < reaches[primer.Candidate]))) {
            primer.Candidate = bound;
        }
    }
    // Walks [first, end) over a box from the far seed, the box form of WalkPoints: with reaches it skips each cullable
    // instance whose candidates' lower ends clear the running upper end or the primer's, walking the primer on its own
    // first when the running upper end clears nothing, and joins the primer as the bounds already walked; a culling walk
    // also skips each chain whose candidates' lower ends clear the running upper end by its margin. With a tape it walks
    // the tape's ranges in stream order in place of [first, end). With a record it is a regional walk
    // (SdfFieldEvaluator.Regions.cs): it lists every range of the stream it did not jump over, and it jumps where the
    // point certificates clear at every point of the box, the primer's clearance held strict, in place of the box ones.
    //
    // With affine set, the walk carries the affine reading beside every interval it holds (SdfFormula's affine lowering:
    // the generated FixedAffineForm overloads of MapPoint, EvaluateShape, EditField and BlendShape): the point as forms
    // over the box's three axes, the running distance and each open scope's as forms. Every form and its interval hold
    // the same point values, so each step meets the two (Meet) and both readings walk on from the tighter. A step the
    // affine reading has no rule for lifts its interval as fresh noise, and the culls and skips stay the interval's, read
    // from the met interval. The primer's walk and a regional walk read intervals alone.
    private FixedInterval WalkBounds(int first, int end, IntervalVector3 world, in BoxQuery query, ReadOnlySpan<FixedQ4816> reaches, ref BoxPrimer primer, bool cull, ref int walked, ref int rotationsExpanded, ReadOnlySpan<int> tape = default, List<(int First, int End)>? record = null, bool nested = false, bool affine = false) {
        var visited = 0;
        var expanded = 0;
        var cullBounds = m_cullBounds;
        var segmentSlots = m_segmentSlots;
        var segmentBounds = m_segmentBounds;
        var local = world;
        var distanceScale = Point(value: FixedQ4816.One);
        var frames = m_rootFrames.Span;
        var root = new PointFrame<BoxPoint, FixedInterval>();
        var result = FixedInterval.FromPoint(value: FarDistance);
        var affineWorld = (affine ? FixedAffineForm3.FromBox(lower: new FixedVector3(X: world.X.Lower, Y: world.Y.Lower, Z: world.Z.Lower), upper: new FixedVector3(X: world.X.Upper, Y: world.Y.Upper, Z: world.Z.Upper)) : default);
        var affineLocal = affineWorld;
        var affineResult = FixedAffineForm.FromPoint(value: FarDistance);
        var affinePower = new PowerWork();
        Span<FixedAffineForm> affineScopes = stackalloc FixedAffineForm[(affine ? SdfProgramBuilder.MaxFieldScopeDepth : 0)];
        // A pending LaneErode's effect on the next shape (WalkPoints' laneErodeSkip and laneErodeAmount): skipped, or
        // eroded by an interval of its reach; cleared by ResetPoint.
        var laneErodeSkip = false;
        var laneErodeMaySkip = false;
        var laneErodeAmount = FixedInterval.FromPoint(value: FixedQ4816.Zero);
        Span<FixedInterval> scopes = stackalloc FixedInterval[SdfProgramBuilder.MaxFieldScopeDepth];
        var scopeDepth = 0;
        var cullIndex = (reaches.IsEmpty ? cullBounds.Length : 0);
        var tapeAt = 0;
        var regional = (record is not null);
        var test = new BoxReachTest(query: query, regional: regional);

        if (!tape.IsEmpty) {
            (first, end, tapeAt) = (tape[0], tape[1], 2);
        }

        // The start of the stretch the walk is reading: every jump a certificate grants closes it into the record.
        var live = first;
        // A regional walk's chain, from its ResetPoint, and whether every instruction read in it so far leaves the
        // accumulator of every point of the box as it was (RegionJoinClears). A chain inert to its end is left out of
        // the record: its shapes need no certificate, since the walk has enclosed their candidates over the box.
        var chain = -1;
        var inert = false;
        // The live stretch and the record's length as the chain opened. While the chain is inert each of its joins is
        // left out of the record as it is read (an inert join changes no point walk whatever follows it), and a chain
        // inert to its end takes its record back to these and leaves out whole.
        var chainLive = first;
        var chainMark = 0;

        for (var index = first; NextLive(at: ref tapeAt, end: ref end, index: ref index, tape: tape); index++) {
            // A tape can open past an instance's first instruction, which then meets no instance test.
            while ((cullIndex < cullBounds.Length) && (cullBounds[cullIndex].First < index)) {
                cullIndex++;
            }

            if (
                (cullIndex < cullBounds.Length) &&
                (cullBounds[cullIndex].First == index)
            ) {
                var bound = cullIndex++;

                if (inert) {
                    record!.RemoveRange(count: (record.Count - chainMark), index: chainMark);
                    record.Add(item: (chainLive, chain));
                    live = index;
                    inert = false;
                }

                if (bound == primer.Bound) {
                    result = FixedInterval.Min(first: result, second: primer.Bounds);
                    if (affine) { affineResult = Meet(form: FixedAffineForms.Min(a: affineResult, b: FixedAffineForm.FromInterval(interval: primer.Bounds)), interval: ref result); }
                    record?.Add(item: (live, index));
                    live = cullBounds[bound].End;
                    index = (cullBounds[bound].End - 1);

                    continue;
                }

                if (BoxInstanceClears(bound: in cullBounds[bound], cheapReach: reaches[bound], regional: regional, test: in test, threshold: primer.Clearance(strict: regional, upper: result.Upper))) {
                    record?.Add(item: (live, index));
                    live = cullBounds[bound].End;
                    index = (cullBounds[bound].End - 1);

                    continue;
                }

                if ((primer.Bound < 0) && (primer.Candidate >= 0) && (primer.Candidate != bound)) {
                    var none = BoxPrimer.None;

                    primer.Bound = primer.Candidate;
                    primer.Bounds = WalkBounds(
                        cull: true,
                        end: cullBounds[primer.Bound].End,
                        first: cullBounds[primer.Bound].First,
                        primer: ref none,
                        reaches: [],
                        query: in query,
                        nested: true,
                        record: record,
                        rotationsExpanded: ref expanded,
                        walked: ref visited,
                        world: world
                    );

                    if (BoxInstanceClears(bound: in cullBounds[bound], cheapReach: reaches[bound], regional: regional, test: in test, threshold: primer.Clearance(strict: regional, upper: result.Upper))) {
                        record?.Add(item: (live, index));
                        live = cullBounds[bound].End;
                        index = (cullBounds[bound].End - 1);

                        continue;
                    }
                }

                if (bound == primer.Candidate) {
                    primer.Candidate = -1;
                }
            }

            if (root.Captures(index: index)) {
                root.Capture(index: index, point: new BoxPoint(Affine: affineLocal, Box: local), scale: distanceScale);
            }
            ref readonly var instruction = ref m_instructions[index];

            ++visited;

            switch (instruction.IntervalAction) {
                case SdfIntervalAction.ResetPoint: {
                        local = world;
                        affineLocal = affineWorld;
                        distanceScale = Point(value: FixedQ4816.One);
                        laneErodeSkip = false;
                        laneErodeMaySkip = false;
                        laneErodeAmount = FixedInterval.FromPoint(value: FixedQ4816.Zero);

                        if (inert) {
                            record!.RemoveRange(count: (record.Count - chainMark), index: chainMark);
                            record.Add(item: (chainLive, chain));
                            live = index;
                        }

                        var segment = segmentSlots[index];
                        var skipped = ((!cull || (segment < 0))
                            ? -1
                            : (regional
                                ? SkippedChainsEnd(above: result.Upper, below: result.Lower, segment: segment, test: in test)
                                : ((segmentBounds[segment].Reach.Boxed && test.Clears(reach: in segmentBounds[segment].Reach, threshold: SegmentThreshold(above: result.Upper, below: result.Lower, bound: in segmentBounds[segment]))) ? segmentBounds[segment].End : -1)));

                        chain = index;
                        inert = (regional && (skipped < 0));

                        chainLive = live;
                        chainMark = (record?.Count ?? 0);

                        if (skipped >= 0) {
                            record?.Add(item: (live, index));
                            live = skipped;
                            index = (skipped - 1);
                        } else if (!frames.IsEmpty) {
                            var restored = new BoxPoint(Affine: affineLocal, Box: local);

                            root.Reset(frame: frames[index], index: ref index, point: ref restored, scale: ref distanceScale);
                            (local, affineLocal) = (restored.Box, restored.Affine);
                        }

                        break;
                    }
                case SdfIntervalAction.Translate: {
                        var translation = instruction.BoundsVector;

                        local = new(
                            X: (local.X - translation.X),
                            Y: (local.Y - translation.Y),
                            Z: (local.Z - translation.Z)
                        );
                        if (affine) { affineLocal = Meet(box: ref local, point: (instruction.ExactData0 ? MapPoint(instruction: in instruction, p: affineLocal) : FixedAffineForms.Subtract(a: affineLocal, b: Lift(box: translation)))); }
                        break;
                    }
                case SdfIntervalAction.Rotate: {
                        if (TryAxialRotationBounds(instruction: instruction, p: local, rotated: out var axial)) {
                            local = axial;
                        } else {
                            expanded++;
                            local = RotateBoundsByInverseQuaternion(p: local, rotation: in m_rotationBounds[m_rotationSlots[index]]);
                        }
                        // The point walk takes the identity, either sign of it, as the point itself; a quaternion off the grid
                        // is no identity, whatever raws it rounds to.
                        if (affine && !(instruction.ExactData0 && IsIdentityRotation(instruction: in instruction))) { affineLocal = Meet(box: ref local, point: RotateAffine(instruction: in instruction, p: affineLocal, rotation: in m_rotationBounds[m_rotationSlots[index]])); }
                        break;
                    }
                case SdfIntervalAction.Scale: {
                        // A unit scale divides by one and multiplies by one, both exact on a point interval.
                        if (instruction.ExactData0 && IsUnitScale(instruction: in instruction)) {
                            break;
                        }

                        var scale = instruction.BoundsVector;

                        local = new(
                            X: (local.X / scale.X),
                            Y: (local.Y / scale.Y),
                            Z: (local.Z / scale.Z)
                        );
                        distanceScale *= instruction.Bounds0W;
                        // Off the grid, each axis is the point times its reciprocal's interval: the symbols carry at its
                        // centre, and its half-width times the axis's reach is noise.
                        if (affine) {
                            var one = Point(value: FixedQ4816.One);

                            affineLocal = Meet(box: ref local, point: (instruction.ExactData0
                                ? MapPoint(instruction: in instruction, p: affineLocal)
                                : FixedAffineForms.MultiplyComponents(a: affineLocal, b: Lift(box: new IntervalVector3(X: (one / scale.X), Y: (one / scale.Y), Z: (one / scale.Z))))));
                        }
                        break;
                    }
                case SdfIntervalAction.Fold: {
                        local = FoldBounds(instruction: in instruction, local: local);
                        if (affine) { affineLocal = (instruction.Exact ? Meet(box: ref local, point: MapPoint(instruction: in instruction, p: affineLocal)) : Lift(box: local)); }
                        break;
                    }
                case SdfIntervalAction.Warp: {
                        local = WarpBounds(distanceScale: ref distanceScale, instruction: in instruction, local: local);
                        if (affine) { affineLocal = Lift(box: local); }
                        break;
                    }
                case SdfIntervalAction.RenderWarp: {
                        local = RenderWarpBounds(distanceScale: ref distanceScale, instruction: in instruction, local: local);
                        if (affine) { affineLocal = Lift(box: local); }
                        break;
                    }
                case SdfIntervalAction.LaneErode: {
                        (laneErodeSkip, laneErodeMaySkip, laneErodeAmount) = LaneErodeBounds(instruction: in instruction);
                        inert = false;
                        break;
                    }
                case SdfIntervalAction.EditField: {
                        var edited = EditFieldBounds(instruction: in instruction, local: local, result: result);

                        if (affine) { affineResult = (instruction.Exact ? Meet(form: EditField(distance: affineResult, instruction: in instruction, p: affineLocal), interval: ref edited) : FixedAffineForm.FromInterval(interval: edited)); }
                        result = edited;
                        inert = false;
                        break;
                    }
                case SdfIntervalAction.RenderEditField: {
                        result = RenderEditFieldBounds(instruction: in instruction, result: result);
                        if (affine) { affineResult = FixedAffineForm.FromInterval(interval: result); }
                        inert = false;
                        break;
                    }
                case SdfIntervalAction.WeightedRelief: {
                        result = NoiseDisplaceBounds(instruction: in instruction, local: local, result: result, weight: ((instruction.ReliefMasks is { } masks) ? MaskProductBounds(masks: masks, q: local) : null));
                        if (affine) { affineResult = FixedAffineForm.FromInterval(interval: result); }
                        inert = false;
                        break;
                    }
                case SdfIntervalAction.Identity: {
                        // Data for the following relief.
                        break;
                    }
                case SdfIntervalAction.PushField: {
                        // A scope's accumulator is its own: no chain spans a push or a pop.
                        if (affine) {
                            affineScopes[scopeDepth] = affineResult;
                            affineResult = FixedAffineForm.FromPoint(value: FarDistance);
                        }
                        scopes[scopeDepth++] = result;
                        result = FixedInterval.FromPoint(value: FarDistance);
                        chain = -1;
                        inert = false;
                        break;
                    }
                case SdfIntervalAction.PopField: {
                        var popped = PopFieldBounds(candidate: result, instruction: instruction, saved: scopes[--scopeDepth]);

                        if (affine && instruction.Exact) {
                            var scoped = ((instruction.ScopeScale > 0L) ? ScaleScope(candidate: affineResult, scale: instruction.ScopeScale) : affineResult);

                            affineResult = Meet(form: BlendShape(blend: instruction.Blend, candidate: scoped, current: affineScopes[scopeDepth], operand: PopOperand(instruction: in instruction), smoothRadius: instruction.Data1X), interval: ref popped);
                        } else if (affine) {
                            affineResult = FixedAffineForm.FromInterval(interval: popped);
                        }
                        result = popped;
                        chain = -1;
                        inert = false;
                        break;
                    }
                case SdfIntervalAction.ShapeBlend: {
                        var erosion = laneErodeAmount;
                        var skipped = laneErodeSkip;
                        var maySkip = laneErodeMaySkip;

                        laneErodeSkip = false;
                        laneErodeMaySkip = false;
                        laneErodeAmount = FixedInterval.FromPoint(value: FixedQ4816.Zero);

                        if (instruction.Detail || skipped) {
                            break;
                        }

                        var shape = ShapeBounds(instruction: instruction, p: local);
                        var affineCandidate = FixedAffineForm.Entire;

                        // A shape whose lanes, side table or scope scale lie off the grid takes its interval rule's candidate.
                        if (affine && instruction.Exact) {
                            var affineShape = Meet(form: EvaluateShape(instruction: in instruction, p: affineLocal, powerWork: ref affinePower), interval: ref shape);

                            // The point walk's (shape · distanceScale) + erosion, then the scope's fraction.
                            affineCandidate = FixedAffineForms.Add(a: Multiply(form: affineShape, scale: distanceScale), b: FixedAffineForm.FromInterval(interval: erosion));
                            if (instruction.ScopeScale > 0L) { affineCandidate = ScaleScope(candidate: affineCandidate, scale: instruction.ScopeScale); }
                        }

                        var candidate = ScaleCandidateBounds(candidate: ((shape * distanceScale) + erosion), instruction: in instruction);

                        if (affine) { affineCandidate = Meet(form: affineCandidate, interval: ref candidate); }

                        // An inert join, its candidate past the accumulator, leaves every point's accumulator as it was,
                        // so the interval that encloses them stands as it is, tighter than the blend's rule would
                        // leave it.
                        if (regional && RegionJoinClears(farther: candidate, instruction: in instruction, nearer: result, strictly: false)) {
                            if (inert) {
                                record!.Add(item: (live, index));
                                live = (index + 1);
                            }

                            break;
                        }

                        // The chain's first join that is not inert, where every join before it in the chain is out of
                        // the record: a walk that entered the stream at the chain's ResetPoint arrives here holding
                        // the far seed. Where the candidate stands below both the accumulator and the far seed by the
                        // blend's margin, that walk and the whole stream's leave with the candidate and its material,
                        // so nothing before the chain reaches any later instruction. A chain that has read anything
                        // else (a join of another blend, a field edit, a scope) arrives holding what a walk from its
                        // ResetPoint does not, and takes nothing over; the primer's own walk holds its instance's
                        // accumulator, not the root's, and a scope holds its own.
                        var opens = inert;

                        inert = false;

                        if (
                            opens && !maySkip && !nested && (scopeDepth == 0) &&
                            RegionJoinClears(farther: result, instruction: in instruction, nearer: candidate, strictly: true) &&
                            RegionJoinClears(farther: FixedInterval.FromPoint(value: FarDistance), instruction: in instruction, nearer: candidate, strictly: true)
                        ) {
                            var kept = 0;

                            for (var entry = 0; (entry < record!.Count); entry++) {
                                if (record[entry].End > chain) {
                                    record[kept++] = (Math.Max(val1: record[entry].First, val2: chain), record[entry].End);
                                }
                            }

                            record.RemoveRange(count: (record.Count - kept), index: kept);
                            live = Math.Max(val1: live, val2: chain);
                        }

                        var blended = BlendBounds(blend: instruction.Blend, candidate: candidate, current: result, smoothRadius: instruction.Bounds1X);

                        if (affine) {
                            affineResult = (instruction.ExactData1X
                                ? Meet(form: BlendShape(blend: instruction.Blend, candidate: affineCandidate, current: affineResult, operand: FixedQ4816.Zero, smoothRadius: instruction.Data1X), interval: ref blended)
                                : FixedAffineForm.FromInterval(interval: blended));
                        }
                        // A LaneErode whose lanes lie off the grid may skip the shape: the accumulator either way.
                        if (maySkip) {
                            blended = FixedInterval.Union(first: result, second: blended);
                            if (affine) { affineResult = FixedAffineForm.FromInterval(interval: blended); }
                        }
                        result = blended;
                        break;
                    }
                case SdfIntervalAction.Unsupported: {
                        ThrowNoInclusionRule(op: instruction.Op);
                        break;
                    }
                default:
                    throw new InvalidOperationException(message: "Invalid Interval action.");
            }
        }

        walked += visited;
        rotationsExpanded += expanded;

        if (inert) {
            record!.RemoveRange(count: (record.Count - chainMark), index: chainMark);
            record.Add(item: (chainLive, chain));
            live = int.MaxValue;
        }

        // A walk over a tape can close past a gap of its tape; the region's build takes the record within the tape.
        record?.Add(item: (live, (tape.IsEmpty ? end : int.MaxValue)));

        return result;
    }
    // Whether one operand of a shape's join stands past the other at every point of a box by enough that the blend
    // returns the nearer one to the bit, with its material: at or past it for a union, and past it by the identity margin
    // for a smooth union, whose weight then rounds to exactly one or exactly zero. A union returns the accumulator on a
    // tie, so the candidate is the nearer one only when it is strictly nearer. No other blend is judged.
    //
    // The margin on the candidate's side: for b − a ≤ −(k + ⌊k·2⁻¹⁵⌋ + 16) raws, ½·(b − a) rounds to at most
    // −(k + ⌊k·2⁻¹⁵⌋ + 15)/2, whose quotient by k is below −½ before it rounds, so it rounds to −½ or less however the
    // division rounds, and the weight clamps to zero.
    private static bool RegionJoinClears(in CompiledInstruction instruction, FixedInterval nearer, FixedInterval farther, bool strictly) {
        if (nearer.IsUnbounded || farther.IsUnbounded) {
            return false;
        }

        var clearance = (((Int128)farther.Lower.Value) - nearer.Upper.Value);

        return (SdfFieldBlends.CullGuardOf(value: ((SdfBlendOp)instruction.Blend)) switch {
            SdfCullGuardAction.Union => (strictly ? (clearance > 0) : (clearance >= 0)),
            SdfCullGuardAction.SmoothUnion => (clearance >= SmoothUnionIdentityMargin(smoothRadius: SmoothRadiusReach(instruction: in instruction))),
            _ => false,
        });
    }
    // The box folds, the cold half of WalkBounds' switch, kept out of it so its hot cases inline their helpers.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IntervalVector3 FoldBounds(in CompiledInstruction instruction, IntervalVector3 local) {
        switch (SdfFieldOps.IntervalFoldOf(value: instruction.Op)) {
            case SdfIntervalFoldAction.Repeat: {
                    var spacing = instruction.BoundsVector;
                    var inverseSpacing = instruction.BoundsVector1;

                    return new(
                        X: (local.X - (spacing.X * FixedInterval.Round(value: (local.X * inverseSpacing.X)))),
                        Y: (local.Y - (spacing.Y * FixedInterval.Round(value: (local.Y * inverseSpacing.Y)))),
                        Z: (local.Z - (spacing.Z * FixedInterval.Round(value: (local.Z * inverseSpacing.Z))))
                    );
                }
            case SdfIntervalFoldAction.RepeatLimited: {
                    var spacing = instruction.BoundsVector;
                    var limit = instruction.BoundsVector1;

                    return new(
                        X: (local.X - (spacing.X * FixedInterval.Clamp(value: FixedInterval.Round(value: (local.X / spacing.X)), minimum: -limit.X, maximum: limit.X))),
                        Y: (local.Y - (spacing.Y * FixedInterval.Clamp(value: FixedInterval.Round(value: (local.Y / spacing.Y)), minimum: -limit.Y, maximum: limit.Y))),
                        Z: (local.Z - (spacing.Z * FixedInterval.Clamp(value: FixedInterval.Round(value: (local.Z / spacing.Z)), minimum: -limit.Z, maximum: limit.Z)))
                    );
                }
            case SdfIntervalFoldAction.SymmetryPlane: {
                    var normal = instruction.BoundsVector;
                    var t = (Dot(left: local, right: normal) + instruction.Bounds0W);
                    var twiceMin = (FixedInterval.Min(first: t, second: Point(value: FixedQ4816.Zero)) * Point(value: Two));

                    return new(
                        X: (local.X - (normal.X * twiceMin)),
                        Y: (local.Y - (normal.Y * twiceMin)),
                        Z: (local.Z - (normal.Z * twiceMin))
                    );
                }
            case SdfIntervalFoldAction.Elongate: {
                    var extents = instruction.BoundsVector;

                    return new(
                        X: ElongateBounds(extent: extents.X, value: local.X),
                        Y: ElongateBounds(extent: extents.Y, value: local.Y),
                        Z: ElongateBounds(extent: extents.Z, value: local.Z)
                    );
                }
            case SdfIntervalFoldAction.Unsupported:
                throw new InvalidOperationException(message: "Unsupported IntervalFold action.");
            default:
                throw new InvalidOperationException(message: "Invalid IntervalFold action.");
        }
    }
    // The box field edits (Onion, CellDisplace, Dilate), kept out of WalkBounds with the folds.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedInterval EditFieldBounds(in CompiledInstruction instruction, IntervalVector3 local, FixedInterval result) => SdfFieldOps.IntervalFieldEditOf(value: instruction.Op) switch {
        SdfIntervalFieldEditAction.Onion => (FixedInterval.Abs(value: result) - instruction.Bounds0X),
        SdfIntervalFieldEditAction.CellDisplace => (result + (instruction.Bounds0Y * (CellBounds(
            mode: ((SdfCellMode)instruction.Blend),
            point: (local * instruction.Bounds0X),
            randomness: instruction.Bounds0Z,
            seed: instruction.Shape
        ) - Point(value: Half)))),
        SdfIntervalFieldEditAction.Dilate => (result - instruction.Bounds0X),
        SdfIntervalFieldEditAction.Unsupported => throw new InvalidOperationException(message: "The point walk selected an unsupported field edit."),
        _ => throw new InvalidOperationException(message: "Invalid IntervalFieldEdit action."),
    };
    // The render reading's warps over a box (WarpPoint's inclusion rules). AxialProfile divides the two other axes by an s
    // of at least FlareMinScale, so each lies between zero and its range over FlareMinScale. RotatePlane uses directed
    // interval trigonometry: the enclosure shrinks with the query box, so a contact solve can certify its surface.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IntervalVector3 WarpBounds(in CompiledInstruction instruction, IntervalVector3 local, ref FixedInterval distanceScale) {
        if (instruction.Op == SdfOp.AxialProfile) {
            var axis = ((int)instruction.Shape);
            var shrink = new FixedInterval(lower: FixedQ4816.Zero, upper: (FixedQ4816.One / FlareMinScale));

            distanceScale *= instruction.Bounds1X;

            return new IntervalVector3(
                X: ((axis == 0) ? local.X : (local.X * shrink)),
                Y: ((axis == 1) ? local.Y : (local.Y * shrink)),
                Z: ((axis == 2) ? local.Z : (local.Z * shrink))
            );
        }

        var (u, v) = PlaneAxes(plane: instruction.Shape);
        var angle = (instruction.Bounds0X * (local[((int)instruction.Blend)] - instruction.Bounds0Y));
        var sin = FixedInterval.Sin(angle: angle);
        var cos = FixedInterval.Cos(angle: angle);
        var pu = local[u];
        var pv = local[v];
        var turnedU = ((cos * pu) + (sin * pv));
        var turnedV = ((cos * pv) - (sin * pu));

        return new IntervalVector3(
            X: ((u == 0) ? turnedU : ((v == 0) ? turnedV : local.X)),
            Y: ((u == 1) ? turnedU : ((v == 1) ? turnedV : local.Y)),
            Z: ((u == 2) ? turnedU : ((v == 2) ? turnedV : local.Z))
        );
    }
    [DoesNotReturn]
    private static void ThrowNoInclusionRule(SdfOp op) =>
        throw new NotSupportedException(message: $"The bounds interpreter has no inclusion rule for op {op}.");
    // Whether the box walk may skip an instance: every candidate interval it can join has its lower end at or past the
    // threshold, as its box certificate proves, or, for an unscoped instance, each of its members' does. A regional walk
    // asks the point walk's question (InstanceClears) of every point of the box at once.
    private bool BoxInstanceClears(in CullBound bound, in BoxReachTest test, FixedQ4816 cheapReach, FixedQ4816 threshold, bool regional) =>
        (regional
            ? InstanceClears(bound: in bound, cheapReach: cheapReach, test: in test, threshold: threshold)
            : ((threshold.Value <= bound.SeedFloor) && bound.Reach.Boxed && (
                (cheapReach >= threshold) ||
                test.Clears(reach: in bound.Reach, threshold: threshold.Value) ||
                (!bound.Scoped && (bound.MemberCount > 1) && MembersClear(bound: in bound, test: in test, threshold: threshold.Value)))));
    // The box's least corner, the point whose reach orders an uncertified instance as a primer.
    private static FixedVector3 Corner(IntervalVector3 box) => new(X: box.X.Lower, Y: box.Y.Lower, Z: box.Z.Lower);
    private static ulong CentreMagnitude(in Reach reach) =>
        Math.Max(val1: FusedArithmetic.RawMagnitude(value: reach.CenterX), val2: Math.Max(val1: FusedArithmetic.RawMagnitude(value: reach.CenterY), val2: FusedArithmetic.RawMagnitude(value: reach.CenterZ)));

    /// <summary>Gets the ops the bounds interpreter has an inclusion rule for. Every op the point interpreter accepts
    /// under either reading must appear here; the sync law holds the two sets equal.</summary>
    public static IReadOnlyList<SdfOp> BoundedOps { get; } = Array.AsReadOnly(Enum.GetValues<SdfOp>()
        .Where(static op => SdfFieldOps.IntervalOf(value: op) != SdfIntervalAction.Unsupported).ToArray());
    /// <summary>Gets the shapes the bounds interpreter has an inclusion rule for. Every shape the point interpreter
    /// accepts must appear here; the sync law holds the two sets equal.</summary>
    public static IReadOnlyList<SdfShapeType> BoundedShapes { get; } = Array.AsReadOnly(Enum.GetValues<SdfShapeType>()
        .Where(static shape => SdfFieldShapes.ShapeIntervalOf(value: shape) != SdfShapeIntervalAction.Entire).ToArray());

    private static FixedInterval Point(FixedQ4816 value) =>
        FixedInterval.FromPoint(value: value);
    private static FixedInterval Dot(IntervalVector3 left, IntervalVector3 right) =>
        (((left.X * right.X) + (left.Y * right.Y)) + (left.Z * right.Z));
    // x − clamp(x, −e, e) is x − e above e, x + e below −e and zero between: monotone in x, and for each x monotone in e
    // (falling for x above zero, rising below it), so its extremes over the box of x and e lie at its corners, each exact.
    private static FixedInterval ElongateBounds(FixedInterval value, FixedInterval extent) {
        static FixedQ4816 Fold(FixedQ4816 x, FixedQ4816 e) =>
            (x - FixedQ4816.Clamp(maximum: e, minimum: -e, value: x));

        return ((value.IsUnbounded || extent.IsUnbounded)
            ? FixedInterval.Entire
            : new(
                lower: FixedQ4816.Min(x: Fold(x: value.Lower, e: extent.Lower), y: Fold(x: value.Lower, e: extent.Upper)),
                upper: FixedQ4816.Max(x: Fold(x: value.Upper, e: extent.Lower), y: Fold(x: value.Upper, e: extent.Upper))
            ));
    }
    // A table value off the raw grid, read within a raw of the raw it compiled to (CompiledInstruction.TableInexact).
    private static readonly FixedInterval TableSlack = new(lower: -FixedQ4816.Epsilon, upper: FixedQ4816.Epsilon);
    private static FixedInterval Widened(FixedQ4816 value) => (Point(value: value) + TableSlack);
    private static FixedInterval TableValue(FixedQ4816 value, bool inexact) => (inexact ? Widened(value: value) : Point(value: value));
    // The largest smooth radius a blend's enclosure holds (the carrier's greatest for an unbounded one), which every
    // margin and rounding allowance rising in the radius is charged at.
    private static FixedQ4816 SmoothRadiusReach(in CompiledInstruction instruction) => instruction.Bounds1X.Upper;
    // A Rotate in the affine reading: the generated turn by the quaternion's raws, and, for a quaternion off the grid,
    // what any quaternion of its enclosure turns the point by beyond it. Both lie in the enclosed map's entries
    // (RotationBounds, built from the enclosure), so each entry's difference is at most its width, and axis i moves by at
    // most the sum over j of that width times axis j's reach.
    private static FixedAffineForm3 RotateAffine(in CompiledInstruction instruction, FixedAffineForm3 p, in RotationBounds rotation) {
        var turned = MapPoint(instruction: in instruction, p: p);

        if (instruction.ExactData0) {
            return turned;
        }

        var (x, y, z) = (p.X.Range, p.Y.Range, p.Z.Range);
        FixedAffineForm Slack(FixedInterval m0, FixedInterval m1, FixedInterval m2) =>
            FixedAffineForm.FromInterval(interval: (((Spread(entry: m0) * x) + (Spread(entry: m1) * y)) + (Spread(entry: m2) * z)));
        static FixedInterval Spread(FixedInterval entry) => (entry.IsUnbounded
            ? FixedInterval.Entire
            : new FixedInterval(lower: (entry.Lower - entry.Upper), upper: (entry.Upper - entry.Lower)));

        return new FixedAffineForm3(
            X: FixedAffineForms.Add(a: turned.X, b: Slack(m0: rotation.M00, m1: rotation.M01, m2: rotation.M02)),
            Y: FixedAffineForms.Add(a: turned.Y, b: Slack(m0: rotation.M10, m1: rotation.M11, m2: rotation.M12)),
            Z: FixedAffineForms.Add(a: turned.Z, b: Slack(m0: rotation.M20, m1: rotation.M21, m2: rotation.M22))
        );
    }
    // Simplifies the general formula only when its quaternion coefficients are exactly 0 or +/-1. A half turn still
    // intersects the three-use interval with the slackened linear image: returning just the negated interval would
    // tighten the old answer and could move a sweep. The range guard proves every intermediate of the general formula
    // bounded (at most three copies of an endpoint, plus four raws); outside it, including Entire, use that formula
    // so its absorbing overflow and frame discovery remain identical.
    private static bool TryAxialRotationBounds(IntervalVector3 p, in CompiledInstruction instruction, out IntervalVector3 rotated) {
        rotated = p;

        if (!instruction.ExactData0 || !FitsAxialRotation(axis: p.X) || !FitsAxialRotation(axis: p.Y) || !FitsAxialRotation(axis: p.Z)) {
            return false;
        }

        var x = instruction.Data0X;
        var y = instruction.Data0Y;
        var z = instruction.Data0Z;
        var w = instruction.Data0W;
        var zero = FixedQ4816.Zero;
        var one = FixedQ4816.One;

        if ((x == zero) && (y == zero) && (z == zero)) {
            return ((w == one) || (w == -one));
        }

        if (w != zero) {
            return false;
        }

        var aroundX = (((x == one) || (x == -one)) && (y == zero) && (z == zero));
        var aroundY = ((x == zero) && ((y == one) || (y == -one)) && (z == zero));
        var aroundZ = ((x == zero) && (y == zero) && ((z == one) || (z == -one)));

        if (!aroundX && !aroundY && !aroundZ) {
            return false;
        }

        rotated = new IntervalVector3(
            X: (aroundX ? p.X : HalfTurnBounds(axis: p.X)),
            Y: (aroundY ? p.Y : HalfTurnBounds(axis: p.Y)),
            Z: (aroundZ ? p.Z : HalfTurnBounds(axis: p.Z))
        );
        return true;
    }

    private static readonly FixedInterval LinearRotationSlack = new(lower: FixedQ4816.FromRawBits(value: -LinearRotationSlackRaws), upper: FixedQ4816.FromRawBits(value: LinearRotationSlackRaws));

    private static bool FitsAxialRotation(FixedInterval axis) =>
        (!axis.IsUnbounded && (axis.Lower.Value >= -(long.MaxValue / 4L)) && (axis.Upper.Value <= (long.MaxValue / 4L)));
    // The step-by-step half turn, (axis + −axis) + −axis = [L − 2U, U − 2L], met with −axis widened by the linear
    // slack, [−U − 4, −L + 4], endpoint by endpoint: FitsAxialRotation keeps every sum inside the carrier, and both hold
    // [−U, −L], so the meet is never empty.
    private static FixedInterval HalfTurnBounds(FixedInterval axis) {
        var (lower, upper) = (axis.Lower.Value, axis.Upper.Value);

        return new FixedInterval(
            lower: FixedQ4816.FromRawBits(value: Math.Max(val1: ((lower - upper) - upper), val2: (-upper - LinearRotationSlackRaws))),
            upper: FixedQ4816.FromRawBits(value: Math.Min(val1: ((upper - lower) - lower), val2: (-lower + LinearRotationSlackRaws)))
        );
    }
    // The rotation constants every expansion of one Rotate instruction reads, enclosed once at construction: the
    // conjugate's components as points and the linear map M's entries (see RotateBoundsByInverseQuaternion). Each is a
    // function of the instruction alone, so the enclosure an expansion reads is the one it would have formed itself.
    private static (int[] Slots, RotationBounds[] Bounds) BuildRotationBounds(CompiledInstruction[] instructions) {
        var slots = new int[instructions.Length];
        var count = 0;

        // Numbered first, so the table is allocated once at its size.
        for (var index = 0; (index < instructions.Length); index++) {
            slots[index] = ((instructions[index].Op == SdfOp.Rotate) ? count++ : -1);
        }

        var bounds = new RotationBounds[count];

        for (var index = 0; (index < instructions.Length); index++) {
            if (slots[index] >= 0) { bounds[slots[index]] = RotationBounds.Of(instruction: in instructions[index]); }
        }

        return (slots, bounds);
    }
    // FixedQuaternion.Rotate's two fused stages over the conjugate, each stage's three-term sum enclosed by the sum of its
    // outward-rounded products: t = u×v + w·v, then d = u×t, then v + 2d.
    private static IntervalVector3 RotateBoundsByInverseQuaternion(IntervalVector3 p, in RotationBounds rotation) {
        var ux = rotation.Ux;
        var uy = rotation.Uy;
        var uz = rotation.Uz;
        var w = rotation.W;
        var tx = (((uy * p.Z) - (uz * p.Y)) + (w * p.X));
        var ty = (((uz * p.X) - (ux * p.Z)) + (w * p.Y));
        var tz = (((ux * p.Y) - (uy * p.X)) + (w * p.Z));
        var dx = ((uy * tz) - (uz * ty));
        var dy = ((uz * tx) - (ux * tz));
        var dz = ((ux * ty) - (uy * tx));
        var stepwise = new IntervalVector3(
            X: ((p.X + dx) + dx),
            Y: ((p.Y + dy) + dy),
            Z: ((p.Z + dz) + dz)
        );

        // The step-by-step enclosure reuses each coordinate several times, so a rotation as plain as a half turn
        // triples a box's width. The exact expression is linear in p, p + 2u×(u×p + w·p) = M·p with
        // M = (1 − 2|u|²)·I + 2·u·uᵀ + 2w·[u]×, so its enclosure as M·p reads each coordinate once per axis. The point
        // code rounds each of t and d to the nearest raw, so its answer lies within 0.5 + 2·0.5 raws of the exact d and
        // twice that of the exact result for |u| ≤ 1: three raws, widened to four. That error budget holds only while
        // no fused stage wraps, and the linear form never sees the stages: a stage can leave the carrier while M·p,
        // a rotated point, stays inside it. The step-by-step enclosure does see them, a bounded one proving every
        // stage of t and d representable, so the two meet only then (see Intersect).
        return new(
            X: Intersect(first: stepwise.X, second: ((((rotation.M00 * p.X) + (rotation.M01 * p.Y)) + (rotation.M02 * p.Z)) + LinearRotationSlack)),
            Y: Intersect(first: stepwise.Y, second: ((((rotation.M10 * p.X) + (rotation.M11 * p.Y)) + (rotation.M12 * p.Z)) + LinearRotationSlack)),
            Z: Intersect(first: stepwise.Z, second: ((((rotation.M20 * p.X) + (rotation.M21 * p.Y)) + (rotation.M22 * p.Z)) + LinearRotationSlack))
        );
    }

    /// <summary>Encloses the rounded inverse quaternion transform used by field-collider point reads.</summary>
    public static bool TryInverseRotationBounds(FixedQuaternion rotation, FixedVector3 lower, FixedVector3 upper, out FixedAabb box) {
        if (rotation == FixedQuaternion.Identity) { box = new FixedAabb(Maximum: upper, Minimum: lower); return true; }
        var constants = RotationBounds.Of(quaternion: rotation);
        var result = RotateBoundsByInverseQuaternion(p: new IntervalVector3(X: new(lower: lower.X, upper: upper.X), Y: new(lower: lower.Y, upper: upper.Y), Z: new(lower: lower.Z, upper: upper.Z)), rotation: in constants);

        box = FixedAabb.Everywhere;
        if (result.X.IsUnbounded || result.Y.IsUnbounded || result.Z.IsUnbounded) { return false; }
        box = new FixedAabb(new FixedVector3(X: result.X.Lower, Y: result.Y.Lower, Z: result.Z.Lower), new FixedVector3(X: result.X.Upper, Y: result.Y.Upper, Z: result.Z.Upper));
        return true;
    }

    // A faithful enclosure meets a tighter one that holds the point answer only where no point step wrapped. An unbounded
    // first stays unbounded: it is the proof that a point step may have wrapped, and the second, which assumes none did,
    // cannot bound what a wrap produced. An unbounded second, or an empty meet no point answer could produce, defers to
    // the first.
    private static FixedInterval Intersect(FixedInterval first, FixedInterval second) {
        if (first.IsUnbounded || second.IsUnbounded) {
            return first;
        }

        var lower = FixedQ4816.Max(x: first.Lower, y: second.Lower);
        var upper = FixedQ4816.Min(x: first.Upper, y: second.Upper);

        return ((lower <= upper)
            ? new FixedInterval(lower: lower, upper: upper)
            : first);
    }
    // A candidate times its scope scale's enclosure (ScopeScaleLow, ScopeScaleHigh): the product is monotone in the
    // candidate and in the scale, so its extremes lie at the corners, each rounded as the point walk rounds it and widened
    // by a raw.
    private static FixedInterval ScaleCandidateBounds(FixedInterval candidate, in CompiledInstruction instruction) {
        if ((instruction.ScopeScale <= 0L) || candidate.IsUnbounded) {
            return candidate;
        }

        var (low, high) = (instruction.ScopeScaleLow, instruction.ScopeScaleHigh);

        if (low > high) {
            return FixedInterval.Entire;
        }

        return new FixedInterval(
            lower: (FixedQ4816.Min(x: ScaleScope(candidate: candidate.Lower, scale: low), y: ScaleScope(candidate: candidate.Lower, scale: high)) - FixedQ4816.Epsilon),
            upper: (FixedQ4816.Max(x: ScaleScope(candidate: candidate.Upper, scale: low), y: ScaleScope(candidate: candidate.Upper, scale: high)) + FixedQ4816.Epsilon));
    }

    private static FixedInterval PopFieldBounds(FixedInterval candidate, FixedInterval saved, in CompiledInstruction instruction) {
        var isStairs = (instruction.Blend is ((uint)SdfBlendOp.StairsUnion) or ((uint)SdfBlendOp.StairsSubtraction));

        candidate = ScaleCandidateBounds(candidate: candidate, instruction: in instruction);

        if (instruction.Blend == ((uint)SdfBlendOp.Morph)) {
            var from = instruction.Bounds0Y;
            var to = instruction.Bounds0Z;
            var t = FixedInterval.Clamp(
                value: ((Point(value: FixedQ4816.Zero) - from) / (to - from)),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );

            return (((Point(value: FixedQ4816.One) - t) * saved) + (t * candidate));
        }

        if (isStairs) {
            var r = instruction.Bounds1X;
            var n = instruction.Bounds1Z;
            // The point walk steps only with at least one step of a positive radius; where the enclosures straddle that
            // test, the field is either the stairs or the plain blend.
            var always = (!r.IsUnbounded && !n.IsUnbounded && (n.Lower >= FixedQ4816.One) && (r.Lower > FixedQ4816.Zero));
            var never = (!r.IsUnbounded && !n.IsUnbounded && ((n.Upper < FixedQ4816.One) || (r.Upper <= FixedQ4816.Zero)));

            if (!never) {
                var s = (r / n);
                var twoS = (s * Point(value: Two));
                var isSubtraction = (instruction.Blend == ((uint)SdfBlendOp.StairsSubtraction));
                var u = (isSubtraction
                    ? ((-candidate) - r)
                    : (candidate - r));
                var argument = ((u - saved) + s);
                var m = (argument - (twoS * FixedInterval.Floor(value: (argument / twoS))));
                var w = (m - s);
                var stairs = (Point(value: Half) * ((u + saved) + FixedInterval.Abs(value: w)));
                var stepped = (isSubtraction
                    ? FixedInterval.Max(first: FixedInterval.Max(first: saved, second: -candidate), second: -stairs)
                    : FixedInterval.Min(first: FixedInterval.Min(first: saved, second: candidate), second: stairs));

                return (always ? stepped : FixedInterval.Union(first: stepped, second: BlendBounds(blend: instruction.Blend, candidate: candidate, current: saved, smoothRadius: instruction.Bounds1X)));
            }
        }

        return BlendBounds(blend: instruction.Blend, candidate: candidate, current: saved, smoothRadius: instruction.Bounds1X);
    }
    // The blend formulas (BlendShape, SdfFieldFormulas) over intervals, operation for operation, over the smooth radius's
    // enclosure.
    private static FixedInterval BlendBounds(FixedInterval current, FixedInterval candidate, uint blend, FixedInterval smoothRadius) {
        var smoothK = FixedInterval.Max(first: smoothRadius, second: Point(value: SmoothRadiusMin));
        var chamfer = FixedInterval.Max(first: smoothRadius, second: Point(value: FixedQ4816.Zero));
        var root = Point(value: SqrtHalf);

        return SdfFieldBlends.IntervalBlendOf(value: ((SdfBlendOp)blend)) switch {
            SdfIntervalBlendAction.SmoothUnion => SmoothUnionBounds(a: current, b: candidate, k: smoothK),
            SdfIntervalBlendAction.Subtraction => FixedInterval.Max(first: current, second: -candidate),
            SdfIntervalBlendAction.Intersection => FixedInterval.Max(first: current, second: candidate),
            SdfIntervalBlendAction.Xor => FixedInterval.Max(first: FixedInterval.Min(first: current, second: candidate), second: -FixedInterval.Max(first: current, second: candidate)),
            SdfIntervalBlendAction.SmoothIntersection => -SmoothUnionBounds(a: -current, b: -candidate, k: smoothK),
            SdfIntervalBlendAction.SmoothSubtraction => -SmoothUnionBounds(a: candidate, b: -current, k: smoothK),
            SdfIntervalBlendAction.GrooveUnion => FixedInterval.Max(first: FixedInterval.Min(first: current, second: candidate), second: (chamfer - FixedInterval.Magnitude(x: current, y: candidate))),
            SdfIntervalBlendAction.PipeUnion => FixedInterval.Min(first: FixedInterval.Min(first: current, second: candidate), second: (FixedInterval.Magnitude(x: current, y: candidate) - chamfer)),
            SdfIntervalBlendAction.GrooveSubtraction => FixedInterval.Max(first: FixedInterval.Max(first: current, second: -candidate), second: (chamfer - FixedInterval.Magnitude(x: current, y: candidate))),
            SdfIntervalBlendAction.PipeSubtraction => FixedInterval.Min(first: FixedInterval.Max(first: current, second: -candidate), second: (FixedInterval.Magnitude(x: current, y: candidate) - chamfer)),
            SdfIntervalBlendAction.ChamferUnion => FixedInterval.Min(first: FixedInterval.Min(first: current, second: candidate), second: (((current + candidate) - chamfer) * root)),
            SdfIntervalBlendAction.ChamferIntersection => FixedInterval.Max(first: FixedInterval.Max(first: current, second: candidate), second: (((current + candidate) + chamfer) * root)),
            SdfIntervalBlendAction.ChamferSubtraction => FixedInterval.Max(first: FixedInterval.Max(first: current, second: -candidate), second: (((current - candidate) + chamfer) * root)),
            SdfIntervalBlendAction.Union => FixedInterval.Min(first: current, second: candidate),
            _ => throw new InvalidOperationException(message: "Invalid IntervalBlend action."),
        };
    }
    // The smooth minimum (SdfBlendSmoothMinimum) over intervals. The exact polynomial smooth minimum rises with each operand (its gradient is
    // (h, 1 − h)), so over the box it lies between its values at the operands' lower ends and at their upper ends, and
    // the point blend lies within its rounding allowance r of the exact one (SmoothBlendRoundingRaws): the answer is the
    // point blend at those two corners widened by 2r, so a chain of smooth unions widens by 4r a blend. The operation-for-
    // operation hull (the h ≤ 0 select joining both arms wherever the box reaches both) reads b − a twice and doubles its
    // width at every blend, which leaves the carrier over a few trees' smooth unions and with it collapses the frame; it
    // is kept only as the proof that no step of the point blend leaves the carrier, and the two are intersected.
    // Over a radius enclosure the exact smooth minimum also falls as k rises (min(a, b) less k/4·max(0, 1 − |a − b|/k)²),
    // so the lower corner reads the greatest radius and the upper corner the least, each allowance at the greatest.
    private static FixedInterval SmoothUnionBounds(FixedInterval a, FixedInterval b, FixedInterval k) {
        if (k.IsUnbounded) {
            return FixedInterval.Entire;
        }

        var h = FixedInterval.Clamp(
            value: (Point(value: Half) + ((Point(value: Half) * (b - a)) / k)),
            minimum: FixedQ4816.Zero,
            maximum: FixedQ4816.One
        );
        var oneMinusH = (Point(value: FixedQ4816.One) - h);
        var lerp = (a + ((b - a) * oneMinusH));
        var blended = ((h.Upper <= FixedQ4816.Zero)
            ? b
            : ((h.Lower > FixedQ4816.Zero)
                ? lerp
                : FixedInterval.Union(first: b, second: lerp)));
        var stepped = (blended - ((k * h) * oneMinusH));

        if (stepped.IsUnbounded) {
            return stepped;
        }

        var rounding = (2L * SmoothBlendRoundingRaws(k: k.Upper));

        return Intersect(
            first: stepped,
            second: Within(
                lower: (((Int128)SdfBlendSmoothMinimum(a: a.Lower, b: b.Lower, k: k.Upper).Value) - rounding),
                upper: (((Int128)SdfBlendSmoothMinimum(a: a.Upper, b: b.Upper, k: k.Lower).Value) + rounding)
            )
        );
    }
    // The interval between two raw ends, each held to the carrier: every point answer lies in the carrier, so clamping an
    // end that bounds them keeps it a bound.
    private static FixedInterval Within(Int128 lower, Int128 upper) => new(
        lower: FixedQ4816.FromRawBits(value: ((long)Int128.Clamp(value: lower, min: FixedQ4816.MinValue.Value, max: FixedQ4816.MaxValue.Value))),
        upper: FixedQ4816.FromRawBits(value: ((long)Int128.Clamp(value: upper, min: FixedQ4816.MinValue.Value, max: FixedQ4816.MaxValue.Value)))
    );
    // SampleCells over a box: walked exactly when the box stays inside one lattice cell on every axis, otherwise bounded by
    // the reach of the two nearest features (every feature offset lies within 1/2 + |randomness|/2 of its cell's centre, and
    // the second nearest is no farther than the nearer of two adjacent cells' features).
    private static FixedInterval CellBounds(IntervalVector3 point, uint seed, SdfCellMode mode, FixedInterval randomness) {
        // The frequency multiplication is a point-evaluation step even though a cell distance is bounded. Its
        // overflow must reach the program result rather than being hidden by the lattice's finite reach.
        if (point.X.IsUnbounded || point.Y.IsUnbounded || point.Z.IsUnbounded) {
            return FixedInterval.Entire;
        }

        var cx = (point.X.Lower.Value >> FixedQ4816.FractionBitCount);
        var cy = (point.Y.Lower.Value >> FixedQ4816.FractionBitCount);
        var cz = (point.Z.Lower.Value >> FixedQ4816.FractionBitCount);

        if (
            (cx != (point.X.Upper.Value >> FixedQ4816.FractionBitCount)) ||
            (cy != (point.Y.Upper.Value >> FixedQ4816.FractionBitCount)) ||
            (cz != (point.Z.Upper.Value >> FixedQ4816.FractionBitCount))
        ) {
            // Each delta component lies within |x| + 1/2 + |randomness|/2 + 1 of zero; the own cell (x = 0) bounds the
            // nearest feature and a face neighbour bounds the second, and F2 − F1 is at most the second.
            var reach = (FixedInterval.Abs(value: randomness).Upper * Half);
            var near = ((Half + reach) + FixedQ4816.One);
            var far = (near + FixedQ4816.One);
            var span = FixedInterval.Magnitude(
                x: Point(value: far),
                y: Point(value: near),
                z: Point(value: near)
            );

            return new(lower: FixedQ4816.Zero, upper: span.Upper);
        }

        var fraction = (point - new FixedVector3(
            X: FixedQ4816.FromInteger(value: cx),
            Y: FixedQ4816.FromInteger(value: cy),
            Z: FixedQ4816.FromInteger(value: cz)
        ));
        var first = Point(value: FixedQ4816.MaxValue);
        var second = Point(value: FixedQ4816.MaxValue);

        for (var z = -1; (z <= 1); z++) {
            for (var y = -1; (y <= 1); y++) {
                for (var x = -1; (x <= 1); x++) {
                    var h = Pcg3dLatticeNoise.Pcg3d(
                        x: unchecked((uint)(cx + x)) ^ seed,
                        y: unchecked((uint)(cy + y)) ^ (seed ^ Pcg3dLatticeNoise.NoiseSeedStreamY),
                        z: unchecked((uint)(cz + z)) ^ (seed ^ Pcg3dLatticeNoise.NoiseSeedStreamZ)
                    );
                    var feature = new IntervalVector3(
                        X: (Point(value: (FixedQ4816.FromInteger(value: x) + Half)) + (randomness * (Point(value: FixedQ4816.FromRawBits(value: (h.X >> 16))) - Point(value: Half)))),
                        Y: (Point(value: (FixedQ4816.FromInteger(value: y) + Half)) + (randomness * (Point(value: FixedQ4816.FromRawBits(value: (h.Y >> 16))) - Point(value: Half)))),
                        Z: (Point(value: (FixedQ4816.FromInteger(value: z) + Half)) + (randomness * (Point(value: FixedQ4816.FromRawBits(value: (h.Z >> 16))) - Point(value: Half))))
                    );
                    var distance = FixedInterval.Magnitude(x: (feature.X - fraction.X), y: (feature.Y - fraction.Y), z: (feature.Z - fraction.Z));

                    // The two smallest of the running set, each an order statistic monotone in every argument.
                    var lowest = FixedInterval.Min(first: first, second: distance);

                    second = FixedInterval.Min(first: second, second: FixedInterval.Max(first: first, second: distance));
                    first = lowest;
                }
            }
        }

        return ((mode == SdfCellMode.F1)
            ? first
            : (second - first));
    }
    private static FixedInterval ShapeBounds(in CompiledInstruction instruction, IntervalVector3 p) {
        return instruction.ShapeIntervalAction switch {
            SdfShapeIntervalAction.Sphere => SphereBounds(p: p, radius: instruction.Bounds0X),
            SdfShapeIntervalAction.Box => BoxBounds(p: p, halfExtents: instruction.BoundsVector, cornerRadius: instruction.Bounds0W),
            SdfShapeIntervalAction.Torus => TorusBounds(p: p, majorRadius: instruction.Bounds0X, minorRadius: instruction.Bounds0Y),
            SdfShapeIntervalAction.Plane => PlaneBounds(p: p, normal: instruction.BoundsVector, offset: instruction.Bounds0W),
            SdfShapeIntervalAction.RoundCone => RoundConeBounds(p: p, lowerRadius: instruction.Bounds0X, upperRadius: instruction.Bounds0Y, height: instruction.Bounds0Z, b: instruction.Bounds0W, a: instruction.Bounds1Y),
            SdfShapeIntervalAction.Capsule => CapsuleBounds(p: p, endpoint: instruction.BoundsVector, radius: instruction.Bounds0W),
            SdfShapeIntervalAction.Cylinder => (Extrude2DBounds(distance2D: (FixedInterval.Magnitude(x: p.X, y: p.Z) - instruction.Bounds0X), z: p.Y, halfDepth: instruction.Bounds0Y) - instruction.Bounds1W),
            SdfShapeIntervalAction.Vesica => VesicaBounds(p: p, r: instruction.Bounds0X, d: instruction.Bounds0Y, b: instruction.Bounds0Z),
            SdfShapeIntervalAction.LiftedWithCap or SdfShapeIntervalAction.Trapezoid or SdfShapeIntervalAction.ConvexPolygon => (LiftedBounds(
                p: p,
                instruction: instruction,
                capChamfer: instruction.Bounds1Z
            ) - instruction.Bounds1W),
            SdfShapeIntervalAction.ChamferedRectangle => (LiftedBounds(
                p: p,
                instruction: instruction,
                capChamfer: instruction.Bounds0Z
            ) - instruction.Bounds1W),
            SdfShapeIntervalAction.Superellipsoid => SuperellipsoidBounds(p: p, radii: instruction.BoundsVector, exponent: instruction.Bounds0W),
            SdfShapeIntervalAction.Sweep => SweepBounds(p: p, curve: (instruction.SweepCurve ?? throw new UnreachableException(message: "Compile always attaches a SweepCurve to a Sweep instruction.")), strandOffset: instruction.Bounds0W, inexact: instruction.TableInexact),
            SdfShapeIntervalAction.Path => PathBounds(instruction: in instruction, p: p),
            // A shape with no rule proves nothing: the unbounded interval, which shrinks the frame and never grants it.
            SdfShapeIntervalAction.Entire => FixedInterval.Entire,
            _ => throw new InvalidOperationException(message: "Invalid ShapeInterval action."),
        };
    }
    private static FixedInterval BoxBounds(IntervalVector3 p, IntervalVector3 halfExtents, FixedInterval cornerRadius) {
        var absolute = p.Abs();
        var q = new IntervalVector3(X: (absolute.X - (halfExtents.X - cornerRadius)),
            Y: (absolute.Y - (halfExtents.Y - cornerRadius)), Z: (absolute.Z - (halfExtents.Z - cornerRadius)));
        var zero = Point(value: FixedQ4816.Zero);
        var outside = new IntervalVector3(
            X: FixedInterval.Max(first: q.X, second: zero),
            Y: FixedInterval.Max(first: q.Y, second: zero),
            Z: FixedInterval.Max(first: q.Z, second: zero)
        ).Length;
        var inside = FixedInterval.Min(first: FixedInterval.Max(first: q.X, second: FixedInterval.Max(first: q.Y, second: q.Z)), second: zero);

        return ((outside + inside) - cornerRadius);
    }
    // The shared interval parameter takes the exact quotient at its extreme corners for an endpoint on the grid. Squaring
    // the endpoint into a Q48.16 interval first would lose tiny segments to a divisor containing zero, or overflow on large
    // ones; an endpoint off the grid takes that quotient, and the whole parameter range where it fails.
    private static FixedInterval CapsuleBounds(IntervalVector3 p, IntervalVector3 endpoint, FixedInterval radius) {
        var h = FixedInterval.SegmentParameter(x: p.X, y: p.Y, z: p.Z, segmentX: endpoint.X, segmentY: endpoint.Y, segmentZ: endpoint.Z);

        return (new IntervalVector3(
            X: (p.X - (endpoint.X * h)),
            Y: (p.Y - (endpoint.Y * h)),
            Z: (p.Z - (endpoint.Z * h))
        ).Length - radius);
    }
    // SdfSuperellipsoid over a box. The point side scales each ratio q_i/m into [0, 1] (each q_i is at most the largest,
    // m), raises it, sums, and roots the sum; the largest axis's ratio is exactly one and so is its power, so the sum is
    // at least one, which keeps the lower bound the gauge's own m − min(r), each ratio scaled by min(r) as the point side
    // carries it. A box reaching the centre (m = 0) joins the centre arm, −min(r).
    private static FixedInterval SuperellipsoidBounds(IntervalVector3 p, IntervalVector3 radii, FixedInterval exponent) {
        var minimumRadius = FixedInterval.Min(first: radii.X, second: FixedInterval.Min(first: radii.Y, second: radii.Z));
        var q = SuperellipsoidScaledRatioBounds(absolute: p.Abs(), minimumRadius: minimumRadius, radii: radii);

        if (exponent.IsUnbounded || q.X.IsUnbounded || q.Y.IsUnbounded || q.Z.IsUnbounded) {
            return FixedInterval.Entire;
        }

        if ((exponent.Lower == SuperellipsoidSphereExponent) && (exponent.Upper == SuperellipsoidSphereExponent)) {
            return (q.Length - minimumRadius);
        }

        var m = FixedInterval.Max(first: q.X, second: FixedInterval.Max(first: q.Y, second: q.Z));
        FixedInterval? joined = null;

        if (m.Lower <= FixedQ4816.Zero) {
            joined = -minimumRadius;
        }

        if (m.Upper > FixedQ4816.Zero) {
            var power = exponent;

            FixedInterval Ratio(FixedInterval component) => ((m.Lower > FixedQ4816.Zero)
                ? FixedInterval.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: (component / m))
                : new FixedInterval(lower: FixedQ4816.Zero, upper: FixedQ4816.One));
            var sum = ((FixedInterval.Pow(exponent: power, value: Ratio(component: q.X)) + FixedInterval.Pow(exponent: power, value: Ratio(component: q.Y))) + FixedInterval.Pow(exponent: power, value: Ratio(component: q.Z)));
            var root = FixedInterval.Pow(exponent: (Point(value: FixedQ4816.One) / power), value: FixedInterval.Max(first: sum, second: Point(value: FixedQ4816.One)));
            var norm = (m * root);
            // The point side's clamp of the norm to its certified band (SdfSuperellipsoid), over the box: certain where
            // the whole enclosure lies in the band, either way where it straddles a band edge.
            var banded = ((exponent.Upper > Two) && (exponent.Lower <= SuperellipsoidClampedExponent));

            if (banded) {
                var length = q.Length;
                var floor = ((Point(value: FixedQ4816.One) - (Point(value: SuperellipsoidNormSlope) * (power - Point(value: Two)))) * length);
                var clamped = FixedInterval.Max(first: floor, second: FixedInterval.Min(first: norm, second: length));

                norm = (((exponent.Lower > Two) && (exponent.Upper <= SuperellipsoidClampedExponent)) ? clamped : FixedInterval.Union(first: norm, second: clamped));
            }

            joined = Join(joined: joined, next: (norm - minimumRadius));
        }

        return joined!.Value;
    }
    // SdfFieldEvaluator.SuperellipsoidScaledRatio over a box: the point side's one rounding is non-decreasing in the
    // magnitude, so on the grid its values at the box's two ends enclose every value the box holds, exactly. Over radii off
    // the grid, all positive, the exact ratio also rises with the least radius and falls with the axis's own, so it lies
    // between its values at the enclosures' corners, each within half a raw of its rounding: those, a raw outward.
    private static IntervalVector3 SuperellipsoidScaledRatioBounds(IntervalVector3 absolute, IntervalVector3 radii, FixedInterval minimumRadius) {
        var exact = (!minimumRadius.IsUnbounded && !radii.X.IsUnbounded && !radii.Y.IsUnbounded && !radii.Z.IsUnbounded &&
            (minimumRadius.Lower == minimumRadius.Upper) && (radii.X.Lower == radii.X.Upper) && (radii.Y.Lower == radii.Y.Upper) && (radii.Z.Lower == radii.Z.Upper));

        FixedInterval Axis(FixedInterval magnitude, FixedInterval radius) {
            if (magnitude.IsUnbounded) {
                return FixedInterval.Entire;
            }

            if (exact) {
                return new FixedInterval(
                    lower: SuperellipsoidScaledRatio(magnitude: magnitude.Lower, minRadius: minimumRadius.Lower, radius: radius.Lower),
                    upper: SuperellipsoidScaledRatio(magnitude: magnitude.Upper, minRadius: minimumRadius.Lower, radius: radius.Lower));
            }

            if (radius.IsUnbounded || minimumRadius.IsUnbounded || (minimumRadius.Lower <= FixedQ4816.Zero)) {
                return FixedInterval.Entire;
            }

            return new FixedInterval(
                lower: (SuperellipsoidScaledRatio(magnitude: magnitude.Lower, minRadius: minimumRadius.Lower, radius: radius.Upper) - FixedQ4816.Epsilon),
                upper: (SuperellipsoidScaledRatio(magnitude: magnitude.Upper, minRadius: minimumRadius.Upper, radius: radius.Lower) + FixedQ4816.Epsilon));
        }

        return new(X: Axis(magnitude: absolute.X, radius: radii.X), Y: Axis(magnitude: absolute.Y, radius: radii.Y), Z: Axis(magnitude: absolute.Z, radius: radii.Z));
    }
    // The sweep (SdfSweep) over a box, whatever parameter the point side picks: the centreline over the whole parameter
    // range is the box of the Bézier's interval evaluation, the radius the interval of every radius, and the strand offset
    // the interval its rotated frame reaches. The lower bound is the box's least distance to that hull, less the largest
    // radius. The upper bound is the lesser of the far side of that hull and, because the tube's parameter
    // (SdfSweepParameter) and the search (SdfSweepSearch) each measure the gap at the curve's start and only ever keep a
    // gap they measure lower, the gap at that start.
    // A curve off the grid reads each control point and radius within a raw (TableValue).
    private static FixedInterval SweepBounds(IntervalVector3 p, SweepCurveFixed curve, FixedInterval strandOffset, bool inexact) {
        var t = SweepParameter;
        var centre = BezierBounds(a: curve.A, b: curve.B, c: curve.C, inexact: inexact, t: t);
        var radiusStart = TableValue(inexact: inexact, value: curve.RadiusStart);
        var taper = (radiusStart + ((TableValue(inexact: inexact, value: curve.RadiusEnd) - radiusStart) * t));
        var swell = FixedInterval.Max(first: FixedInterval.Sin(angle: (Point(value: SweepPi) * t)), second: Point(value: FixedQ4816.Zero));
        var radius = (taper + (TableValue(inexact: inexact, value: curve.Bulge) * FixedInterval.Pow(exponent: Point(value: SweepBulgeExponent), value: swell)));
        // SinCos answers inside [−1, 1] for every angle, so the phase (which never sees the point) needs no enclosure.
        var spread = (((SweepFrameComponent * UnitRange) + (SweepFrameComponent * UnitRange)) * strandOffset);
        var offsetCentre = new IntervalVector3(X: (centre.X + spread), Y: (centre.Y + spread), Z: (centre.Z + spread));
        var distance = new IntervalVector3(X: (p.X - offsetCentre.X), Y: (p.Y - offsetCentre.Y), Z: (p.Z - offsetCentre.Z)).Length;
        var hull = (distance - radius);

        if (hull.IsUnbounded) {
            return FixedInterval.Entire;
        }

        // From the start: the answer is at most the gap at t = 0, |p - a| less the start radius, which is at least the
        // least radius. The strand's reach and one raw for its rounded coordinates and final root bound that gap.
        var startDistance = new IntervalVector3(X: (p.X - TableValue(inexact: inexact, value: curve.A.X)), Y: (p.Y - TableValue(inexact: inexact, value: curve.A.Y)), Z: (p.Z - TableValue(inexact: inexact, value: curve.A.Z))).Length;
        var fromStart = (((startDistance + new IntervalVector3(X: spread, Y: spread, Z: spread).Length) + Point(value: FixedQ4816.Epsilon)) - radius);

        return (fromStart.IsUnbounded
            ? hull
            : new FixedInterval(lower: hull.Lower, upper: FixedQ4816.Max(x: hull.Lower, y: FixedQ4816.Min(x: hull.Upper, y: fromStart.Upper))));
    }
    // SdfBezierPoint over a parameter interval: the two Lerps and the Lerp between them, each a single rounding of
    // from + (to − from)·t on the point side, enclosed here as written.
    private static IntervalVector3 BezierBounds(FixedVector3 a, FixedVector3 b, FixedVector3 c, FixedInterval t, bool inexact) {
        static FixedInterval Lerp(FixedInterval from, FixedInterval to, FixedInterval amount) =>
            (from + ((to - from) * amount));
        FixedInterval Axis(FixedQ4816 a, FixedQ4816 b, FixedQ4816 c) {
            var (pa, pb, pc) = (TableValue(inexact: inexact, value: a), TableValue(inexact: inexact, value: b), TableValue(inexact: inexact, value: c));

            return Lerp(amount: t, from: Lerp(amount: t, from: pa, to: pb), to: Lerp(amount: t, from: pb, to: pc));
        }

        return new(X: Axis(a: a.X, b: b.X, c: c.X), Y: Axis(a: a.Y, b: b.Y, c: c.Y), Z: Axis(a: a.Z, b: b.Z, c: c.Z));
    }
    private static FixedInterval SphereBounds(IntervalVector3 p, FixedInterval radius) => (p.Length - radius);
    private static FixedInterval TorusBounds(IntervalVector3 p, FixedInterval majorRadius, FixedInterval minorRadius) =>
        (FixedInterval.Magnitude(x: (FixedInterval.Magnitude(x: p.X, y: p.Z) - majorRadius), y: p.Y) - minorRadius);
    private static FixedInterval PlaneBounds(IntervalVector3 p, IntervalVector3 normal, FixedInterval offset) =>
        ((((p.X * normal.X) + (p.Y * normal.Y)) + (p.Z * normal.Z)) + offset);
    // The point side tests k against zero and against a·height, the two rounded once each; over the enclosures every
    // branch a test's interval reaches joins.
    private static FixedInterval RoundConeBounds(IntervalVector3 p, FixedInterval lowerRadius, FixedInterval upperRadius, FixedInterval height, FixedInterval b, FixedInterval a) {
        var qx = FixedInterval.Magnitude(x: p.X, y: p.Z);
        var qy = p.Y;
        var k = ((qx * (-b)) + (qy * a));
        var top = (a * height);
        FixedInterval? joined = null;

        // Joining branch results cannot certify a predicate whose point arithmetic may have wrapped.
        if (k.IsUnbounded || top.IsUnbounded) {
            return FixedInterval.Entire;
        }

        if (k.Lower < FixedQ4816.Zero) {
            joined = Join(joined: joined, next: (FixedInterval.Magnitude(x: qx, y: qy) - lowerRadius));
        }

        if (k.Upper > top.Lower) {
            joined = Join(joined: joined, next: (FixedInterval.Magnitude(x: qx, y: (qy - height)) - upperRadius));
        }

        if ((k.Upper >= FixedQ4816.Zero) && (k.Lower <= top.Upper)) {
            joined = Join(joined: joined, next: (((qx * a) + (qy * b)) - lowerRadius));
        }

        return joined!.Value;
    }
    private static FixedInterval VesicaBounds(IntervalVector3 p, FixedInterval r, FixedInterval d, FixedInterval b) {
        var qx = FixedInterval.Magnitude(x: p.X, y: p.Z);
        var qy = FixedInterval.Abs(value: p.Y);
        var test = (((qy - b) * d) - (qx * b));
        var cap = FixedInterval.Magnitude(x: qx, y: (qy - b));
        var body = (FixedInterval.Magnitude(x: (qx + d), y: qy) - r);

        if (test.IsUnbounded) {
            return FixedInterval.Entire;
        }

        // The point test compares two separately rounded products; their difference's interval decides it wherever it
        // keeps one sign.
        return ((test.Lower > FixedQ4816.Zero)
            ? cap
            : ((test.Upper <= FixedQ4816.Zero)
                ? body
                : FixedInterval.Union(first: cap, second: body)));
    }
    // The lifted formulas (SdfFieldFormulas.Lifted) over intervals: an extruded profile (lift above one half) or a revolved one.
    // The profile is chosen by a switch on the instruction's shape rather than a delegate, so a bounds walk, which a
    // moving body's certified sweep runs every tick, allocates nothing.
    // The lift (Data1.y) chooses extrusion above one half; an enclosure straddling that test joins both readings.
    private static FixedInterval LiftedBounds(IntervalVector3 p, in CompiledInstruction instruction, FixedInterval capChamfer) {
        var (liftAmount, lift) = (instruction.Bounds0W, instruction.Bounds1Y);
        var extruded = (lift.IsUnbounded || (lift.Upper > Half));
        var revolved = (lift.IsUnbounded || (lift.Lower <= Half));
        FixedInterval? joined = null;

        if (extruded) {
            joined = ExtrudeChamfer2DBounds(distance2D: ProfileBounds(instruction: instruction, p: new(X: p.X, Y: p.Y)), z: p.Z, halfDepth: liftAmount, c: capChamfer, sqrtHalf: Point(value: SqrtHalf));
        }

        if (revolved) {
            joined = Join(joined: joined, next: ProfileBounds(instruction: instruction, p: new(X: (FixedInterval.Magnitude(x: p.X, y: p.Z) - liftAmount), Y: p.Y)));
        }

        return joined!.Value;
    }
    private static FixedInterval ProfileBounds(in CompiledInstruction instruction, IntervalVector2 p) => SdfFieldShapes.ProfileIntervalOf(value: ((SdfShapeType)instruction.Shape)) switch {
        SdfProfileIntervalAction.RoundedRectangle => RoundedRectangle2DBounds(p: p, halfWidth: instruction.Bounds0X, halfHeight: instruction.Bounds0Y, cornerRadius: instruction.Bounds0Z),
        SdfProfileIntervalAction.Ellipse => (Ellipse2DBounds(p: p, semiX: instruction.Data0X, semiY: instruction.Data0Y) + ParameterSlack(first: instruction.Bounds0X, second: instruction.Bounds0Y)),
        SdfProfileIntervalAction.Trapezoid => Trapezoid2DBounds(p: p, r1: instruction.Bounds0X, r2: instruction.Bounds0Y, halfHeight: instruction.Bounds0Z),
        SdfProfileIntervalAction.ChamferedRectangle => ChamferBox2DBounds(p: p, halfWidth: instruction.Bounds0X, halfHeight: instruction.Bounds0Y, chamfer: instruction.Bounds0Z, sqrtHalf: Point(value: SqrtHalf)),
        SdfProfileIntervalAction.ConvexPolygon => (ConvexPolygon2DBounds(p: p, vertices: (instruction.ConvexPolygonVertices ?? [])) + TableReach(inexact: instruction.TableInexact)),
        SdfProfileIntervalAction.Unsupported => throw new NotSupportedException(message: $"The bounds interpreter has no profile for shape {((SdfShapeType)instruction.Shape)}."),
        _ => throw new InvalidOperationException(message: "Invalid ProfileInterval action."),
    };
    private static FixedInterval Extrude2DBounds(FixedInterval distance2D, FixedInterval z, FixedInterval halfDepth) {
        var wy = (FixedInterval.Abs(value: z) - halfDepth);
        var zero = Point(value: FixedQ4816.Zero);
        var inside = FixedInterval.Min(first: FixedInterval.Max(first: distance2D, second: wy), second: zero);
        var outside = FixedInterval.Magnitude(x: FixedInterval.Max(first: distance2D, second: zero), y: FixedInterval.Max(first: wy, second: zero));

        return (inside + outside);
    }
    private static FixedInterval ExtrudeChamfer2DBounds(FixedInterval distance2D, FixedInterval z, FixedInterval halfDepth, FixedInterval c, FixedInterval sqrtHalf) {
        var wy = (FixedInterval.Abs(value: z) - halfDepth);
        var plain = Extrude2DBounds(distance2D: distance2D, halfDepth: halfDepth, z: z);
        var bevel = (((distance2D + wy) + c) * sqrtHalf);

        return FixedInterval.Max(first: plain, second: bevel);
    }
    private static FixedInterval RoundedRectangle2DBounds(IntervalVector2 p, FixedInterval halfWidth, FixedInterval halfHeight, FixedInterval cornerRadius) {
        var qx = ((FixedInterval.Abs(value: p.X) - halfWidth) + cornerRadius);
        var qy = ((FixedInterval.Abs(value: p.Y) - halfHeight) + cornerRadius);
        var zero = Point(value: FixedQ4816.Zero);
        var outside = FixedInterval.Magnitude(x: FixedInterval.Max(first: qx, second: zero), y: FixedInterval.Max(first: qy, second: zero));
        var inside = FixedInterval.Min(first: FixedInterval.Max(first: qx, second: qy), second: zero);

        return ((inside + outside) - cornerRadius);
    }
    private static FixedInterval ChamferBox2DBounds(IntervalVector2 p, FixedInterval halfWidth, FixedInterval halfHeight, FixedInterval chamfer, FixedInterval sqrtHalf) {
        var qx = (FixedInterval.Abs(value: p.X) - halfWidth);
        var qy = (FixedInterval.Abs(value: p.Y) - halfHeight);
        var zero = Point(value: FixedQ4816.Zero);
        var box = (FixedInterval.Min(first: FixedInterval.Max(first: qx, second: qy), second: zero) + FixedInterval.Magnitude(x: FixedInterval.Max(first: qx, second: zero), y: FixedInterval.Max(first: qy, second: zero)));
        var bevel = (((qx + qy) + chamfer) * sqrtHalf);

        return FixedInterval.Max(first: box, second: bevel);
    }
    // The shared interval parameter encloses the point side's exact quotient without a carrier-rounded squared length; the
    // slant (r2 − r1, 2h) is the point side's exact difference and doubling, over the enclosures.
    private static FixedInterval Trapezoid2DBounds(IntervalVector2 p, FixedInterval r1, FixedInterval r2, FixedInterval halfHeight) {
        var slant = new IntervalVector2(X: (r2 - r1), Y: (Point(value: Two) * halfHeight));
        var px = FixedInterval.Abs(value: p.X);
        var zero = Point(value: FixedQ4816.Zero);
        // p.x − min(p.x, r) is max(p.x − r, 0) exactly; r is r1 below the axis and r2 at or above it.
        var r = ((p.Y.Upper < FixedQ4816.Zero)
            ? r1
            : ((p.Y.Lower >= FixedQ4816.Zero)
                ? r2
                : FixedInterval.Union(first: r1, second: r2)));
        var cax = FixedInterval.Max(first: (px - r), second: zero);
        var cay = (FixedInterval.Abs(value: p.Y) - halfHeight);
        var projection = FixedInterval.SegmentParameter(x: (r2 - px), y: (halfHeight - p.Y), segmentX: slant.X, segmentY: slant.Y);
        var cbx = ((px - r2) + (slant.X * projection));
        var cby = ((p.Y - halfHeight) + (slant.Y * projection));
        var magnitude = FixedInterval.Sqrt(value: FixedInterval.Min(
            first: (FixedInterval.Square(value: cax) + FixedInterval.Square(value: cay)),
            second: (FixedInterval.Square(value: cbx) + FixedInterval.Square(value: cby))
        ));
        var surelyInside = ((cbx.Upper < FixedQ4816.Zero) && (cay.Upper < FixedQ4816.Zero));
        var surelyOutside = ((cbx.Lower >= FixedQ4816.Zero) || (cay.Lower >= FixedQ4816.Zero));

        return (surelyInside
            ? -magnitude
            : (surelyOutside
                ? magnitude
                : FixedInterval.Union(first: -magnitude, second: magnitude)));
    }
    // The exact polygon field over a box: the edge-list core (Polygon2DBounds) walked from each vertex back to the one
    // before it.
    private static FixedInterval ConvexPolygon2DBounds(IntervalVector2 p, FixedVector2[] vertices) =>
        Polygon2DBounds(edges: new PolygonEdges(vertices: vertices), p: p);
    // What an outline compiled from a side table off the grid can move its field by: each coordinate lies within half a
    // raw of its raw, so each point of an edge (a vertex, or a stroke's end and radius) moves by at most a raw, and a
    // signed distance to an outline whose every point moves by at most δ moves by at most 2δ.
    private static readonly FixedInterval TableOutlineReach = new(lower: FixedQ4816.FromRawBits(value: -2L), upper: FixedQ4816.FromRawBits(value: 2L));
    private static FixedInterval TableReach(bool inexact) => (inexact ? TableOutlineReach : Point(value: FixedQ4816.Zero));
    // What the ellipse's field can move by as its semi-axes run over their enclosures: each point of the outline moves by
    // at most the larger change, so the signed distance by at most twice it.
    private static FixedInterval ParameterSlack(FixedInterval first, FixedInterval second) {
        if (first.IsUnbounded || second.IsUnbounded) {
            return FixedInterval.Entire;
        }

        var widest = FixedQ4816.Max(x: (first.Upper - first.Lower), y: (second.Upper - second.Lower));
        var reach = (widest + widest);

        return new FixedInterval(lower: -reach, upper: reach);
    }
    private static bool? Decide(bool always, bool never) =>
        (always
            ? true
            : (never
                ? false
                : null));
    private static FixedInterval Join(FixedInterval? joined, FixedInterval next) =>
        ((joined is { } value)
            ? FixedInterval.Union(first: value, second: next)
            : next);

    // === The affine reading's meets =====================================================================================
    // A form and an interval that hold the same point values meet: the interval narrows to the form's range, and a form
    // the affine reading could not keep (unbounded where the interval is bounded) is the interval as fresh noise. An
    // unbounded interval stays unbounded, as Intersect keeps it: it is the proof a point step may have wrapped.
    private static readonly FixedAffineForm3 AffineEntirePoint = new(X: FixedAffineForm.Entire, Y: FixedAffineForm.Entire, Z: FixedAffineForm.Entire);

    private static FixedAffineForm Meet(FixedAffineForm form, ref FixedInterval interval) {
        if (form.IsUnbounded) {
            return FixedAffineForm.FromInterval(interval: interval);
        }

        interval = Intersect(first: interval, second: form.Range);
        return form;
    }
    private static FixedAffineForm3 Meet(FixedAffineForm3 point, ref IntervalVector3 box) {
        var (x, y, z) = (box.X, box.Y, box.Z);
        var met = new FixedAffineForm3(X: Meet(form: point.X, interval: ref x), Y: Meet(form: point.Y, interval: ref y), Z: Meet(form: point.Z, interval: ref z));

        box = new IntervalVector3(X: x, Y: y, Z: z);
        return met;
    }
    private static FixedAffineForm3 Lift(IntervalVector3 box) => FixedAffineForm3.FromIntervals(x: box.X, y: box.Y, z: box.Z);
    // A candidate times the walk's distance scale, as the point walk's carrier product rounds it: a constant multiple
    // for a point scale, a product with the scale's interval as noise otherwise.
    private static FixedAffineForm Multiply(FixedAffineForm form, FixedInterval scale) => (scale.IsUnbounded
        ? FixedAffineForm.Entire
        : ((scale.Lower == scale.Upper)
            ? FixedAffineForms.Multiply(a: form, b: scale.Lower)
            : FixedAffineForms.Multiply(a: form, b: FixedAffineForm.FromInterval(interval: scale))));
    // A candidate scaled by a scope's 2⁻³² fraction (ScaleScope): the rounded quotient of its product with the scale.
    private static FixedAffineForm ScaleScope(FixedAffineForm candidate, long scale) =>
        FixedAffineForms.MultiplyDivide(divisor: FixedQ4816.FromRawBits(value: (1L << ScopeScaleBits)), multiplier: FixedQ4816.FromRawBits(value: scale), value: candidate);

    // The point a box walk carries and its root frame restores: the box's interval and its affine forms together.
    private readonly record struct BoxPoint(IntervalVector3 Box, FixedAffineForm3 Affine);
    // A box of points: one interval per axis.
    private readonly record struct IntervalVector3(FixedInterval X, FixedInterval Y, FixedInterval Z) {
        public FixedInterval Length => FixedInterval.Magnitude(x: X, y: Y, z: Z);

        public FixedInterval this[int axis] => axis switch {
            0 => X,
            1 => Y,
            2 => Z,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(axis)),
        };

        public IntervalVector3 With(int axis, FixedInterval value) => axis switch {
            0 => (this with { X = value }),
            1 => (this with { Y = value }),
            2 => (this with { Z = value }),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(axis)),
        };
        public IntervalVector3 Abs() => new(
            X: FixedInterval.Abs(value: X),
            Y: FixedInterval.Abs(value: Y),
            Z: FixedInterval.Abs(value: Z)
        );

        public static IntervalVector3 operator -(IntervalVector3 left, FixedVector3 right) => new(
            X: (left.X - FixedInterval.FromPoint(value: right.X)),
            Y: (left.Y - FixedInterval.FromPoint(value: right.Y)),
            Z: (left.Z - FixedInterval.FromPoint(value: right.Z))
        );
        public static IntervalVector3 operator *(IntervalVector3 left, FixedInterval right) => new(
            X: (left.X * right),
            Y: (left.Y * right),
            Z: (left.Z * right)
        );
    }
    private readonly record struct IntervalVector2(FixedInterval X, FixedInterval Y);
    // The nearest cullable instance a box walk may evaluate ahead of its place, and its bounds once walked.
    private struct BoxPrimer {
        public static BoxPrimer None => new() { Bound = -1, Candidate = -1 };

        // The instance chosen to walk first, or -1 once the walk no longer needs one.
        public int Candidate;
        // The instance walked, or -1 before it is.
        public int Bound;
        public FixedInterval Bounds;

        // The reach at or past which an instance's candidates cannot lower any point answer in the box: the running
        // upper end, or the primer's when it is lower. Strict, the primer's end stands one raw higher: an instance
        // skipped by it may stand before the primer in the stream, where a candidate equal to the primer's answer
        // would keep its own material, so only one strictly past it leaves a point walk's material as well.
        public readonly FixedQ4816 Clearance(FixedQ4816 upper, bool strict) =>
            ((Bound >= 0) ? FixedQ4816.Min(x: upper, y: (strict ? (Bounds.Upper + FixedQ4816.Epsilon) : Bounds.Upper)) : upper);
    }
    // One Rotate instruction's enclosed constants: the conjugate's vector part and scalar as points, and the linear
    // map M = (1 − 2|u|²)·I + 2·u·uᵀ + 2w·[u]× entry by entry, each formed with outward rounding exactly as written.
    private readonly record struct RotationBounds(
        FixedInterval Ux, FixedInterval Uy, FixedInterval Uz, FixedInterval W,
        FixedInterval M00, FixedInterval M01, FixedInterval M02,
        FixedInterval M10, FixedInterval M11, FixedInterval M12,
        FixedInterval M20, FixedInterval M21, FixedInterval M22
    ) {
        // A Rotate's constants over its quaternion's enclosure, so they hold the packed float's map as well as the raws'.
        public static RotationBounds Of(in CompiledInstruction instruction) => Of(w: instruction.Bounds0W, x: instruction.Bounds0X, y: instruction.Bounds0Y, z: instruction.Bounds0Z);
        public static RotationBounds Of(FixedQuaternion quaternion) => Of(w: Point(value: quaternion.W), x: Point(value: quaternion.X), y: Point(value: quaternion.Y), z: Point(value: quaternion.Z));
        public static RotationBounds Of(FixedInterval x, FixedInterval y, FixedInterval z, FixedInterval w) {
            var ux = -x;
            var uy = -y;
            var uz = -z;
            var two = Point(value: Two);
            var usq = (((ux * ux) + (uy * uy)) + (uz * uz));
            var diagonal = (Point(value: FixedQ4816.One) - (two * usq));

            return new(
                M00: (diagonal + (two * (ux * ux))), M01: (two * ((ux * uy) - (w * uz))), M02: (two * ((ux * uz) + (w * uy))), M10: (two * ((uy * ux) + (w * uz))),
                M11: (diagonal + (two * (uy * uy))),
                M12: (two * ((uy * uz) - (w * ux))),
                M20: (two * ((uz * ux) - (w * uy))),
                M21: (two * ((uz * uy) + (w * ux))),
                M22: (diagonal + (two * (uz * uz))),
                Ux: ux,
                Uy: uy,
                Uz: uz,
                W: w
            );
        }
    }
}
