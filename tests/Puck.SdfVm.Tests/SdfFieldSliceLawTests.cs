using System.Buffers.Binary;
using System.Numerics;

using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws over <see cref="SdfFieldSlice"/> sampled through <see cref="SdfMeshPathBake.Probe"/>, driven on the CPU: the test
/// answers each lattice as the bake kernel would, from an analytic field at the lattice's points with the kernel's
/// tetrahedral gradient. An exact field reads a gradient of one everywhere and draws white on the gradient panel; a gauge
/// that underestimates reads below one and draws blue; the slice's lattices go before a pending group's, a returned
/// lattice is taken again, and a new program restarts the slice.
/// </summary>
public sealed class SdfFieldSliceLawTests {
    private static readonly Vector3 SphereCentre = new(x: 0.2f, y: 0.5f, z: -0.1f);
    private const float SphereRadius = 0.6f;
    private static readonly Vector3 GaugeRadii = new(x: 0.9f, y: 0.3f, z: 0.5f);

    // An exact sphere.
    private static float Sphere(Vector3 point) => ((point - SphereCentre).Length() - SphereRadius);
    // The exponent-2 superellipsoid gauge, minR·(|p/r| - 1): a bound that underestimates away from the smallest radius.
    private static float Gauge(Vector3 point) => (MathF.Min(x: GaugeRadii.X, y: MathF.Min(x: GaugeRadii.Y, y: GaugeRadii.Z)) * ((point / GaugeRadii).Length() - 1f));
    private static float Far(Vector3 point) => 1.0e9f;

    // The bake kernel's answer to a batch: at each lattice's points, the field the selector names (every instance, or the
    // segments no instance owns), with the tetrahedral gradient a sixty-fourth of the lattice's cell out.
    private static byte[] Answer(SdfMeshBakeBatch batch, Func<Vector3, float> instances, Func<Vector3, float> world) {
        var bytes = new byte[((batch.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint))];
        var sample = 0;
        ReadOnlySpan<Vector3> taps = [new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: -1f, z: 1f), new(x: -1f, y: 1f, z: -1f), new(x: 1f, y: 1f, z: 1f)];

        foreach (var lattice in batch.Lattices) {
            var field = ((lattice.Selector == int.MinValue) ? world : instances);
            var spacing = (0.015625f * MathF.Min(lattice.Cell.X, MathF.Min(lattice.Cell.Y, lattice.Cell.Z)));

            for (var local = 0; (local < lattice.Samples); local++, sample++) {
                var position = (lattice.Points.IsEmpty
                    ? (lattice.Origin + (new Vector3(x: (local % lattice.Dims.X), y: ((local / lattice.Dims.X) % lattice.Dims.Y), z: (local / (lattice.Dims.X * lattice.Dims.Y))) * lattice.Cell))
                    : new Vector3(x: lattice.Points.Span[local].X, y: lattice.Points.Span[local].Y, z: lattice.Points.Span[local].Z));
                var gradient = Vector3.Zero;

                if (lattice.Gradients) {
                    foreach (var tap in taps) {
                        gradient += (tap * field(arg: (position + (tap * spacing))));
                    }
                    gradient *= (0.25f / spacing);
                }

                var record = bytes.AsSpan(start: ((sample * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

                BinaryPrimitives.WriteSingleLittleEndian(destination: record, value: field(arg: position));
                BinaryPrimitives.WriteInt32LittleEndian(destination: record[4..], value: 0);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[8..], value: gradient.X);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[12..], value: gradient.Y);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[16..], value: gradient.Z);
            }
        }

        return bytes;
    }
    // A program of one instance, a box, whose group the bake owes alongside the slice.
    private static SdfFrame Frame(Vector3 half) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: (half.Length() + 0.05f))
            .ResetPoint()
            .Box(halfExtents: half, material: material, round: 0f)
            .EndInstance();
        return new SdfFrame(Program: builder.Build(), ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };
    }
    // Answers the slice's lattices frame by frame until its completion runs, and returns how many frames that took.
    private static int Drive(SdfMeshPathBake bake, Func<Vector3, float> instances, Func<Vector3, float> world, Func<bool> done, SdfFrame? frame = null) {
        var frames = 0;

        while (!done()) {
            Assert.True(condition: (frames < 64), userMessage: "the slice never completed");
            if (frame is not null) { _ = bake.Dress(frame: frame); }
            if (bake.TryTakeForFrame(batch: out var batch, frame: frames)) {
                bake.Record(batch: batch);
                bake.Complete(batch: batch, samples: Answer(batch: batch, instances: instances, world: world));
            }
            frames++;
        }
        return frames;
    }
    private static Vector3 Pixel(byte[] rgba, int width, int column, int row) {
        var at = (((row * width) + column) * 4);

        return new Vector3(x: rgba[at], y: rgba[(at + 1)], z: rgba[(at + 2)]);
    }

    [Fact]
    public void AnExactFieldReadsAGradientOfOneAndDrawsWhite() {
        var bake = new SdfMeshPathBake { Residency = "slice-exact" };
        var completions = 0;
        // The plane passes 0.4 m from the sphere's centre, where the stencil's error is far under a thousandth, cutting a
        // circle of radius √(0.6² - 0.4²).
        var slice = new SdfFieldSlice(axis: SdfAxis.Z, bands: 0.1f, centre: new Vector2(x: SphereCentre.X, y: SphereCentre.Y), completed: _ => completions++, half: 1f, offset: (SphereCentre.Z + 0.4f), resolution: 64);

        Assert.True(condition: bake.Probe(slice: slice));
        Assert.True(condition: bake.Pending);
        Assert.False(condition: bake.Probe(slice: new SdfFieldSlice(axis: SdfAxis.X, bands: 0.1f, centre: Vector2.Zero, half: 1f, offset: 0f, resolution: 16)));

        // No program: every lattice samples the unowned segments alone, here the sphere.
        _ = Drive(bake: bake, done: () => (completions > 0), instances: Far, world: Sphere);

        Assert.Equal(expected: 1, actual: completions);
        Assert.True(condition: slice.Done);
        Assert.False(condition: bake.Pending);

        var described = slice.Describe();

        Assert.StartsWith(expectedStartString: "z=0.3 centre=(0.2,0.5) half=1 64x64 bands=0.1 d=[-0.19", actualString: described, comparisonType: StringComparison.Ordinal);
        // The stencil's float rounding reads a thousandth either side of one.
        Assert.Matches(expectedRegexPattern: @"\|grad d\| min=(0\.999|1) mean=1 max=(1\.001|1) off=0% near-surface min=(0\.999|1) mean=1 max=(1\.001|1) off=0%$", actualString: described);

        var rgba = slice.Render();

        Assert.Equal(expected: ((2 * 64) * 64 * 4), actual: rgba.Length);
        // The centre is inside (cool), a corner outside (warm); the gradient panel is white outside and dimmed inside.
        var inside = Pixel(column: 32, rgba: rgba, row: 32, width: 128);
        var outside = Pixel(column: 0, rgba: rgba, row: 0, width: 128);

        Assert.True(condition: (inside.Z > inside.X), userMessage: $"inside {inside}");
        Assert.True(condition: (outside.X > outside.Z), userMessage: $"outside {outside}");
        Assert.Equal(expected: new Vector3(value: 255f), actual: Pixel(column: (64 + 0), rgba: rgba, row: 0, width: 128));
        Assert.Equal(expected: new Vector3(value: 204f), actual: Pixel(column: (64 + 32), rgba: rgba, row: 32, width: 128));
        // The surface crosses the middle row the circle's radius right of the centre: black on the gradient panel, white on
        // the left.
        var surface = (32 + (int)MathF.Round(x: ((MathF.Sqrt(x: ((SphereRadius * SphereRadius) - 0.16f)) / (2f / 64f)) - 0.5f)));

        Assert.Equal(expected: Vector3.Zero, actual: Pixel(column: (64 + surface), rgba: rgba, row: 32, width: 128));
        Assert.Equal(expected: new Vector3(value: 255f), actual: Pixel(column: surface, rgba: rgba, row: 32, width: 128));
    }
    [Fact]
    public void AGaugeReadsBelowOneAndDrawsBlueWhileTheSliceGoesBeforeAPendingGroup() {
        var bake = new SdfMeshPathBake { Residency = "slice-gauge" };
        var frame = Frame(half: new Vector3(value: 0.25f));
        var completions = 0;
        // Past one reference frame's share of evaluations: the slice samples over several frames.
        var slice = new SdfFieldSlice(axis: SdfAxis.Y, bands: 0.05f, centre: Vector2.Zero, completed: _ => completions++, half: 1.2f, offset: 0.1f, resolution: 256);

        _ = bake.Dress(frame: frame);
        Assert.True(condition: bake.Probe(slice: slice));
        Assert.True(condition: bake.TryTakeForFrame(batch: out var first, frame: 0));
        // The slice's lattice first, every instance in its mask; the box's group waits.
        var lattice = first.Lattices[0];

        Assert.Single(collection: first.Lattices);
        Assert.Equal(expected: -1, actual: lattice.Selector);
        Assert.Equal(expected: 1, actual: lattice.Instances);
        Assert.True(condition: lattice.Gradients);
        // An abandoned dispatch hands its lattice back, and the next frame takes it again.
        bake.Abandon(batch: first);
        Assert.True(condition: bake.TryTakeForFrame(batch: out var again, frame: 1));
        Assert.Equal(expected: lattice.Points, actual: again.Lattices[0].Points);

        var frames = Drive(bake: bake, done: () => (completions > 0), frame: frame, instances: Gauge, world: Far);

        Assert.True(condition: (frames >= ((2 * slice.Chunks) - 1)), userMessage: $"{frames} frames for {slice.Chunks} chunks");
        Assert.Equal(expected: 1, actual: completions);
        // The box's group still bakes after the slice.
        Assert.True(condition: bake.Pending);

        var described = slice.Describe();

        Assert.Contains(expectedSubstring: "y=0.1 centre=(0,0) half=1.2 256x256 bands=0.05", actualString: described, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "max=1 off=0%", actualString: described, comparisonType: StringComparison.Ordinal);
        Assert.Matches(expectedRegexPattern: @"\|grad d\| min=0\.[0-5]\d* mean=0\.\d+ max=1(\.0\d*)? off=\d", actualString: described);

        var rgba = slice.Render();
        // Along X at the centre row the gauge's rate is minR/rX: a third, so the gradient panel is blue there.
        var blue = Pixel(column: (256 + 250), rgba: rgba, row: 128, width: 512);

        Assert.True(condition: ((blue.Z > 200f) && (blue.X < 100f)), userMessage: $"gradient panel {blue}");
    }
    [Fact]
    public void ANewProgramRestartsTheSliceAndItsStaleAnswersAreDropped() {
        var bake = new SdfMeshPathBake { Residency = "slice-restart" };
        var completions = 0;
        var slice = new SdfFieldSlice(axis: SdfAxis.X, bands: 0.1f, centre: Vector2.Zero, completed: _ => completions++, half: 1f, offset: 0f, resolution: 32);

        _ = bake.Dress(frame: Frame(half: new Vector3(value: 0.25f)));
        Assert.True(condition: bake.Probe(slice: slice));
        Assert.True(condition: bake.TryTakeForFrame(batch: out var stale, frame: 0));
        bake.Record(batch: stale);
        // The program changes while the lattice is in flight: its answer lands on nothing, and the slice samples anew.
        var next = Frame(half: new Vector3(value: 0.3f));

        _ = bake.Dress(frame: next);
        bake.Complete(batch: stale, samples: Answer(batch: stale, instances: Sphere, world: Far));
        Assert.Equal(expected: 0, actual: completions);
        Assert.False(condition: slice.Done);

        _ = Drive(bake: bake, done: () => (completions > 0), frame: next, instances: Sphere, world: Far);
        Assert.Equal(expected: 1, actual: completions);
        Assert.Contains(expectedSubstring: "off=0%", actualString: slice.Describe(), comparisonType: StringComparison.Ordinal);
    }
    // A field twice the distance overestimates it: its gradient reads two and draws red. A field that holds no surface
    // within a band of the slice has nothing near it to describe.
    [Fact]
    public void AnOverestimateDrawsRedAndAFieldWithNoSurfaceNearHasNoNearSurfaceReading() {
        var completions = 0;
        var doubled = new SdfFieldSlice(axis: SdfAxis.Z, bands: 0.1f, centre: new Vector2(x: SphereCentre.X, y: SphereCentre.Y), completed: _ => completions++, half: 1f, offset: (SphereCentre.Z + 0.4f), resolution: 32);
        var bake = new SdfMeshPathBake { Residency = "slice-doubled" };

        Assert.True(condition: bake.Probe(slice: doubled));
        _ = Drive(bake: bake, done: () => (completions > 0), instances: Far, world: static point => (2f * Sphere(point: point)));
        Assert.Matches(expectedRegexPattern: @"\|grad d\| min=(1\.99\d|2) mean=2 max=(2\.00\d|2) off=100%", actualString: doubled.Describe());

        var red = Pixel(column: (32 + 0), rgba: doubled.Render(), row: 0, width: 64);

        Assert.True(condition: ((red.X > 200f) && (red.Z < 100f)), userMessage: $"gradient panel {red}");

        var distant = new SdfFieldSlice(axis: SdfAxis.Z, bands: 0.1f, centre: Vector2.Zero, completed: _ => completions++, half: 1f, offset: 0f, resolution: 16);

        Assert.True(condition: bake.Probe(slice: distant));
        _ = Drive(bake: bake, done: () => (completions > 1), instances: Far, world: static _ => 5f);
        Assert.EndsWith(expectedEndString: "near-surface none", actualString: distant.Describe(), comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void ASliceNoShapeAnswersSaysSoAndDrawsGrey() {
        var bake = new SdfMeshPathBake { Residency = "slice-empty" };
        var completions = 0;
        var slice = new SdfFieldSlice(axis: SdfAxis.Z, bands: 0.1f, centre: Vector2.Zero, completed: _ => completions++, half: 1f, offset: 0f, resolution: 16);

        Assert.True(condition: bake.Probe(slice: slice));
        _ = Drive(bake: bake, done: () => (completions > 0), instances: Far, world: Far);

        Assert.EndsWith(expectedEndString: "16x16 bands=0.1 no shape answered", actualString: slice.Describe(), comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: new Vector3(value: 128f), actual: Pixel(column: 3, rgba: slice.Render(), row: 5, width: 32));
        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 0f), actual: slice.PlanePoint(u: 0f, v: 0f));
    }
    [Theory]
    [InlineData(3, 0f, 1f, 64, 0.1f, "axis")]
    [InlineData(2, float.NaN, 1f, 64, 0.1f, "offset")]
    [InlineData(2, 0f, 0f, 64, 0.1f, "half")]
    [InlineData(2, 0f, 1f, 64, 0f, "bands")]
    [InlineData(2, 0f, 1f, 8, 0.1f, "resolution")]
    [InlineData(2, 0f, 1f, 1024, 0.1f, "resolution")]
    public void ASliceOutsideItsRangesIsRefused(uint axis, float offset, float half, int resolution, float bands, string parameter) {
        var refusal = Assert.ThrowsAny<ArgumentOutOfRangeException>(testCode: () => new SdfFieldSlice(axis: (SdfAxis)axis, bands: bands, centre: Vector2.Zero, half: half, offset: offset, resolution: resolution));

        Assert.Equal(expected: parameter, actual: refusal.ParamName);
    }
    [Fact]
    public void ACentreThatIsNotFiniteIsRefused() =>
        Assert.Equal(expected: "centre", actual: Assert.ThrowsAny<ArgumentOutOfRangeException>(testCode: () => new SdfFieldSlice(axis: SdfAxis.Y, bands: 0.1f, centre: new Vector2(x: float.NaN, y: 0f), half: 1f, offset: 0f, resolution: 16)).ParamName);
}
