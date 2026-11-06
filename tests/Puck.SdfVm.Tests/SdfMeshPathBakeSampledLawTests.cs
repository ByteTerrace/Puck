using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws over what moving sampled fields and abandoned dispatches cost the mesh bake, driven on the CPU with an analytic
/// answer standing in for the bake kernel: a height field's heights moving every frame restart nothing and let a field
/// slice finish; a reference republished every frame never starves its group, whose newest plan lands once the flips stop;
/// and an answer that stops part way returns exactly the lattices it never answered.
/// </summary>
public sealed class SdfMeshPathBakeSampledLawTests {
    // Columns a quarter metre wide, four by four, standing up to a metre.
    private const float Cell = 0.25f;
    private const int Columns = 4;

    private static SdfHeightField Field(float height) {
        var heights = new float[(Columns * Columns)];

        Array.Fill(array: heights, value: height);
        return new SdfHeightField(cell: Cell, depth: Columns, heights: heights, origin: Vector3.Zero, top: 1f, width: Columns);
    }
    // A program of the height field's instance alone, and the group it plans: instance 0's.
    private static SdfProgram Program(SdfHeightField field) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.HeightField(field: field, material: material);
        return builder.Build();
    }
    private const int HeightSelector = -1;

    private static SdfFrame Frame(SdfProgram program, SdfSampledSet sampled) =>
        new(Program: program, ProgramChanged: false, Time: 0f, Views: []) { FarDistance = 100f, Sampled = sampled };

    // The kernel's stand-in: a box over the columns, its top half a metre up, with the tetrahedral gradient.
    private static float Slab(Vector3 point) {
        var centre = new Vector3(x: (Columns * Cell * 0.5f), y: 0.125f, z: (Columns * Cell * 0.5f));
        var half = new Vector3(x: (Columns * Cell * 0.5f), y: 0.375f, z: (Columns * Cell * 0.5f));
        var q = (Vector3.Abs(value: (point - centre)) - half);

        return (Vector3.Max(value1: q, value2: Vector3.Zero).Length() + MathF.Min(x: MathF.Max(x: q.X, y: MathF.Max(x: q.Y, y: q.Z)), y: 0f));
    }
    private static byte[] Answer(SdfMeshBakeBatch batch, Func<Vector3, float> field) {
        var bytes = new byte[((batch.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint))];
        var sample = 0;
        ReadOnlySpan<Vector3> taps = [new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: -1f, z: 1f), new(x: -1f, y: 1f, z: -1f), new(x: 1f, y: 1f, z: 1f)];

        foreach (var lattice in batch.Lattices) {
            var spacing = (0.015625f * MathF.Min(x: lattice.Cell.X, y: MathF.Min(x: lattice.Cell.Y, y: lattice.Cell.Z)));

            for (var local = 0; (local < lattice.Samples); local++, sample++) {
                var position = lattice.SampleAt(local: local);
                var gradient = Vector3.Zero;

                if (lattice.Gradients) {
                    foreach (var tap in taps) { gradient += (tap * field(arg: (position + (tap * spacing)))); }
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
    // One count of the residency line (SdfMeshPathBake.DescribeResidency).
    private static long Count(SdfMeshPathBake bake, string name) {
        var match = Regex.Match(input: bake.DescribeResidency(radius: 1f), pattern: $@"\b{name}=(\d+)");

        Assert.True(condition: match.Success, userMessage: $"no {name} in the residency line");
        return long.Parse(s: match.Groups[1].Value, provider: CultureInfo.InvariantCulture);
    }

    [Fact]
    public void HeightsMovingEveryFrameRestartNothingAndAFieldSliceStillFinishes() {
        using var run = new MeshBakeTestRun(new SdfMeshPathBake { Residency = "heights" });
        var bake = run.Bake;
        var field = Field(height: 0.5f);
        var program = Program(field: field);
        var sampled = program.Sampled;
        var completions = 0;
        var slice = new SdfFieldSlice(axis: SdfAxis.Y, bands: 0.1f, centre: new Vector2(value: 0.5f), completed: _ => completions++, half: 1f, offset: 0.25f, resolution: 64);

        _ = run.Dresser.Dress(frame: Frame(program: program, sampled: sampled));
        Assert.True(condition: bake.Probe(slice: slice));

        var frames = 0;
        var baked = sampled.Baked;

        // Every frame raises every column a little, so the heights snapshot moves while no column crosses zero.
        while (completions == 0) {
            Assert.True(condition: (frames < 64), userMessage: "the slice never completed while the heights moved");
            Assert.False(condition: field.Publish(heights: Enumerable.Repeat(count: (Columns * Columns), element: (0.5f + (0.001f * (frames + 1)))).ToArray()));
            sampled = SdfSampledSet.Capture(program: program, previous: sampled);
            Assert.Same(expected: baked, actual: sampled.Baked);
            _ = run.Dresser.Dress(frame: Frame(program: program, sampled: sampled));
            if (bake.TryTake(batch: out var batch, field: bake.Program)) { bake.Complete(batch: batch, samples: Answer(batch: batch, field: Slab)); }
            frames++;
        }

        Assert.Equal(expected: 1L, actual: Count(bake: bake, name: "restarts"));
        Assert.Equal(expected: 0L, actual: Count(bake: bake, name: "resamples"));
        Assert.True(condition: (Count(bake: bake, name: "frames") > frames), userMessage: bake.DescribeResidency(radius: 1f));
    }

    [Fact]
    public void AReferenceRepublishedEveryFrameNeverStarvesItsGroupAndItsNewestPlanLands() {
        using var run = new MeshBakeTestRun(new SdfMeshPathBake { Residency = "flips" });
        var bake = run.Bake;
        var field = Field(height: 0.5f);
        var program = Program(field: field);
        var sampled = program.Sampled;
        var heights = Enumerable.Repeat(count: (Columns * Columns), element: 0.5f).ToArray();
        var frames = 0;
        var landed = false;

        _ = run.Dresser.Dress(frame: Frame(program: program, sampled: sampled));
        // Column 0 is lowered to nothing and raised again every frame, which republishes the reference the bake keys, a
        // wetting front faster than any one bake of the group.
        TestLiveness.Until(reason: () => $"no mesh landed in {frames} frames of flips: {bake.Describe()} {bake.DescribeResidency(radius: 1f)}", step: () => {
            Assert.True(condition: (frames < 4000), userMessage: $"no mesh landed in {frames} frames of flips: {bake.DescribeResidency(radius: 1f)}");
            heights[0] = (((frames % 2) == 0) ? 0f : 0.5f);
            Assert.True(condition: field.Publish(heights: heights));
            sampled = SdfSampledSet.Capture(program: program, previous: sampled);
            _ = run.Dresser.Dress(frame: Frame(program: program, sampled: sampled));
            if (bake.TryTake(batch: out var batch, field: bake.Program)) { bake.Complete(batch: batch, samples: Answer(batch: batch, field: Slab)); }
            frames++;
            landed |= bake.TryLandedMesh(mesh: out _, selector: HeightSelector);
            return (landed && (frames >= 8));
        });
        Assert.True(condition: (Count(bake: bake, name: "coalesced") > 0), userMessage: bake.DescribeResidency(radius: 1f));
        Assert.Equal(expected: 1L, actual: Count(bake: bake, name: "restarts"));

        // The flips stop on a reference no bake has keyed yet: the bake settles on it.
        heights[5] = 0f;
        Assert.True(condition: field.Publish(heights: heights));
        sampled = SdfSampledSet.Capture(program: program, previous: sampled);

        var final = Frame(program: program, sampled: sampled);

        TestLiveness.Until(reason: () => bake.DescribeResidency(radius: 1f), step: () => {
            _ = run.Dresser.Dress(frame: final);
            if (bake.TryTake(batch: out var batch, field: bake.Program)) { bake.Complete(batch: batch, samples: Answer(batch: batch, field: Slab)); }
            return !bake.Pending;
        });

        var newest = SdfMeshBakeGroup.Plan(create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector), farDistance: 100f, program: program, sampled: sampled.Baked, transforms: [])
            .Single(predicate: static group => (group.Selector == HeightSelector));

        Assert.Same(expected: field.Reference.Current, actual: newest.SampledSnapshots.Single().Snapshot);
        Assert.True(condition: bake.Library.TryTake(geometry: newest.Sealed, identity: bake.Identity.Value, materials: newest.MaterialOrder, mesh: out var held), userMessage: "the newest reference never baked");
        held.Release();
        Assert.Equal(expected: 0L, actual: Count(bake: bake, name: "successors"));
    }

    [Fact]
    public void AnAnswerThatStopsPartWayReturnsExactlyTheLatticesItNeverAnswered() {
        using var run = new MeshBakeTestRun(new SdfMeshPathBake { Residency = "partial" });
        var bake = run.Bake;
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        foreach (var x in (ReadOnlySpan<float>)[0f, 4f]) {
            _ = builder.BeginInstance(boundCenter: new Vector3(x: x, y: 0f, z: 0f), boundRadius: 0.5f)
                .ResetPoint()
                .Translate(offset: new Vector3(x: x, y: 0f, z: 0f))
                .Box(halfExtents: new Vector3(value: 0.25f), material: material, round: 0f)
                .EndInstance();
        }

        var program = builder.Build();
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        _ = run.Dresser.Dress(frame: frame);
        Assert.True(condition: bake.TryTake(batch: out var batch, field: bake.Program));
        Assert.Equal(expected: 2, actual: batch.Lattices.Length);

        var second = batch.Lattices[1].Selector;
        var whole = Answer(batch: batch, field: static point => (point.Length() - 0.25f));
        var first = ((batch.Lattices[0].Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint));

        // The readback holds the first lattice's samples alone, so answering the second throws, as a decode that runs out of
        // memory does; the service then abandons the batch.
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => bake.Complete(batch: batch, samples: whole.AsSpan(start: 0, length: first)));
        bake.Abandon(batch: batch);

        Assert.Equal(expected: SdfMeshBakeTicket.Answered, actual: batch.StateOf(lattice: 0));
        Assert.Equal(expected: SdfMeshBakeTicket.Returned, actual: batch.StateOf(lattice: 1));
        // The returned lattice is taken again; the answered one is not abandoned by a second call either.
        bake.Abandon(batch: batch);
        Assert.Equal(expected: SdfMeshBakeTicket.Answered, actual: batch.StateOf(lattice: 0));
        Assert.True(condition: bake.TryTake(batch: out var again, field: bake.Program));
        Assert.Contains(collection: again.Lattices, filter: lattice => (lattice.Selector == second));
        bake.Abandon(batch: again);
    }
}
