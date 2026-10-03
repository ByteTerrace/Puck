using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Exhaustive certified CPU pruning decisions over the counters cameras, without a GPU beam readback.</summary>
public sealed partial class SdfTapePredictionLawTests(ITestOutputHelper output) {
    private const int Width = 1440;
    private const int Height = 810;
    private const int Tile = 16;
    private const int Slabs = 8;
    private const int Partitions = 3;

    private static readonly FixedInterval Zero = I(value: 0);
    private static readonly FixedInterval One = I(value: 1);

    [InlineData("nexus")]
    [InlineData("courtyard")]
    [Theory]
    public void EveryCountersTileReportsItsCertifiedPruningDecisions(string workload) {
        var path = $"tests/Puck.Counters/{workload}.world.json";
        var definition = AuthoredGameFixtures.Load(relativePath: path);
        var frame = ComposedSdfWorldFixture.Capture(definition: definition, relativePath: path);
        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: 0.9f, position: new Vector3(x: 0, y: 2.5f, z: 7),
            target: new Vector3(x: 0, y: 0.8f, z: 0), viewportWidth: Width, viewportHeight: Height);
        var far = WorldRenderFarDistance.Resolve(defaults: definition.Render);
        var model = Candidates(frame: frame);
        var bounds = Instances(frame: frame);
        var shapes = model.Count(predicate: static entry => (entry.Candidate is not null));
        var certified = model.Count(predicate: static entry => ((entry.Candidate is { } candidate) && ((candidate.Certificate.Flags & SdfTapeCertificate.Certified) != 0)));
        var totals = new Totals[Partitions, Slabs];
        var studyPartitions = StudyPartitions();
        var studyTotals = new StudyTotals[studyPartitions.Length];
        var radiusTotals = new RadiusTotals[Slabs];
        var mask = new bool[frame.Program.InstructionCount];
        var retained = new bool[frame.Program.InstructionCount];
        var zeroRadiusRetained = new bool[frame.Program.InstructionCount];
        var tiles = 0;
        var maskRejected = 0L;
        var belowThreshold = 0;
        var maskedKinds = new SortedDictionary<string, long>(comparer: StringComparer.Ordinal);

        for (var y = 0; (y < Height); y += Tile) {
            for (var x = 0; (x < Width); x += Tile) {
                var cone = Cone(camera: camera, x: x, y: y);

                Array.Fill(array: mask, value: true);
                foreach (var bound in bounds) {
                    if (!Visible(bound: bound, origin: camera.Position, cone: cone)) {
                        Array.Fill(array: mask, value: false, startIndex: bound.First, count: (bound.End - bound.First));
                    }
                }
                var selected = model.Where(predicate: entry => mask[entry.Index]).ToArray();
                var maskedShapes = selected.Count(predicate: static entry => (entry.Candidate is not null));

                foreach (var entry in selected) {
                    if (entry.Candidate is { } candidate) {
                        maskedKinds[candidate.Category] = (maskedKinds.GetValueOrDefault(key: candidate.Category) + 1);
                    }
                }
                maskRejected += (shapes - maskedShapes);
                var instructions = mask.Count(predicate: static visible => visible);

                tiles++;
                if (instructions < SdfProgram.TapeInstructionThreshold) {
                    belowThreshold++;
                    continue;
                }
                for (var partition = 0; (partition < Partitions); partition++) {
                    for (var slab = 0; (slab < Slabs); slab++) {
                        var ball = Ball(camera: camera, cone: cone, depthPower: (1 << partition), far: far, slab: slab);
                        var sample = Walk(entries: selected, ball: ball, retained: retained);

                        totals[partition, slab].Add(sample: sample, radius: ball.Radius);
                        studyTotals[partition].Add(sample: sample, depthUnits: studyPartitions[partition].Width(slab: slab));
                        if (partition == 2) {
                            var zeroRadius = Walk(entries: selected, ball: ball with { Radius = 0 }, retained: zeroRadiusRetained);

                            radiusTotals[slab].Add(other: CompareRadius(entries: selected, normal: sample,
                                retained: retained, zeroRadius: zeroRadius, zeroRadiusRetained: zeroRadiusRetained));
                        }
                    }
                }
                for (var experiment = Partitions; (experiment < studyPartitions.Length); experiment++) {
                    var partition = studyPartitions[experiment];

                    for (var slab = 0; (slab < partition.SlabCount); slab++) {
                        var ball = Ball(camera: camera, cone: cone, far: far,
                            s0: (((float)partition.DepthUnits[slab]) / StudyDepthUnits),
                            s1: (((float)partition.DepthUnits[(slab + 1)]) / StudyDepthUnits));
                        var sample = Walk(entries: selected, ball: ball, retained: retained);

                        studyTotals[experiment].Add(sample: sample, depthUnits: partition.Width(slab: slab));
                    }
                }
            }
        }

        output.WriteLine(message: $"{workload}: camera=(0,2.5,7)->(0,0.8,0) extent={Width}x{Height} tiles={tiles} slabs={Slabs} far={far} shapes={shapes} certified={certified} maskRejected={maskRejected} belowThresholdTiles={belowThreshold}");
        output.WriteLine(message: "Coverage: every tile and slab; certified cone mask; near-plane entry and authored far fallback. No GPU beam outputs are assumed. Counts are exact for these CPU enclosures, not a backend-bit prediction or a march-weighted counter projection.");
        output.WriteLine(message: "The production partition is quartic; uniform and quadratic partitions are diagnostic comparisons.");
        output.WriteLine(message: "Every quartic slab also walks its identical centre at radius zero. radiusOnlyLive counts normal retained shapes absent from that point mask; remainingOverlap excludes both those shapes and unsupported candidates.");
        foreach (var kind in maskedKinds) { output.WriteLine(message: $"masked {kind.Value}: {kind.Key}"); }
        var droppedByPartition = new long[Partitions];

        for (var partition = 0; (partition < Partitions); partition++) {
            var sum = new Totals();
            var radiusSum = new RadiusTotals();

            for (var slab = 0; (slab < Slabs); slab++) {
                var count = totals[partition, slab];

                sum.Add(other: count);
                output.WriteLine(message: $"{PartitionName(partition: partition)} slab={slab} considered={count.Considered} finite={count.Finite} dropped={count.Dropped} unsupportedLive={count.UnsupportedLive} overlapLive={count.OverlapLive} radius=[{count.MinRadius:R},{count.MaxRadius:R}] rejects=(certificate:{count.NoCertificate},transform:{count.NoTransform},domain:{count.DomainRejected},primitive:{count.PrimitiveRejected})");
                if (partition == 2) {
                    var radiusCount = radiusTotals[slab];

                    radiusSum.Add(other: radiusCount);
                    ReportRadius(label: $"quartic slab={slab}", normal: count, radius: radiusCount);
                }
            }
            output.WriteLine(message: $"{PartitionName(partition: partition)} total considered={sum.Considered} finite={sum.Finite} dropped={sum.Dropped} certifiedFraction={(((double)sum.Dropped) / sum.Considered):P6} undecidable={sum.OverlapLive} unsupported={sum.UnsupportedLive} possibleFraction=[{(((double)sum.Dropped) / sum.Considered):P6},{(((double)(sum.Dropped + sum.OverlapLive)) / sum.Considered):P6}]");
            Assert.Equal(actual: ((sum.Dropped + sum.OverlapLive) + sum.UnsupportedLive), expected: sum.Considered);
            Assert.Equal(actual: (sum.Finite + sum.UnsupportedLive), expected: sum.Considered);
            Assert.Equal(actual: (((sum.NoCertificate + sum.NoTransform) + sum.DomainRejected) + sum.PrimitiveRejected), expected: sum.UnsupportedLive);
            if (partition == 2) {
                ReportRadius(label: "quartic total", normal: sum, radius: radiusSum);
                Assert.True(condition: (radiusSum.RadiusOnlyLive > 0), userMessage: "The exhaustive camera coverage must exercise candidates retained only by ball radius.");
            }
            droppedByPartition[partition] = sum.Dropped;
        }
        ReportStudy(program: frame.Program, tiles: tiles, partitions: studyPartitions, totals: studyTotals);
        DiagnoseTiles(camera: camera, far: far, entries: model, bounds: bounds, instructions: frame.Program.InstructionCount);
        Assert.True(condition: (droppedByPartition[2] > 0), userMessage: "No certified candidate lost in the production quartic partition's exhaustive camera coverage.");
        Assert.Equal(actual: tiles, expected: 4590);
    }

    private static string PartitionName(int partition) => partition switch { 0 => "uniform", 1 => "quadratic", _ => "quartic" };
    private static Sample Walk(Entry[] entries, BallDomain ball, bool[] retained, bool tighten = false,
        Action<Entry, Range, FixedInterval>? trace = null) {
        Array.Clear(array: retained);
        var current = Range.Point(value: 1e9f);
        var saved = Range.Unknown;
        var prefix = 0;
        var scopeStart = 0;
        var scopeKnown = true;
        var considered = 0;
        var finite = 0;
        var unsupported = 0;
        var rejections = new long[5];

        foreach (var entry in entries) {
            if (entry.Op == SdfOp.PushField) {
                saved = current;
                current = Range.Point(value: 1e9f);
                prefix = (entry.Index + 1);
                scopeStart = prefix;
                scopeKnown = true;
                continue;
            }
            if (entry.Op == SdfOp.PopField) {
                var child = current.Scale(scale: ((entry.Scale > 0f) ? entry.Scale : 1f));
                var scopeOffered = ((entry.Blend is SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction) ? child.Negate() : child);
                var modeled = (entry.Blend is SdfBlendOp.Union or SdfBlendOp.SmoothUnion or SdfBlendOp.Intersection
                    or SdfBlendOp.SmoothIntersection or SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction);
                var scopeLoses = (scopeKnown && modeled && Loses(current: saved, candidate: scopeOffered, blend: entry.Blend, smooth: entry.Smooth));

                if (scopeLoses) {
                    Array.Clear(array: retained, index: scopeStart, length: (entry.Index - scopeStart));
                    current = saved;
                } else {
                    current = Combine(current: saved, candidate: child, blend: entry.Blend, smooth: entry.Smooth);
                }
                prefix = (entry.Index + 1);
                continue;
            }
            if (entry.Candidate is not { } shape) {
                if (entry.Op == SdfOp.Dilate) {
                    current = current.Add(amount: -I(value: entry.Scale));
                } else if (entry.Op == SdfOp.Onion) {
                    current = current.Absolute().Add(amount: -I(value: entry.Scale));
                } else {
                    current = Range.Unknown;
                    scopeKnown = false;
                }
                prefix = (entry.Index + 1);
                continue;
            }
            considered++;
            var candidate = shape.Bounds(ball: ball, rejection: out var rejection);

            if (tighten && !candidate.IsUnbounded) { candidate = shape.Tighten(ball: ball, centred: candidate); }
            trace?.Invoke(arg1: entry, arg2: current, arg3: candidate);

            rejections[((int)rejection)]++;
            var known = !candidate.IsUnbounded;

            finite += (known ? 1 : 0);
            unsupported += (known ? 0 : 1);
            scopeKnown &= known;
            var offered = Range.Of(value: candidate);
            var union = (entry.Blend is SdfBlendOp.Union or SdfBlendOp.SmoothUnion);
            var maximum = (entry.Blend is SdfBlendOp.Intersection or SdfBlendOp.SmoothIntersection or SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction);

            if (entry.Blend is SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction) {
                offered = offered.Negate();
            }
            if (!union && !maximum) {
                current = Range.Unknown;
                retained[entry.Index] = true;
                prefix = (entry.Index + 1);
                scopeKnown = false;
                continue;
            }
            var loses = (known && Loses(current: current, candidate: offered, blend: entry.Blend, smooth: entry.Smooth));

            if (loses) { continue; }
            retained[entry.Index] = true;
            var wins = (known && Loses(current: offered, candidate: current, blend: entry.Blend, smooth: entry.Smooth));

            if (wins) {
                Array.Clear(array: retained, index: prefix, length: (entry.Index - prefix));
                current = offered;
            } else {
                current = Combine(current: current, candidate: offered, blend: entry.Blend, smooth: entry.Smooth, candidateSigned: true);
            }
            if (!union || !known) { prefix = (entry.Index + 1); }
        }
        var live = entries.Count(predicate: entry => ((entry.Candidate is not null) && retained[entry.Index]));

        return new Sample(Considered: considered, Finite: finite, Dropped: (considered - live), UnsupportedLive: unsupported,
            OverlapLive: (live - unsupported), NoCertificate: rejections[((int)Rejection.Certificate)], NoTransform: rejections[((int)Rejection.Transform)],
            DomainRejected: rejections[((int)Rejection.Domain)], PrimitiveRejected: rejections[((int)Rejection.Primitive)]);
    }

    private readonly record struct Sample(long Considered, long Finite, long Dropped, long UnsupportedLive, long OverlapLive,
        long NoCertificate, long NoTransform, long DomainRejected, long PrimitiveRejected);
    private struct Totals {
        public long Considered, Finite, Dropped, UnsupportedLive, OverlapLive;
        public long NoCertificate, NoTransform, DomainRejected, PrimitiveRejected;
        public float MinRadius, MaxRadius;

        public void Add(Sample sample, float radius) {
            if (Considered == 0) { MinRadius = radius; }
            MinRadius = Math.Min(val1: MinRadius, val2: radius);
            MaxRadius = Math.Max(val1: MaxRadius, val2: radius);
            Considered += sample.Considered; Finite += sample.Finite; Dropped += sample.Dropped;
            UnsupportedLive += sample.UnsupportedLive; OverlapLive += sample.OverlapLive;
            NoCertificate += sample.NoCertificate; NoTransform += sample.NoTransform;
            DomainRejected += sample.DomainRejected; PrimitiveRejected += sample.PrimitiveRejected;
        }
        public void Add(Totals other) => Add(sample: new Sample(Considered: other.Considered, DomainRejected: other.DomainRejected,
            Dropped: other.Dropped, Finite: other.Finite, NoCertificate: other.NoCertificate,
            NoTransform: other.NoTransform, OverlapLive: other.OverlapLive, PrimitiveRejected: other.PrimitiveRejected,
            UnsupportedLive: other.UnsupportedLive), radius: other.MaxRadius);
    }
}
