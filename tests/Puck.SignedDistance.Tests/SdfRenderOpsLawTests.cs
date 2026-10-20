using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The render reading (<see cref="SdfFieldReading.Render"/>) interprets every op the renderer draws but no contact field
/// reads (<see cref="SdfFieldEvaluator.RenderWarpOps"/>) as <c>mapCore</c>'s case does. Each law writes the kernel's own
/// formula in single precision, the hashed ones over the same integer PCG3D the kernel keys its cells and corners on, and
/// reads the fixed-point answer against it on a lattice through a box and the space about it; a point the formula puts
/// within a seam's rounding (a sector wall, a cell wall, a shell boundary) is left out, since the two sides may round it
/// onto different copies. The box bounds enclose every point answer under each op, and the contact reading refuses each
/// op by name.
/// </summary>
public sealed class SdfRenderOpsLawTests {
    private const float Tolerance = 2e-3f;
    private static readonly Vector3 HalfExtents = new(x: 0.2f, y: 0.4f, z: 0.1f);

    private static IEnumerable<Vector3> Points() {
        for (var x = -3; (x <= 3); x++) {
            for (var y = -3; (y <= 3); y++) {
                for (var z = -3; (z <= 3); z++) {
                    yield return new Vector3(x: (0.11f * x), y: (0.17f * y), z: (0.07f * z));
                }
            }
        }
    }
    private static SdfProgram Program(Func<SdfProgramBuilder, SdfProgramBuilder> chain) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return chain(arg: builder.ResetPoint()).Box(halfExtents: HalfExtents, material: material, round: 0f).Build();
    }
    private static SdfProgram FieldProgram(Func<SdfProgramBuilder, SdfProgramBuilder> after) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return after(arg: builder.ResetPoint().Box(halfExtents: HalfExtents, material: material, round: 0f)).Build();
    }
    private static float Box(Vector3 p) {
        var q = (Vector3.Abs(value: p) - HalfExtents);

        return (Vector3.Max(value1: q, value2: Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f));
    }
    private static FixedPosition Fixed(Vector3 point) => FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point));
    private static float Read(SdfFieldEvaluator field, Vector3 point) {
        Assert.True(condition: field.TryDistance(distance: out var distance, material: out _, position: Fixed(point: point)));

        return (float)(double)distance;
    }
    // Reads every lattice point against the reference, skipping the points the reference marks NaN, and reports how
    // many it read.
    private static void AssertAnswers(SdfProgram program, Func<Vector3, float> reference, float tolerance = Tolerance) {
        var field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);
        var read = 0;

        Assert.False(condition: field.Capabilities.WarpFree);
        foreach (var point in Points()) {
            var expected = reference(arg: point);

            if (float.IsNaN(f: expected)) { continue; }

            var actual = Read(field: field, point: point);

            read++;
            Assert.True(condition: (MathF.Abs(x: (actual - expected)) <= tolerance), userMessage: $"at {point}: the render reading answers {actual}, the kernel's formula {expected}");
        }

        Assert.True(condition: (read >= (Points().Count() / 2)), userMessage: $"only {read} lattice points lay clear of the op's seams");
    }
    private static void AssertBoundsEnclose(SdfProgram program) {
        var field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);
        var low = new Vector3(x: -0.15f, y: -0.3f, z: -0.12f);
        var high = new Vector3(x: 0.25f, y: 0.2f, z: 0.05f);

        Assert.True(condition: field.TryDistanceBounds(distance: out var bounds, lower: Fixed(point: low), upper: Fixed(point: high)));
        for (var corner = 0; (corner < 8); corner++) {
            var point = new Vector3(x: (((corner & 1) == 0) ? low.X : high.X), y: (((corner & 2) == 0) ? low.Y : high.Y), z: (((corner & 4) == 0) ? low.Z : high.Z));

            Assert.True(condition: field.TryDistance(distance: out var distance, material: out _, position: Fixed(point: point)));
            Assert.True(condition: (bounds.IsUnbounded || ((bounds.Lower <= distance) && (distance <= bounds.Upper))), userMessage: $"at {point}: {distance} lies outside the box's bounds {bounds}");
        }
    }
    private static void AssertContactRefuses(SdfProgram program) =>
        _ = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));
    // Whether a value lies within reach of the rounding seam between two integers.
    private static bool NearHalf(float value, float reach = 2e-3f) => (MathF.Abs(x: (value - MathF.Floor(x: value)) - 0.5f) < reach);
    // The kernel's float fraction of a hash.
    private static float Fraction(uint hash) => (hash * (1f / 4294967296f));
    // sdfValueNoise3 in single precision over the shared integer hash.
    private static float ValueNoise3(Vector3 q, uint seedX, uint seedY, uint seedZ) {
        var cellFloor = new Vector3(x: MathF.Floor(x: q.X), y: MathF.Floor(x: q.Y), z: MathF.Floor(x: q.Z));
        var cell = ((int)cellFloor.X, (int)cellFloor.Y, (int)cellFloor.Z);
        var f = (q - cellFloor);
        var u = (f * f * f * ((f * ((f * 6f) - new Vector3(value: 15f))) + new Vector3(value: 10f)));

        float Corner(int dx, int dy, int dz) =>
            Fraction(hash: Pcg3dLatticeNoise.Pcg3d(x: (unchecked((uint)(cell.Item1 + dx)) ^ seedX), y: (unchecked((uint)(cell.Item2 + dy)) ^ seedY), z: (unchecked((uint)(cell.Item3 + dz)) ^ seedZ)).X);

        var x00 = float.Lerp(value1: Corner(dx: 0, dy: 0, dz: 0), value2: Corner(dx: 1, dy: 0, dz: 0), amount: u.X);
        var x10 = float.Lerp(value1: Corner(dx: 0, dy: 1, dz: 0), value2: Corner(dx: 1, dy: 1, dz: 0), amount: u.X);
        var x01 = float.Lerp(value1: Corner(dx: 0, dy: 0, dz: 1), value2: Corner(dx: 1, dy: 0, dz: 1), amount: u.X);
        var x11 = float.Lerp(value1: Corner(dx: 0, dy: 1, dz: 1), value2: Corner(dx: 1, dy: 1, dz: 1), amount: u.X);

        return ((float.Lerp(value1: float.Lerp(value1: x00, value2: x10, amount: u.Y), value2: float.Lerp(value1: x01, value2: x11, amount: u.Y), amount: u.Z) * 2f) - 1f);
    }

    [Fact]
    public void ShearAddsTheDriversCubicToTheTarget() {
        const float Linear = 0.3f;
        const float Quadratic = 0.6f;
        const float Cubic = -0.4f;
        var program = Program(chain: static builder => builder.Shear(cubic: Cubic, driver: SdfAxis.Y, linear: Linear, quadratic: Quadratic, target: SdfAxis.X));

        AssertAnswers(program: program, reference: static point => {
            var t = point.Y;

            return Box(p: new Vector3(x: (point.X + ((((Cubic * t) + Quadratic) * t + Linear) * t)), y: point.Y, z: point.Z));
        });
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [Fact]
    public void GaussianPushMovesThePointByTheWeightedPush() {
        var center = new Vector3(x: 0.1f, y: 0.2f, z: -0.05f);
        var radii = new Vector3(x: 0.15f, y: 0.25f, z: 0.2f);
        var push = new Vector3(x: 0.08f, y: -0.05f, z: 0.07f);
        var program = Program(chain: builder => builder.GaussianPush(center: center, push: push, radii: radii));

        AssertAnswers(program: program, reference: point => {
            var offset = ((point - center) / radii);

            return Box(p: (point - (push * MathF.Exp(x: -Vector3.Dot(vector1: offset, vector2: offset)))));
        });
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [Fact]
    public void DomainWarpMovesEachAxisByTheNextAxissSine() {
        var frequency = new Vector3(x: 3f, y: 5f, z: 7f);
        const float Amplitude = 0.04f;
        var program = Program(chain: builder => builder.DomainWarp(amplitude: Amplitude, frequency: frequency));

        AssertAnswers(program: program, reference: point => Box(p: (point + (Amplitude * new Vector3(x: MathF.Sin(x: (frequency.X * point.Y)), y: MathF.Sin(x: (frequency.Y * point.Z)), z: MathF.Sin(x: (frequency.Z * point.X)))))));
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [InlineData(SdfAxis.Y, false)]
    [InlineData(SdfAxis.Y, true)]
    [InlineData(SdfAxis.X, false)]
    [InlineData(SdfAxis.Z, true)]
    [Theory]
    public void RepeatPolarFoldsThePlaneIntoSectors(SdfAxis axis, bool mirror) {
        const int Count = 5;
        const float Angle = (MathF.Tau / Count);
        var program = Program(chain: builder => builder.RepeatPolar(axis: axis, count: Count, mirror: mirror).Translate(offset: new Vector3(x: 0.15f, y: 0.05f, z: 0.1f)));

        AssertAnswers(program: program, reference: point => {
            var (pu, pv) = axis switch {
                SdfAxis.X => (point.Y, point.Z),
                SdfAxis.Z => (point.X, point.Y),
                _ => (point.X, point.Z),
            };
            var a = (MathF.Atan2(y: pv, x: pu) + (0.5f * Angle));
            var r = MathF.Sqrt(x: ((pu * pu) + (pv * pv)));
            var raw = (a / Angle);

            if ((r < 1e-3f) || (MathF.Abs(x: (raw - MathF.Floor(x: raw))) < 2e-3f) || (MathF.Abs(x: (raw - MathF.Ceiling(x: raw))) < 2e-3f)) { return float.NaN; }

            var sector = MathF.Floor(x: raw);

            a = ((a - (Angle * sector)) - (0.5f * Angle));
            if (mirror) { a = MathF.Abs(x: a); }

            var (sine, cosine) = MathF.SinCos(x: a);
            var folded = axis switch {
                SdfAxis.X => new Vector3(x: point.X, y: (cosine * r), z: (sine * r)),
                SdfAxis.Z => new Vector3(x: (cosine * r), y: (sine * r), z: point.Z),
                _ => new Vector3(x: (cosine * r), y: point.Y, z: (sine * r)),
            };

            return Box(p: (folded - new Vector3(x: 0.15f, y: 0.05f, z: 0.1f)));
        });
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [Fact]
    public void RepeatPolarStridesTheMaterialBySector() {
        const int Count = 4;
        var builder = new SdfProgramBuilder();
        var materials = Enumerable.Range(start: 0, count: 8).Select(selector: index => builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: (index / 8f))))).ToArray();
        var program = builder.ResetPoint().RepeatPolar(axis: SdfAxis.Y, count: Count, materialStride: 1).Translate(offset: new Vector3(x: 0.3f, y: 0f, z: 0f)).Sphere(material: materials[0], radius: 0.1f).Build();
        var field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);

        for (var sector = 0; (sector < Count); sector++) {
            var angle = (sector * (MathF.Tau / Count));
            var point = new Vector3(x: (0.3f * MathF.Cos(x: angle)), y: 0f, z: (0.3f * MathF.Sin(x: angle)));

            Assert.True(condition: field.TryDistance(distance: out var distance, material: out var material, position: Fixed(point: point)));
            Assert.Equal(expected: -0.1f, actual: (float)(double)distance, tolerance: 1e-3f);
            Assert.Equal(expected: sector, actual: material);
        }
    }
    [InlineData(SdfWallpaperGroup.Pmm)]
    [InlineData(SdfWallpaperGroup.P4M)]
    [InlineData(SdfWallpaperGroup.P3M1)]
    [InlineData(SdfWallpaperGroup.P6M)]
    [Theory]
    public void WallpaperFoldFoldsThePlaneAsTheFloatStatementDoes(SdfWallpaperGroup group) {
        var cell = new Vector2(x: 0.3f, y: 0.3f);
        var limit = ((group >= SdfWallpaperGroup.P3) ? new Vector2(value: SdfWallpaperFold.UnboundedLimit) : new Vector2(x: 1f, y: 1f));
        var offset = new Vector3(x: 0.06f, y: 0f, z: 0.04f);
        var program = Program(chain: builder => builder.WallpaperFold(cell: cell, group: group, limit: limit, plane: SdfPlane.XZ).Translate(offset: offset));

        AssertAnswers(program: program, reference: point => {
            var folded = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: group, limit: limit, point: new Vector2(x: point.X, y: point.Z));
            // A point within rounding of a cell wall or an in-cell mirror folds onto either side on each interpreter.
            var inverse = SdfWallpaperFold.InverseCell(cell: cell, group: group);
            var axialB = (point.Z * inverse.Y);
            var axialA = ((point.X * inverse.X) - (0.5f * axialB));
            var nearWall = ((group >= SdfWallpaperGroup.P3)
                ? (NearHalf(value: axialA) || NearHalf(value: axialB) || NearHalf(value: -(axialA + axialB)))
                : (NearHalf(value: (point.X * inverse.X)) || NearHalf(value: (point.Z * inverse.Y))));
            var nearMirror = ((MathF.Abs(x: folded.X) < 2e-3f) || (MathF.Abs(x: folded.Y) < 2e-3f) || (MathF.Abs(x: (folded.X - folded.Y)) < 2e-3f) || (MathF.Abs(x: ((0.5f * folded.X) - (0.8660254f * folded.Y))) < 2e-3f) || (MathF.Abs(x: ((0.8660254f * folded.X) - (0.5f * folded.Y))) < 2e-3f));

            return ((nearWall || nearMirror) ? float.NaN : Box(p: (new Vector3(x: folded.X, y: point.Y, z: folded.Y) - offset)));
        });
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [Fact]
    public void LogSphereFoldsTheRadiusToItsShell() {
        const float Ratio = 3f;
        const float Twist = 0.4f;
        var w = MathF.Log(x: Ratio);
        var program = Program(chain: static builder => builder.LogSphere(shellRatio: Ratio, twist: Twist));

        AssertAnswers(program: program, reference: point => {
            var radius = MathF.Max(x: point.Length(), y: 1.0e-4f);
            var raw = (MathF.Log(x: radius) / w);

            if (NearHalf(value: raw, reach: 1e-2f)) { return float.NaN; }

            var shell = MathF.Round(x: raw);
            var scale = MathF.Exp(x: (shell * w));
            var folded = (point / scale);
            var (sine, cosine) = MathF.SinCos(x: (shell * Twist));

            return (Box(p: new Vector3(x: ((cosine * folded.X) - (sine * folded.Y)), y: ((sine * folded.X) + (cosine * folded.Y)), z: folded.Z)) * scale);
        });
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [InlineData(SdfNoiseFlavor.White, 0f)]
    [InlineData(SdfNoiseFlavor.Blue, 0f)]
    [InlineData(SdfNoiseFlavor.Gaussian, 0f)]
    [InlineData(SdfNoiseFlavor.White, 0.6f)]
    [Theory]
    public void CellJitterDisplacesAndTumblesEachCellByItsHash(SdfNoiseFlavor flavor, float tumble) {
        var spacing = new Vector3(x: 1.2f, y: 1.4f, z: 1.1f);
        const float Jitter = 0.08f;
        const uint Seed = 11u;
        var program = Program(chain: builder => builder.CellJitter(flavor: flavor, jitter: Jitter, seed: Seed, spacing: spacing, tumble: tumble));

        AssertAnswers(program: program, reference: point => {
            var scaled = (point / spacing);

            if (NearHalf(value: scaled.X) || NearHalf(value: scaled.Y) || NearHalf(value: scaled.Z)) { return float.NaN; }

            var cell = new Vector3(x: MathF.Round(x: scaled.X), y: MathF.Round(x: scaled.Y), z: MathF.Round(x: scaled.Z));
            var (cx, cy, cz) = (unchecked((uint)(int)cell.X), unchecked((uint)(int)cell.Y), unchecked((uint)(int)cell.Z));
            var (sx, sy, sz) = (Seed, unchecked(Seed * Pcg3dLatticeNoise.HashStreamA), unchecked(Seed * Pcg3dLatticeNoise.HashStreamB));
            var (kx, ky, kz) = ((cx ^ sx), (cy ^ sy), (cz ^ sz));
            var h0 = Pcg3dLatticeNoise.Pcg3d(x: kx, y: ky, z: kz);
            Vector3 r0;

            switch (flavor) {
                case SdfNoiseFlavor.Blue: {
                        var (ux, uy, uz) = (unchecked(cx + sx), unchecked(cy + sy), unchecked(cz + sz));

                        r0 = new Vector3(
                            x: Fraction(hash: unchecked((ux * 3518319155u) + (uy * 2882110345u) + (uz * 2360945575u))),
                            y: Fraction(hash: unchecked((ux * 2882110345u) + (uy * 2360945575u) + (uz * 3518319155u))),
                            z: Fraction(hash: unchecked((ux * 2360945575u) + (uy * 3518319155u) + (uz * 2882110345u)))
                        );
                        break;
                    }
                case SdfNoiseFlavor.Gaussian: {
                        var g1 = Pcg3dLatticeNoise.Pcg3d(x: (kx ^ Pcg3dLatticeNoise.HashStreamA), y: (ky ^ Pcg3dLatticeNoise.HashStreamA), z: (kz ^ Pcg3dLatticeNoise.HashStreamA));
                        var g2 = Pcg3dLatticeNoise.Pcg3d(x: (kx ^ Pcg3dLatticeNoise.HashStreamB), y: (ky ^ Pcg3dLatticeNoise.HashStreamB), z: (kz ^ Pcg3dLatticeNoise.HashStreamB));

                        r0 = ((new Vector3(x: Fraction(hash: h0.X), y: Fraction(hash: h0.Y), z: Fraction(hash: h0.Z)) + new Vector3(x: Fraction(hash: g1.X), y: Fraction(hash: g1.Y), z: Fraction(hash: g1.Z)) + new Vector3(x: Fraction(hash: g2.X), y: Fraction(hash: g2.Y), z: Fraction(hash: g2.Z))) / 3f);
                        break;
                    }
                default: {
                        r0 = new Vector3(x: Fraction(hash: h0.X), y: Fraction(hash: h0.Y), z: Fraction(hash: h0.Z));
                        break;
                    }
            }

            var folded = ((point - (spacing * cell)) - ((r0 - new Vector3(value: 0.5f)) * Jitter));

            if (tumble > 0f) {
                var h1 = Pcg3dLatticeNoise.Pcg3d(x: (kx ^ Pcg3dLatticeNoise.HashTumble), y: (ky ^ Pcg3dLatticeNoise.HashTumble), z: (kz ^ Pcg3dLatticeNoise.HashTumble));
                var r1 = new Vector3(x: Fraction(hash: h1.X), y: Fraction(hash: h1.Y), z: Fraction(hash: h1.Z));
                var zz = ((2f * r1.X) - 1f);
                var rr = MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (zz * zz))));
                var phi = (MathF.Tau * r1.Y);
                var axis = new Vector3(x: (rr * MathF.Cos(x: phi)), y: (rr * MathF.Sin(x: phi)), z: zz);
                var half = (0.5f * ((r1.Z * tumble) * MathF.PI));
                var q = new Quaternion(vectorPart: (axis * MathF.Sin(x: half)), scalarPart: MathF.Cos(x: half));

                folded = Vector3.Transform(value: folded, rotation: Quaternion.Conjugate(value: q));
            }

            return Box(p: folded);
        });
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [Fact]
    public void CellJitterStridesTheMaterialByItsHashedVariant() {
        var spacing = new Vector3(value: 1f);
        const uint Seed = 3u;
        const int Variants = 4;
        var builder = new SdfProgramBuilder();
        var materials = Enumerable.Range(start: 0, count: Variants).Select(selector: index => builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: (index / 4f))))).ToArray();
        var program = builder.ResetPoint().CellJitter(jitter: 0f, materialVariants: Variants, seed: Seed, spacing: spacing).Sphere(material: materials[0], radius: 0.2f).Build();
        var field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);

        for (var cell = -2; (cell <= 2); cell++) {
            var (sx, sy, sz) = (Seed, unchecked(Seed * Pcg3dLatticeNoise.HashStreamA), unchecked(Seed * Pcg3dLatticeNoise.HashStreamB));
            var expected = (int)(Pcg3dLatticeNoise.Pcg3d(x: (unchecked((uint)cell) ^ sx), y: sy, z: sz).Z % Variants);

            Assert.True(condition: field.TryDistance(distance: out _, material: out var material, position: Fixed(point: new Vector3(x: cell, y: 0f, z: 0f))));
            Assert.Equal(expected: expected, actual: material);
        }
    }
    [Fact]
    public void LaneErodeErodesTheNextShapeByItsRaggedShareOfTheReachUnderZeroLanes() {
        const float NoiseScale = 9f;
        const float Reach = 0.3f;
        var program = Program(chain: static builder => builder.LaneErode(from: -1f, lane: 0, noiseScale: NoiseScale, reach: Reach, to: 1f));

        AssertAnswers(program: program, reference: static point => {
            const float T = 0.5f;
            var noise = (0.5f + (0.5f * ValueNoise3(q: (point * NoiseScale), seedX: Pcg3dLatticeNoise.HashStreamA, seedY: Pcg3dLatticeNoise.HashStreamB, seedZ: Pcg3dLatticeNoise.HashTumble)));
            var ragged = Math.Clamp(value: (T + (((noise - 0.5f) * SdfProgramBuilder.LaneErodeRaggedAmount) * (4f * T * (1f - T)))), min: 0f, max: 1f);

            return (Box(p: point) + (ragged * Reach));
        });
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [Fact]
    public void LaneErodeSkipsTheNextShapeOnceTheLaneIsPastItsWindow() {
        var skipped = new SdfFieldEvaluator(program: Program(chain: static builder => builder.LaneErode(from: -2f, lane: 1, noiseScale: 1f, reach: 0.3f, to: -1f)), reading: SdfFieldReading.Render);
        var kept = new SdfFieldEvaluator(program: Program(chain: static builder => builder.LaneErode(from: 1f, lane: 2, noiseScale: 1f, reach: 0.3f, to: 2f)), reading: SdfFieldReading.Render);

        Assert.True(condition: (Read(field: skipped, point: Vector3.Zero) > 1e6f));
        Assert.Equal(expected: Box(p: Vector3.Zero), actual: Read(field: kept, point: Vector3.Zero), tolerance: Tolerance);
    }
    [Fact]
    public void DisplaceAddsTheSineProductRelief() {
        var frequency = new Vector3(x: 11f, y: 7f, z: 13f);
        const float Amplitude = 0.03f;
        var program = FieldProgram(after: builder => builder.Displace(amplitude: Amplitude, frequency: frequency));

        AssertAnswers(program: program, reference: point => (Box(p: point) + (Amplitude * ((MathF.Sin(x: (frequency.X * point.X)) * MathF.Sin(x: (frequency.Y * point.Y))) * MathF.Sin(x: (frequency.Z * point.Z))))));
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [Fact]
    public void NoiseDisplaceAddsTheNormalizedOctaveSum() {
        const float Frequency = 6f;
        const float Amplitude = 0.05f;
        const int Octaves = 3;
        const float Gain = 0.5f;
        const float Lacunarity = 2f;
        const uint Seed = 21u;
        var program = FieldProgram(after: static builder => builder.NoiseDisplace(amplitude: Amplitude, frequency: Frequency, gain: Gain, lacunarity: Lacunarity, octaves: Octaves, seed: Seed));

        AssertAnswers(program: program, reference: static point => {
            var q = (point * Frequency);
            var octaveAmplitude = 1f;
            var sum = 0f;
            var gainSum = 0f;

            for (var octave = 0u; (octave < Octaves); octave++) {
                var octaveSeed = unchecked(Seed + octave);

                sum += (octaveAmplitude * ValueNoise3(q: q, seedX: octaveSeed, seedY: unchecked(octaveSeed * Pcg3dLatticeNoise.HashStreamA), seedZ: unchecked(octaveSeed * Pcg3dLatticeNoise.HashStreamB)));
                gainSum += octaveAmplitude;
                q *= Lacunarity;
                octaveAmplitude *= Gain;
            }

            return (Box(p: point) + ((Amplitude / gainSum) * sum));
        });
        AssertBoundsEnclose(program: program);
        AssertContactRefuses(program: program);
    }
    [Fact]
    public void EveryRenderOpIsAdmittedByTheRenderReadingAlone() {
        foreach (var op in SdfFieldEvaluator.RenderWarpOps) {
            Assert.True(condition: SdfFieldEvaluator.IsSupportedOp(op: op, reading: SdfFieldReading.Render), userMessage: $"{op} is not read for rendering");
            Assert.False(condition: SdfFieldEvaluator.IsSupportedOp(op: op, reading: SdfFieldReading.Contact), userMessage: $"{op} is read for contact");
            Assert.Contains(expected: op, collection: SdfFieldEvaluator.BoundedOps);
        }

        Assert.Equal(expected: [SdfOp.TransformDynamic], actual: Enum.GetValues<SdfOp>().Where(predicate: static op => !SdfFieldEvaluator.IsSupportedOp(op: op, reading: SdfFieldReading.Render)));
    }
}
