using System.Numerics;

using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The <see cref="SdfShapeType.Sweep"/> contract: the closed-form closest point on a known quadratic Bezier, the
/// radius profile at its three named parameters, the admission refusals <see cref="SdfProgramBuilder.Sweep"/>
/// enforces, and — the load-bearing proof — that the shape's own conservative margin
/// (<see cref="SdfProgramBuilder.SweepConservativeMargin"/>) keeps the field from overestimating true distance to a
/// fine-sampled reference tube, for both a single strand and a multi-strand braid, over a randomized grid.
/// </summary>
public sealed class SweepLawTests {
    private static Vector3 BezierDerivative(Vector3 a, Vector3 b, Vector3 c, float t) =>
        (2f * Vector3.Lerp(
            amount: t,
            value1: (b - a),
            value2: (c - b)
        ));
    private static Vector3 BezierPoint(Vector3 a, Vector3 b, Vector3 c, float t) => Vector3.Lerp(
        value1: Vector3.Lerp(
            amount: t,
            value1: a,
            value2: b
        ),
        value2: Vector3.Lerp(
            amount: t,
            value1: b,
            value2: c
        ),
        amount: t
    );
    private static SdfProgram BuildSweep(Vector3 a, Vector3 b, Vector3 c, float radiusStart, float radiusEnd, float bulge, int strands, float twist, float strandOffset) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder
            .ResetPoint()
            .Sweep(
            a: a,
            b: b,
            c: c,
            radiusStart: radiusStart,
            radiusEnd: radiusEnd,
            bulge: bulge,
            strands: strands,
            twist: twist,
            strandOffset: strandOffset,
            material: material
        )
            .Build();
    }
    // A C# mirror of the CLOSED-FORM closest-point-on-quadratic-bezier the shader implements (sdfSweepClosestT,
    // sdf-vm.hlsli) — an INDEPENDENT re-derivation from iq's construction, not a call into production code, so this
    // law tests the algorithm the render path actually runs.
    private static float ClosestT(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        var coefA = (b - a);
        var coefB = ((a - (2f * b)) + c);
        var coefC = (coefA * 2f);
        var d = (a - p);
        var bb = Vector3.Dot(
            vector1: coefB,
            vector2: coefB
        );

        if (bb < 1e-10f) {
            var ac = (c - a);
            var acLengthSquared = MathF.Max(
                x: Vector3.Dot(
                    vector1: ac,
                    vector2: ac
                ),
                y: 1e-20f
            );

            return Math.Clamp(
                value: (Vector3.Dot(
                    vector1: (p - a),
                    vector2: ac
                ) / acLengthSquared),
                min: 0f,
                max: 1f
            );
        }

        var kk = (1f / bb);
        var kx = (kk * Vector3.Dot(
            vector1: coefA,
            vector2: coefB
        ));
        var ky = ((kk * ((2f * Vector3.Dot(
            vector1: coefA,
            vector2: coefA
        )) + Vector3.Dot(
            vector1: d,
            vector2: coefB
        ))) / 3f);
        var kz = (kk * Vector3.Dot(
            vector1: d,
            vector2: coefA
        ));
        var p1 = (ky - (kx * kx));
        var p3 = ((p1 * p1) * p1);
        var q = ((kx * (((2f * kx) * kx) - (3f * ky))) + kz);
        var h = ((q * q) + (4f * p3));

        if (h >= 0f) {
            h = MathF.Sqrt(x: h);

            var xh = (((h - q)) * 0.5f);
            var xl = (((-h - q)) * 0.5f);
            var uv = new Vector2(
                x: (MathF.Sign(x: xh) * MathF.Pow(
                    x: MathF.Abs(x: xh),
                    y: (1f / 3f)
                )),
                y: (MathF.Sign(x: xl) * MathF.Pow(
                    x: MathF.Abs(x: xl),
                    y: (1f / 3f)
                ))
            );

            return Math.Clamp(
                max: 1f,
                min: 0f,
                value: ((uv.X + uv.Y) - kx)
            );
        }

        var z = MathF.Sqrt(x: -p1);
        var v = (MathF.Acos(x: Math.Clamp(
            max: 1f,
            min: -1f,
            value: (q / ((p1 * z) * 2f))
        )) / 3f);
        var m = MathF.Cos(x: v);
        var n = (MathF.Sin(x: v) * 1.7320508f);
        var t0 = Math.Clamp(
            max: 1f,
            min: 0f,
            value: (((m + m) * z) - kx)
        );
        var t1 = Math.Clamp(
            max: 1f,
            min: 0f,
            value: (((-n - m) * z) - kx)
        );
        var t2 = Math.Clamp(
            max: 1f,
            min: 0f,
            value: (((n - m) * z) - kx)
        );
        var q0 = (d + ((coefC + (coefB * t0)) * t0));
        var q1 = (d + ((coefC + (coefB * t1)) * t1));
        var q2 = (d + ((coefC + (coefB * t2)) * t2));
        var d0 = Vector3.Dot(
            vector1: q0,
            vector2: q0
        );
        var d1 = Vector3.Dot(
            vector1: q1,
            vector2: q1
        );
        var d2 = Vector3.Dot(
            vector1: q2,
            vector2: q2
        );
        var best = MathF.Min(
            x: d0,
            y: MathF.Min(
                x: d1,
                y: d2
            )
        );

        return ((best == d0)
            ? t0
            : ((best == d1)
                ? t1
                : t2
        ));
    }
    // The render field selects closest-t on the centerline, then the nearest strand there, minus its margin.
    private static float FieldDistance(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float radiusStart, float radiusEnd, float bulge, int strands, float twist, float strandOffset) {
        var t = ClosestT(
            a: a,
            b: b,
            c: c,
            p: p
        );
        var basePoint = BezierPoint(
            a: a,
            b: b,
            c: c,
            t: t
        );
        var radius = RadiusAt(
            bulge: bulge,
            radiusEnd: radiusEnd,
            radiusStart: radiusStart,
            t: t
        );
        var tangent = BezierDerivative(
            a: a,
            b: b,
            c: c,
            t: t
        );
        var tangentDirection = ((tangent.LengthSquared() > 1e-16f)
            ? Vector3.Normalize(value: tangent)
            : Vector3.UnitY
        );
        var referenceAxis = ((MathF.Abs(x: tangentDirection.Y) < 0.999f)
            ? Vector3.UnitY
            : Vector3.UnitX
        );
        var u = Vector3.Normalize(value: Vector3.Cross(
            vector1: tangentDirection,
            vector2: referenceAxis
        ));
        var v = Vector3.Cross(
            vector1: tangentDirection,
            vector2: u
        );
        var best = 1e9f; // sdfSweep's finite strand seed is part of the field, including at extreme distances.

        for (var strand = 0; (strand < strands); strand++) {
            var phase = (((t * twist) * MathF.Tau) + ((strand * MathF.Tau) / strands));
            var offsetPoint = (basePoint + (((u * MathF.Cos(x: phase)) + (v * MathF.Sin(x: phase))) * strandOffset));

            best = MathF.Min(
                x: best,
                y: (Vector3.Distance(
                    value1: p,
                    value2: offsetPoint
                ) - radius)
            );
        }

        var margin = SdfProgramBuilder.SweepConservativeMargin(
            bulge: bulge,
            radiusEnd: radiusEnd,
            radiusStart: radiusStart,
            strandOffset: strandOffset,
            twist: twist
        );

        return (best - margin);
    }
    private static Puck.Maths.FixedPosition FixedPositionOf(Vector3 value) => Puck.Maths.FixedPosition.FromLocal(local: new Puck.Maths.FixedVector3(
        X: Puck.Maths.FixedQ4816.FromDouble(value: value.X),
        Y: Puck.Maths.FixedQ4816.FromDouble(value: value.Y),
        Z: Puck.Maths.FixedQ4816.FromDouble(value: value.Z)
    ));
    private static float RadiusAt(float t, float radiusStart, float radiusEnd, float bulge) {
        var taper = float.Lerp(
            amount: t,
            value1: radiusStart,
            value2: radiusEnd
        );
        var s = MathF.Max(
            x: MathF.Sin(x: (MathF.PI * t)),
            y: 0f
        );

        return (taper + (bulge * MathF.Pow(
            x: s,
            y: 0.65f
        )));
    }
    // A fine-sampled brute-force reference: the true minimum of "distance to the strand curve minus the radius
    // profile" over a dense t grid and every strand — the ground truth the conservative margin must never exceed.
    private static float ReferenceDistance(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float radiusStart, float radiusEnd, float bulge, int strands, float twist, float strandOffset, int samples = 1200) {
        var best = float.PositiveInfinity;

        for (var i = 0; (i <= samples); i++) {
            var t = (i / ((float)samples));
            var basePoint = BezierPoint(
                a: a,
                b: b,
                c: c,
                t: t
            );
            var radius = RadiusAt(
                bulge: bulge,
                radiusEnd: radiusEnd,
                radiusStart: radiusStart,
                t: t
            );
            var tangent = BezierDerivative(
                a: a,
                b: b,
                c: c,
                t: t
            );
            var tangentDirection = ((tangent.LengthSquared() > 1e-16f)
                ? Vector3.Normalize(value: tangent)
                : Vector3.UnitY
            );
            var referenceAxis = ((MathF.Abs(x: tangentDirection.Y) < 0.999f)
                ? Vector3.UnitY
                : Vector3.UnitX
            );
            var u = Vector3.Normalize(value: Vector3.Cross(
                vector1: tangentDirection,
                vector2: referenceAxis
            ));
            var v = Vector3.Cross(
                vector1: tangentDirection,
                vector2: u
            );

            for (var strand = 0; (strand < strands); strand++) {
                var phase = (((t * twist) * MathF.Tau) + ((strand * MathF.Tau) / strands));
                var offsetPoint = (basePoint + (((u * MathF.Cos(x: phase)) + (v * MathF.Sin(x: phase))) * strandOffset));

                best = MathF.Min(
                    x: best,
                    y: (Vector3.Distance(
                        value1: p,
                        value2: offsetPoint
                    ) - radius)
                );
            }
        }

        return best;
    }
    private static Vector4 SweepBound(ReadOnlySpan<uint> words) {
        var index = checked((((int)((words[3] + (20u * words[1])) + 2u)) * 4));

        Assert.Equal(
            1u,
            words[(index + 4)]
        );
        return new Vector4(
            BitConverter.UInt32BitsToSingle(value: words[index]),
            BitConverter.UInt32BitsToSingle(value: words[(index + 1)]),
            BitConverter.UInt32BitsToSingle(value: words[(index + 2)]),
            BitConverter.UInt32BitsToSingle(value: words[(index + 3)])
        );
    }

    [Fact]
    public void ANonPositiveRadiusRefusesByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0f,
            radiusEnd: 0.1f,
            bulge: 0f,
            strands: 1,
            twist: 0f,
            strandOffset: 0f,
            material: material
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "radiusStart"
        );
    }
    [InlineData(0)]
    [InlineData(5)]
    [Theory]
    public void AStrandCountOutsideTheAdmittedRangeRefusesByName(int strands) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0f,
            strands: strands,
            twist: 0f,
            strandOffset: 0f,
            material: material
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "strands"
        );
    }
    [Fact]
    public void AZeroStrandOffsetSingleStrandIsTheExactTubeMinusMargin() {
        var a = Vector3.Zero;
        var b = new Vector3(
            x: 1f,
            y: 1f,
            z: 0f
        );
        var c = new Vector3(
            x: 2f,
            y: 0f,
            z: 0f
        );
        const float Radius = 0.2f;
        var p = new Vector3(
            x: 0.5f,
            y: 1.5f,
            z: 0.3f
        );

        var expected = (FieldDistance(
            a: a,
            b: b,
            bulge: 0f,
            c: c,
            p: p,
            radiusEnd: Radius,
            radiusStart: Radius,
            strandOffset: 0f,
            strands: 1,
            twist: 0f
        ));
        var t = ClosestT(
            a: a,
            b: b,
            c: c,
            p: p
        );
        var directTube = (Vector3.Distance(
            value1: p,
            value2: BezierPoint(
                a: a,
                b: b,
                c: c,
                t: t
            )
        ) - Radius);

        Assert.Equal(
            actual: expected,
            expected: directTube,
            precision: 5
        );
    }
    [Fact]
    public void BulgeExceedingTheAdmittedRatioRefusesByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: (SdfProgramBuilder.MaxSweepBulgeRatio * 0.2f),
            strands: 1,
            twist: 0f,
            strandOffset: 0f,
            material: material
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "bulge"
        );
    }
    [Fact]
    public void ClosestPointOnAKnownParabolaMatchesBruteForce() {
        // A = (-1, 0, 0), B = (0, 2, 0), C = (1, 0, 0): the bezier traces a symmetric arch. At p directly above the
        // apex, the closest point is exactly t = 0.5 by symmetry.
        var a = new Vector3(
            x: -1f,
            y: 0f,
            z: 0f
        );
        var b = new Vector3(
            x: 0f,
            y: 2f,
            z: 0f
        );
        var c = new Vector3(
            x: 1f,
            y: 0f,
            z: 0f
        );
        var p = new Vector3(
            x: 0f,
            y: 3f,
            z: 0f
        );

        var t = ClosestT(
            a: a,
            b: b,
            c: c,
            p: p
        );

        Assert.Equal(
            actual: t,
            expected: 0.5f,
            precision: 4
        );

        // Off-axis: brute-force a fine grid and confirm the closed form lands within one grid step.
        var probe = new Vector3(
            x: 0.6f,
            y: 1.3f,
            z: -0.2f
        );
        var bruteBestT = 0f;
        var bruteBestDistance = float.PositiveInfinity;

        for (var i = 0; (i <= 20000); i++) {
            var sampleT = (i / 20000f);
            var distance = Vector3.Distance(
                value1: probe,
                value2: BezierPoint(
                    a: a,
                    b: b,
                    c: c,
                    t: sampleT
                )
            );

            if (distance < bruteBestDistance) {
                bruteBestDistance = distance;
                bruteBestT = sampleT;
            }
        }

        var closedFormT = ClosestT(
            a: a,
            b: b,
            c: c,
            p: probe
        );

        Assert.True(condition: (MathF.Abs(x: (closedFormT - bruteBestT)) < 0.001f));
    }
    [InlineData(0f, 0f, 0f, 1, false, 0f)]
    [InlineData(0.2f, 0.2f, 4f, 3, false, 0f)]
    [InlineData(-0.2f, 0.1f, -3f, 4, false, 0f)]
    [InlineData(0f, 0.2f, 2f, 2, true, 0f)]
    [InlineData(0.2f, 0.2f, 4f, 3, false, 1000000f)]
    [InlineData(0f, 0.2f, 2f, 2, true, 1000000f)]
    [Theory]
    public void PackedSweepSphereCannotBeatTheActualCandidate(float bulge, float orbit, float twist, int strands, bool degenerate, float offset) {
        var a = new Vector3(
            x: (3f + offset),
            y: -1f,
            z: 2f
        );
        var b = (degenerate
            ? a
            : (a + new Vector3(
                x: 0f,
                y: 0.5f,
                z: 0.4f
            ))
        );
        var c = (degenerate
            ? a
            : (a + Vector3.UnitY)
        );
        var words = BuildSweep(
            a: a,
            b: b,
            bulge: bulge,
            c: c,
            radiusEnd: 0.4f,
            radiusStart: 0.1f,
            strandOffset: orbit,
            strands: strands,
            twist: twist
        ).Words;
        var bound = SweepBound(words: words);
        var center = new Vector3(
            x: bound.X,
            y: bound.Y,
            z: bound.Z
        );
        var random = new Random(Seed: 352);

        Assert.True(condition: (bound.W > 0f));
        for (var trial = 0; (trial < 400); trial++) {
            var direction = ((trial == 0)
                ? Vector3.UnitY
                : Vector3.Normalize(value: new Vector3(
                    x: (((float)random.NextDouble()) - 0.5f),
                    y: (((float)random.NextDouble()) - 0.5f),
                    z: (((float)random.NextDouble()) - 0.5f)
                ))
            );
            var p = (center + (direction * ((trial % 4) switch { 0 => 100f, 1 => 8f, 2 => 2f, _ => 0.1f })));
            var candidate = FieldDistance(
                a: a,
                b: b,
                bulge: bulge,
                c: c,
                p: p,
                radiusEnd: 0.4f,
                radiusStart: 0.1f,
                strandOffset: orbit,
                strands: strands,
                twist: twist
            );
            var lowerBound = (Vector3.Distance(
                value1: p,
                value2: center
            ) - bound.W);

            Assert.True(
                condition: (lowerBound <= (candidate + 1e-5f)),
                userMessage: $"Sweep sphere {lowerBound} exceeded candidate {candidate} at {p}"
            );
        }
    }
    [Fact]
    public void StrandOffsetExceedingTheAdmittedRatioRefusesByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0f,
            strands: 3,
            twist: 1f,
            strandOffset: (SdfProgramBuilder.MaxSweepStrandOffsetRatio * 0.2f),
            material: material
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "strandOffset"
        );
    }
    [InlineData(false, SdfBlendOp.Union, false, true)]
    [InlineData(true, SdfBlendOp.Union, false, true)]
    [InlineData(false, SdfBlendOp.Subtraction, false, false)]
    [InlineData(false, SdfBlendOp.Union, true, false)]
    [Theory]
    public void SweepBoundsRespectPoseBlendAndNonRigidFallback(bool dynamic, SdfBlendOp blend, bool scaled, bool bounded) {
        var original = BuildSweep(
            Vector3.Zero,
            Vector3.UnitY,
            (2f * Vector3.UnitY),
            0.1f,
            0.1f,
            0f,
            1,
            0f,
            0f
        );
        var instructions = new List<SdfInstruction> { original.Instructions[0] };

        if (dynamic) {
            instructions.Add(item: original.Instructions[0] with { Op = SdfOp.TransformDynamic, Data0 = new Vector4(
                w: 0f,
                x: 7f,
                y: 0f,
                z: 0f
            ) });
        }
        if (scaled) {
            instructions.Add(item: original.Instructions[0] with { Op = SdfOp.Scale, Data0 = new Vector4(
                w: 0f,
                x: 2f,
                y: 3f,
                z: 4f
            ) });
        }
        var shapeIndex = instructions.Count;

        instructions.Add(item: original.Instructions[1] with { Blend = ((uint)blend), Detail = true, Secondary = false });
        var words = new SdfProgram(
            instructions: instructions,
            materials: [new SdfMaterial(Albedo: Vector3.One)],
            sweepCurves: [new SdfSweepCurve(
                    InstructionIndex: shapeIndex,
                    A: Vector3.Zero,
                    B: Vector3.UnitY,
                    C: (2f * Vector3.UnitY),
                    RadiusStart: 0.1f,
                    RadiusEnd: 0.1f,
                    Bulge: 0f
                )]
        ).Words;
        var shapeBound = checked((((int)((words[3] + (20u * words[1])) + (2u * ((uint)shapeIndex)))) * 4));

        Assert.Equal(
            (bounded
            ? (dynamic
                ? 2u
                : 1u)
            : 0u),
            words[(shapeBound + 4)]
        );
        if (!bounded) {
            return;
        }
        Assert.Equal(
            (dynamic
            ? 7u
            : 0u),
            words[(shapeBound + 5)]
        );
        var segmentHeader = checked((((int)((words[3] + (20u * words[1])) + (2u * words[0]))) * 4));
        var plan = checked((((int)words[(segmentHeader + 2)]) * 4));
        var leaf = checked((((int)words[plan]) * 4));

        Assert.True(condition: (BitConverter.UInt32BitsToSingle(value: words[(leaf + 11)]) > 1.1f));
        Assert.Equal(
            1f,
            BitConverter.UInt32BitsToSingle(value: words[(leaf + 9)])
        );
        Assert.Equal(
            ((uint)shapeIndex),
            words[(leaf + 3)] & 0x7FFFFFFFu
        );
        Assert.Equal(
            ((uint)SdfShapeType.Sweep) | 0xC0000000u,
            words[(((shapeIndex + 1) * 4) + 1)]
        );
    }
    [InlineData(16f, true)]
    [InlineData(17f, false)]
    [InlineData(64f, false)]
    [Theory]
    public void SweepCapOnlyAdmitsMarginsWithRoundingSlack(float bulge, bool bounded) {
        var words = BuildSweep(
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            4f,
            4f,
            bulge,
            1,
            0f,
            0f
        ).Words;
        var boundIndex = checked((((int)((words[3] + (20u * words[1])) + 2u)) * 4));

        Assert.Equal(
            (bounded
            ? 1u
            : 0u),
            words[(boundIndex + 4)]
        );
        var candidate = FieldDistance(
            new Vector3(
                x: 2e9f,
                y: 0f,
                z: 0f
            ),
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            4f,
            4f,
            bulge,
            1,
            0f,
            0f
        );

        Assert.Equal(
            actual: candidate,
            expected: (1e9f - bulge)
        );
    }
    [Fact]
    public void TaperExceedingTheAdmittedRatioRefusesByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.01f,
            radiusEnd: (0.01f + (SdfProgramBuilder.MaxSweepTaperRatio * 0.02f)),
            bulge: 0f,
            strands: 1,
            twist: 0f,
            strandOffset: 0f,
            material: material
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "radiusEnd"
        );
    }
    [Fact]
    public void TheConservativeMarginStaysBelowTrueDistanceOnAGridForOneStrand() {
        var a = new Vector3(
            x: 0.12f,
            y: 4.148f,
            z: 0.325f
        );
        var b = new Vector3(
            x: -0.16f,
            y: 4.17f,
            z: 0.57f
        );
        var c = new Vector3(
            x: -0.387f,
            y: 3.844f,
            z: 0.38f
        );
        const float RadiusStart = 0.05f;
        const float RadiusEnd = 0.05f;
        const float Bulge = 0.05f;
        const int Strands = 1;
        const float Twist = 0f;
        const float StrandOffset = 0f;
        var random = new Random(Seed: 1);

        for (var trial = 0; (trial < 400); trial++) {
            var t = ((float)random.NextDouble());
            var center = BezierPoint(
                a: a,
                b: b,
                c: c,
                t: t
            );
            var radius = RadiusAt(
                bulge: Bulge,
                radiusEnd: RadiusEnd,
                radiusStart: RadiusStart,
                t: t
            );
            var direction = Vector3.Normalize(value: new Vector3(
                x: (((float)random.NextDouble()) - 0.5f),
                y: (((float)random.NextDouble()) - 0.5f),
                z: (((float)random.NextDouble()) - 0.5f)
            ));
            var offset = MathF.Max(
                x: 0f,
                y: (radius + ((((float)random.NextDouble()) - 0.5f) * radius))
            );
            var p = (center + (direction * offset));

            var fieldDistance = FieldDistance(
                a: a,
                b: b,
                bulge: Bulge,
                c: c,
                p: p,
                radiusEnd: RadiusEnd,
                radiusStart: RadiusStart,
                strandOffset: StrandOffset,
                strands: Strands,
                twist: Twist
            );
            var trueDistance = ReferenceDistance(
                p: p,
                a: a,
                b: b,
                c: c,
                radiusStart: RadiusStart,
                radiusEnd: RadiusEnd,
                bulge: Bulge,
                strands: Strands,
                twist: Twist,
                strandOffset: StrandOffset
            );

            Assert.True(
                condition: (fieldDistance <= (trueDistance + 1e-4f)),
                userMessage: $"trial {trial}: field {fieldDistance} exceeded true distance {trueDistance} at p={p}"
            );
        }
    }
    [Fact]
    public void TheConservativeMarginStaysBelowTrueDistanceOnAGridForThreeStrands() {
        var a = new Vector3(
            x: -0.42f,
            y: 3.48f,
            z: -0.30f
        );
        var b = new Vector3(
            x: -0.77f,
            y: 3.40f,
            z: -0.74f
        );
        var c = new Vector3(
            x: -1.12f,
            y: 3.32f,
            z: -1.18f
        );
        const float RadiusStart = 0.060f;
        const float RadiusEnd = 0.046f;
        const float Bulge = 0f;
        const int Strands = 3;
        const float Twist = 4f;
        const float StrandOffset = 0.06f;
        var random = new Random(Seed: 2);

        for (var trial = 0; (trial < 400); trial++) {
            var t = ((float)random.NextDouble());
            var center = BezierPoint(
                a: a,
                b: b,
                c: c,
                t: t
            );
            var radius = RadiusAt(
                bulge: Bulge,
                radiusEnd: RadiusEnd,
                radiusStart: RadiusStart,
                t: t
            );
            var reach = (radius + StrandOffset);
            var direction = Vector3.Normalize(value: new Vector3(
                x: (((float)random.NextDouble()) - 0.5f),
                y: (((float)random.NextDouble()) - 0.5f),
                z: (((float)random.NextDouble()) - 0.5f)
            ));
            var offset = MathF.Max(
                x: 0f,
                y: (reach + ((((float)random.NextDouble()) - 0.5f) * reach))
            );
            var p = (center + (direction * offset));

            var fieldDistance = FieldDistance(
                a: a,
                b: b,
                bulge: Bulge,
                c: c,
                p: p,
                radiusEnd: RadiusEnd,
                radiusStart: RadiusStart,
                strandOffset: StrandOffset,
                strands: Strands,
                twist: Twist
            );
            var trueDistance = ReferenceDistance(
                p: p,
                a: a,
                b: b,
                c: c,
                radiusStart: RadiusStart,
                radiusEnd: RadiusEnd,
                bulge: Bulge,
                strands: Strands,
                twist: Twist,
                strandOffset: StrandOffset
            );

            Assert.True(
                condition: (fieldDistance <= (trueDistance + 1e-4f)),
                userMessage: $"trial {trial}: field {fieldDistance} exceeded true distance {trueDistance} at p={p}"
            );
        }
    }
    [Fact]
    public void TheFixedPointEvaluatorAcceptsOneStrand() {
        var program = BuildSweep(
            a: Vector3.Zero,
            b: new Vector3(
                x: 0.5f,
                y: 1f,
                z: 0f
            ),
            c: new Vector3(
                x: 1f,
                y: 0f,
                z: 0f
            ),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0.05f,
            strands: 1,
            twist: 0.5f,
            strandOffset: 0.03f
        );
        var evaluator = new SdfFieldEvaluator(program: program);
        var probe = new Vector3(
            x: 0.5f,
            y: 0.4f,
            z: 0.1f
        );

        var found = evaluator.TryDistance(
            position: FixedPositionOf(value: probe),
            distance: out var distance,
            material: out _
        );

        Assert.True(condition: found);

        var expectedFloat = FieldDistance(
            p: probe,
            a: Vector3.Zero,
            b: new Vector3(
                x: 0.5f,
                y: 1f,
                z: 0f
            ),
            c: new Vector3(
                x: 1f,
                y: 0f,
                z: 0f
            ),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0.05f,
            strands: 1,
            twist: 0.5f,
            strandOffset: 0.03f
        );

        Assert.True(condition: (MathF.Abs(x: (((float)distance) - expectedFloat)) < 0.02f));
    }
    [Fact]
    public void TheFixedPointEvaluatorRefusesMoreThanOneStrandByName() {
        var program = BuildSweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0f,
            strands: 3,
            twist: 1f,
            strandOffset: 0.05f
        );
        var exception = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "Sweep"
        );
    }
    [InlineData(0f, 0.1f, 0.5f, 0f, 0.1f)]
    [InlineData(0.5f, 0.1f, 0.5f, 0.05f, 0.35f)]
    [InlineData(1f, 0.1f, 0.5f, 0f, 0.5f)]
    [Theory]
    public void TheRadiusProfileMatchesItsFormulaAtNamedParameters(float t, float radiusStart, float radiusEnd, float bulge, float expected) {
        var radius = RadiusAt(
            bulge: bulge,
            radiusEnd: radiusEnd,
            radiusStart: radiusStart,
            t: t
        );

        Assert.Equal(
            actual: radius,
            expected: expected,
            precision: 5
        );
    }
}
