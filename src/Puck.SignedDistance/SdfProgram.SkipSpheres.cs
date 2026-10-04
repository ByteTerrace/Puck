using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // HOST-BAKED bounding-sphere skip data (programs build once, shapes evaluate millions of times per frame): for
    // every plain-Union shape reachable through a RIGID transform chain, a conservative world-space bounding sphere
    // lets map() skip work outright when the sphere's lower-bound distance cannot beat the running union minimum —
    // mathematically EXACT for Union (the skipped candidate's true distance is >= the bound, so the min, the
    // material winner, and every pixel are unchanged; a skip decision may even DIFFER between backends without any
    // pixel differing, because either path produces the identical result). Non-Union blends, unbounded/approximate
    // shapes (plane, ellipsoid), and chains through a non-uniform Scale or a Repeat/symmetry/wallpaper/warp/elongate op
    // evaluate fully — no bound is always correct; a wrong bound is a rendering bug. A uniform Scale (the identity among
    // them) keeps the chain boundable; see the Scale arm of AnalyzeSegment for the argument.
    //
    // Two levels:
    // - The SEGMENT DIRECTORY partitions the stream at ResetPoints into chain segments, each with one combined
    //   sphere over all its shapes; map()'s OUTER loop tests it and skips the whole chain — transforms included,
    //   which is where the per-step dynamic quaternion rotate cost lives. A skipped segment's transform state is
    //   provably dead (every later segment begins with a ResetPoint, or reads no point:
    //   RequireSegmentsStartAtTheWorldPoint refuses a stream that may carry a moved point into one that does, which is also
    //   why AnalyzeSegment can start every segment from the identity). Directory iteration is
    //   also what keeps the skip fast: the outer loop's counter never depends on a loaded value, so the per-segment
    //   sphere loads pipeline instead of serializing. A segment with any op but a rigid move or uniform scale, a field op (Onion/Dilate — a
    //   skip must never jump one), non-Union shape, unbounded shape, or mixed static/dynamic spheres gets mode 0:
    //   always evaluated.
    // - The per-SHAPE table carries each qualifying shape's own (tighter) sphere, tested only inside an evaluated
    //   segment right before the shape evaluates — always sound, because shape ops never mutate chain state.
    //
    // A STATIC sphere's center is world-space. A DYNAMIC sphere (a single TransformDynamic in the chain, no rotation
    // before it) stores the chain's pre-dynamic translation as its center and the entity slot: the shader adds the
    // slot's per-frame position — center = offset + dynPos, NO quaternion rotate — with the post-dynamic local
    // geometry folded into the radius, which is the whole win for far-away moving entities.
    private (List<BoundRecord> ShapeBounds, List<BoundRecord> Segments) AnalyzeBounds(int[] instructionOwners) {
        var segments = new List<BoundRecord>();
        var shapeBounds = new List<BoundRecord>();

        // O(instructions + instances) lookups replacing the per-instruction linear scans of m_instances the segment
        // walk below would otherwise do (a boundary test per instruction, an owner resolve per segment — together
        // O(instructions x instances)). The owner map arrives from ValidatePackedContract, which already built it for
        // the field-scope walk over the same stream.
        // Segments split BEFORE each ResetPoint AND at every instance boundary (m_instances' First/End): a segment
        // never straddles two instances (or an instance and the WORLD set), so the instance table below can express
        // "this instance owns segments [a, b)" as a plain contiguous directory range. The next segment's leading
        // ResetPoint rebuilds every piece of chain state a segment skip leaves stale.
        foreach (var (segmentStart, segmentEnd) in SegmentRanges()) {
            segments.Add(item: AnalyzeSegment(
                segmentStart: segmentStart,
                segmentEnd: segmentEnd,
                instanceIndex: instructionOwners[segmentStart],
                shapeBounds: shapeBounds
            ));
        }

        // Merge consecutive skippable segments into fewer, wider directory entries — every map() call pays one test
        // per entry, so co-located chains (a stand's pedestal/screen/slot/housing, a shelf of brackets, a d-pad's two
        // crossed boxes) should cost ONE test when the whole cluster is far. Exactness is untouched (a merged sphere
        // still contains every member shape); the trade is only that a FAILED merged test evaluates every member
        // chain (their per-shape spheres still gate the shape evaluations). Two flavours:
        // - DYNAMIC + DYNAMIC on the SAME entity slot: always merged (they move together).
        // - STATIC + STATIC: merged when the enclosing sphere stays within the members' summed radii — co-located
        //   clusters merge, well-separated ones (e.g. two different stands) stay individual so a wide sphere never
        //   erases a productive skip.
        // NEVER merges across an instance boundary (InstanceIndex differs) — the instance table's segment range must
        // stay contiguous and exclusive to its owner.
        //
        // A right-to-left COMPACTION pass, not a right-to-left scan with an in-place RemoveAt per merge: the scan
        // order and every merge decision are unchanged (TryMergeAdjacentSegments below is the same test, same
        // branches, same float ops the inline version used), but the result is built by APPENDING finished entries to
        // a second list instead of shifting `segments`' own suffix down by one on every merge — O(segments) total
        // instead of O(segments x merges). `accumulator` plays the role the old loop's `next` played: the up-to-date
        // (possibly already-merged) entry immediately to the right of the index under test.
        // Dense programs retain their reset chains as tape deletion units; their slab proof replaces the benefit
        // of merging nearby bounds. Small programs retain the compact directory and never build a tile tape.
        if ((segments.Count > 1) && (m_instructions.Length < TapeInstructionThreshold)) {
            var compacted = new List<BoundRecord>(capacity: segments.Count);
            var accumulator = segments[^1];

            for (var index = (segments.Count - 2); (index >= 0); index--) {
                var candidate = segments[index];

                if (TryMergeAdjacentSegments(
                    current: in candidate,
                    merged: out var mergedRecord,
                    next: in accumulator
                )) {
                    accumulator = mergedRecord;
                } else {
                    compacted.Add(item: accumulator);
                    accumulator = candidate;
                }
            }

            compacted.Add(item: accumulator);
            compacted.Reverse();
            segments = compacted;
        }

        return (shapeBounds, segments);
    }
    // Walks one segment maintaining the FORWARD rigid transform (local shape space -> world) the chain's point ops
    // invert, appending per-shape records as it goes, and returns the segment's directory record (mode 0 when any
    // instruction disqualifies the whole-chain skip).
    private BoundRecord AnalyzeSegment(int segmentStart, int segmentEnd, int instanceIndex, List<BoundRecord> shapeBounds) {
        var chainBoundable = true;
        var dynamicOffset = Vector3.Zero;
        var dynamicSlot = NoDynamicTransformSlot;
        var position = Vector3.Zero;
        var rotation = Quaternion.Identity;
        // The chain's accumulated UNIFORM scale k: the forward map is world = position + rotation·(k·local), and every
        // candidate reaches the blend as k·f(local) (mapCore's distanceScale). Exactly 1 until a non-identity Scale.
        var scale = 1f;
        var segmentEligible = true;
        var firstShapeBound = shapeBounds.Count;

        for (var index = segmentStart; (index <= segmentEnd); index++) {
            var instruction = m_instructions[index];

            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                        // Only ever the segment's first instruction (segments split before each ResetPoint) — the walk's
                        // initial state IS the reset state.
                        break;
                    }
                case SdfOp.Translate: {
                        // The offset is authored in the scaled frame, so it reaches the chain's outer frame times k
                        // (exact when k is 1, which keeps every scale-free chain's center bit-identical).
                        position += Vector3.Transform(
                            value: (new Vector3(
                                x: instruction.Data0.X,
                                y: instruction.Data0.Y,
                                z: instruction.Data0.Z
                            ) * scale),
                            rotation: rotation
                        );
                        break;
                    }
                case SdfOp.Rotate: {
                        // A uniform scale commutes with every rotation, so k stays a scalar beside the quaternion.
                        rotation = Quaternion.Concatenate(
                            value1: new Quaternion(
                                w: instruction.Data0.W,
                                x: instruction.Data0.X,
                                y: instruction.Data0.Y,
                                z: instruction.Data0.Z
                            ),
                            value2: rotation
                        );
                        break;
                    }
                case SdfOp.Scale: {
                        // mapCore divides the point by Data0.xyz and multiplies every later candidate by Data0.w (the min
                        // axis). When the three axes and the min are one positive value s, the op is the similarity
                        // q = s·q': a later shape's candidate is c(p) = k·s·f(q'), and for f >= |q' - c| - r (what
                        // TryGetLocalBound promises) c(p) >= |p - C| - k·s·r with C the forward image of c. So the sphere
                        // (C, k·s·r) bounds the candidate from below exactly as the unscaled sphere bounds f, and the skip
                        // stays exact: Union keeps its min and material winner. Identity (s = 1) changes neither the point
                        // nor the distance in float, so it leaves the walk bit-identical.
                        //
                        // A NON-uniform scale keeps no sphere, by proof rather than caution: its candidate is
                        // min(s)·f(S⁻¹q), whose lower bound min(s)/max(s)·|q - Sc| - min(s)·r grows SLOWER than any sphere's
                        // |q - Sc| - R. Far along the long axis the candidate falls below every finite sphere's bound, so a
                        // skip there could drop the winning candidate and change the field, whatever R is. (The sphere of
                        // radius max(s)·r does contain the scaled shape, but containment is not what the skip needs.) The
                        // mirror (Data0.x = -1) is an isometry, but this walk keeps rotations as quaternions, which cannot
                        // carry a reflection, so it stops the chain too.
                        var data0 = instruction.Data0;

                        if (
                            (data0.X > 0f) &&
                            (data0.X == data0.Y) &&
                            (data0.Y == data0.Z) &&
                            (data0.Z == data0.W)
                        ) {
                            scale *= data0.W;
                        } else {
                            chainBoundable = false;
                            segmentEligible = false;
                        }

                        break;
                    }
                case SdfOp.TransformDynamic: {
                        // One dynamic per chain, and NO rotation or scale before it — otherwise the shader-side center
                        // would need the very quaternion rotate the skip exists to avoid (or a scaled entity position);
                        // be conservative and evaluate fully.
                        if (
                            (dynamicSlot != NoDynamicTransformSlot) ||
                            !rotation.IsIdentity ||
                            (scale != 1f)
                        ) {
                            chainBoundable = false;
                            segmentEligible = false;
                        } else {
                            dynamicOffset = position;
                            dynamicSlot = ((int)instruction.Data0.X);
                            position = Vector3.Zero;
                        }

                        break;
                    }
                case SdfOp.ShapeBlend: {
                        if (
                            chainBoundable &&
                            (((uint)SdfBlendOp.Union) == instruction.Blend) &&
                            // Sweep and Path cap their local candidate at SDF_FAR_DISTANCE. The kernel's world-unit
                            // far guard only protects that cap when the accumulated scale does not shrink it.
                            ((scale >= 1f) || ((instruction.Shape != ((uint)SdfShapeType.Sweep)) && (instruction.Shape != ((uint)SdfShapeType.Path)))) &&
                            TryGetLocalBound(
                            center: out var localCenter,
                            convexPolygonProfiles: m_convexPolygonProfiles,
                            instruction: instruction,
                            instructionIndex: index,
                            radius: out var localRadius,
                            sweepCurves: m_sweepCurves
                        )
                        ) {
                            var chainCenter = (position + Vector3.Transform(
                                rotation: rotation,
                                value: (localCenter * scale)
                            ));
                            var chainRadius = (localRadius * scale);

                            // Dynamic: the post-dynamic local geometry folds into the radius, so the entity's orientation
                            // can never move the shape outside offset + dynPos ± radius — rotation-free in the shader.
                            shapeBounds.Add(item: ((dynamicSlot == NoDynamicTransformSlot)
                                ? new BoundRecord(
                                    Center: chainCenter,
                                    End: (index + 1),
                                    Instruction: index,
                                    Mode: BoundModeStatic,
                                    Radius: chainRadius,
                                    Slot: 0
                                )
                                : new BoundRecord(
                                    Center: dynamicOffset,
                                    End: (index + 1),
                                    Instruction: index,
                                    Mode: BoundModeDynamic,
                                    Radius: (chainCenter.Length() + chainRadius),
                                    Slot: dynamicSlot
                                )));
                        } else {
                            segmentEligible = false;
                        }

                        break;
                    }
                case SdfOp.PushField:
                case SdfOp.PopField: {
                        // A scope boundary op mutates the FIELD (the running accumulator), not the point/transform, so the
                        // chain's world-space sphere stays sound — chainBoundable is left TRUE, and any Union shape after the
                        // Push in this SAME chain still earns its per-shape cull bound (the accumulator-plan correction: the
                        // default arm's chainBoundable = false would suppress those bounds, a real cull regression). But a
                        // whole-segment skip must NEVER jump a Push (savedDistance would be unset) or a Pop (the parent
                        // compose would be lost), so the segment holding one is always evaluated.
                        segmentEligible = false;

                        break;
                    }
                default: {
                        // Repeat/RepeatLimited/SymmetryPlane/WallpaperFold/CellJitter/RepeatPolar
                        // (space folding), Twist/Bend/Elongate/DomainWarp/AxialProfile/Shear/GaussianPush(Data) (non-isometries),
                        // Onion/Dilate/Displace/NoiseDisplace (field ops a skip must never jump over):
                        // no world-space sphere is sound past this point, and the segment cannot be skipped whole.
                        chainBoundable = false;
                        segmentEligible = false;

                        break;
                    }
            }
        }

        var alwaysEvaluate = new BoundRecord(
            Center: Vector3.Zero,
            End: (segmentEnd + 1),
            InstanceIndex: instanceIndex,
            Instruction: segmentStart,
            Mode: BoundModeNone,
            Radius: 0f,
            Slot: 0
        );

        // The segment sphere: every instruction qualified AND the shape spheres are homogeneous (all static, or all
        // dynamic on the chain's one slot — a mixed segment would need two centers in one entry).
        if (
            !segmentEligible ||
            (shapeBounds.Count == firstShapeBound)
        ) {
            return alwaysEvaluate;
        }

        var segmentMode = shapeBounds[firstShapeBound].Mode;

        for (var index = (firstShapeBound + 1); (index < shapeBounds.Count); index++) {
            if (shapeBounds[index].Mode != segmentMode) {
                return alwaysEvaluate;
            }
        }

        // Combined sphere: anchored on the first shape's center (dynamic entries all share the pre-dynamic offset,
        // so the max simply widens the radius).
        var segmentCenter = shapeBounds[firstShapeBound].Center;
        var segmentRadius = shapeBounds[firstShapeBound].Radius;

        for (var index = (firstShapeBound + 1); (index < shapeBounds.Count); index++) {
            segmentRadius = MathF.Max(
                x: segmentRadius,
                y: (Vector3.Distance(
                    value1: shapeBounds[index].Center,
                    value2: segmentCenter
                ) + shapeBounds[index].Radius)
            );
        }

        return new BoundRecord(
            Center: segmentCenter,
            End: (segmentEnd + 1),
            InstanceIndex: instanceIndex,
            Instruction: segmentStart,
            Mode: segmentMode,
            Radius: segmentRadius,
            Slot: ((BoundModeDynamic == segmentMode)
            ? dynamicSlot
            : 0)
        );
    }
    // The ONE definition of a segment, read by the directory (AnalyzeBounds, so by both HLSL walks) and by
    // RequireSegmentsStartAtTheWorldPoint: the stream splits BEFORE each ResetPoint AND at every instance boundary
    // (BuildInstanceBoundaries: each instance's First and End, so an empty instance still splits where its two
    // neighbours meet), which keeps a segment from straddling two instances (or an instance and the WORLD set) and lets the
    // instance table say "this instance owns segments [a, b)" as a contiguous directory range. Each range is
    // [Start, End] inclusive.
    private List<(int Start, int End)> SegmentRanges() {
        var ranges = new List<(int Start, int End)>();
        var boundaries = BuildInstanceBoundaries();
        var start = 0;

        while (start < m_instructions.Length) {
            var end = start;

            while (
                ((end + 1) < m_instructions.Length) &&
                (m_instructions[(end + 1)].Op != SdfOp.ResetPoint) &&
                !boundaries.Contains(item: (end + 1))
            ) {
                end++;
            }

            ranges.Add(item: (start, end));

            start = (end + 1);
        }

        return ranges;
    }
    // The set of instruction indices that force a segment split: the FIRST instruction of some instance's range, or the
    // first instruction AFTER some instance's range ends (the second because the ended instance's last segment must not
    // swallow the next, unowned/differently-owned instruction). Built once, O(instances); the segment walk tests
    // membership per instruction, so a per-test linear scan of m_instances was O(instructions x instances).
    private HashSet<int> BuildInstanceBoundaries() {
        var boundaries = new HashSet<int>(capacity: (m_instances.Length * 2));

        foreach (var range in m_instances) {
            boundaries.Add(item: range.First);
            boundaries.Add(item: range.End);
        }

        return boundaries;
    }
    // The minimal sphere containing two spheres: one containing the other wins outright; otherwise the classic
    // segment-spanning enclosure.
    private static (Vector3 Center, float Radius) EncloseSpheres(Vector3 centerA, float radiusA, Vector3 centerB, float radiusB) {
        var distance = Vector3.Distance(
            value1: centerA,
            value2: centerB
        );

        if ((distance + radiusB) <= radiusA) {
            return (centerA, radiusA);
        }

        if ((distance + radiusA) <= radiusB) {
            return (centerB, radiusB);
        }

        var radius = (0.5f * ((distance + radiusA) + radiusB));

        return ((centerA + (Vector3.Normalize(value: (centerB - centerA)) * (radius - radiusA))), radius);
    }
    // AnalyzeBounds' merge test/production, extracted so its compaction pass can call it without shifting a list on
    // every attempt. `current` is the earlier (lower-index) segment, `next` the one immediately after it — the same
    // roles the inline version tested, so the accepted pairs and the merged record's fields (which side's Center
    // survives, which side's End wins) are unchanged.
    private static bool TryMergeAdjacentSegments(in BoundRecord current, in BoundRecord next, out BoundRecord merged) {
        if (
            (current.Mode != next.Mode) ||
            (current.InstanceIndex != next.InstanceIndex)
        ) {
            merged = default;

            return false;
        }

        if (BoundModeDynamic == current.Mode) {
            if (current.Slot != next.Slot) {
                merged = default;

                return false;
            }

            // Anchored on the first segment's center (the shared pre-dynamic offset); the enclosing max keeps it
            // conservative even if the offsets differ.
            merged = current with {
                End = next.End,
                Radius = MathF.Max(
                    x: current.Radius,
                    y: (Vector3.Distance(
                        value1: next.Center,
                        value2: current.Center
                    ) + next.Radius)
                ),
            };

            return true;
        }

        if (BoundModeStatic == current.Mode) {
            var (mergedCenter, mergedRadius) = EncloseSpheres(
                centerA: current.Center,
                radiusA: current.Radius,
                centerB: next.Center,
                radiusB: next.Radius
            );

            if (mergedRadius > (current.Radius + next.Radius)) {
                merged = default;

                return false;
            }

            merged = current with {
                Center = mergedCenter,
                End = next.End,
                Radius = mergedRadius,
            };

            return true;
        }

        merged = default;

        return false;
    }
}
