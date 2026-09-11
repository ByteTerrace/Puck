using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class InstanceGridScaleLawTests {
    [Theory]
    [InlineData(0.0f)]
    [InlineData(1000.0f)]
    public void LargeBoundsRemainQueryableWithoutInflatingTheFineGrid(float largeCenter) {
        var inputs = SmallBounds().Append(new SdfInstanceGridInput(new Vector3(largeCenter), 114.0f, true, true)).ToArray();
        var workspace = new SdfInstanceGrid.Workspace(inputs.Length);
        var words = workspace.Build(inputs, enabled: true).ToArray();

        Assert.Equal(1u, words[0]);
        Assert.InRange(Float(words[9]), 0.1f, 0.11f);
        var always = Always(words);
        Assert.Equal([(uint)(inputs.Length - 1)], always);
        AssertCompletePartition(words, inputs);

        // Independent geometric oracle: every sphere intersecting a probe sphere must be present in the grid's
        // padded box query or always-list. This exercises interior, boundary and off-grid probes at several scales.
        foreach (var reach in new[] { 0.0f, 0.1f, 0.9f, 12.0f }) {
            for (var x = -8; x <= 8; x++) {
                for (var z = -8; z <= 8; z++) {
                    var point = new Vector3(x * 0.5f, 0.0f, z * 0.5f);
                    var candidates = Query(words, point, reach);

                    for (var index = 0; index < inputs.Length; index++) {
                        var bound = inputs[index];
                        if (Vector3.Distance(point, bound.Center) <= (reach + bound.Radius)) {
                            Assert.Contains((uint)index, candidates);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void RebuildRetainsEveryLiveBoundAndClearsPreviousAlwaysEntries() {
        var inputs = SmallBounds().Concat([
            new SdfInstanceGridInput(Vector3.Zero, 114.0f, true, true),
            new SdfInstanceGridInput(Vector3.Zero, 0.2f, false, true),
            new SdfInstanceGridInput(Vector3.Zero, -1.0f, false, false),
        ]).ToArray();
        var workspace = new SdfInstanceGrid.Workspace(inputs.Length);
        var first = workspace.Build(inputs, enabled: true).ToArray();
        Assert.Equal(2u, first[13]);
        AssertCompletePartition(first, inputs);

        inputs[^3] = new SdfInstanceGridInput(new Vector3(0.5f), 0.1f, true, true);
        inputs[^2] = new SdfInstanceGridInput(new Vector3(-0.5f), 0.2f, true, true);
        var second = workspace.Build(inputs, enabled: true).ToArray();
        Assert.Empty(Always(second));
        AssertCompletePartition(second, inputs);
    }

    [Fact]
    public void AllocatingAndPooledProgramBuildsPackIdenticalBytes() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        foreach (var bound in SmallBounds().Append(new SdfInstanceGridInput(Vector3.Zero, 114.0f, true, true))) {
            builder.BeginInstance(bound.Center, bound.Radius);
            builder.ResetPoint().Translate(bound.Center).Sphere(bound.Radius, material);
            builder.EndInstance();
        }

        var allocated = builder.Build();
        var pooled = builder.Build(gridWorkspace: new SdfInstanceGrid.Workspace(SdfProgramBuilder.MaxInstances));
        Assert.Equal(allocated.Words.ToArray(), pooled.Words.ToArray());
    }

    [Fact]
    public void ZeroRadiusPopulationAndDisabledRebuildStayTotal() {
        var inputs = SmallBounds().Select(bound => bound with { Radius = 0.0f }).Append(
            new SdfInstanceGridInput(Vector3.Zero, 100.0f, true, true)).ToArray();
        var workspace = new SdfInstanceGrid.Workspace(inputs.Length);
        var words = workspace.Build(inputs, enabled: true).ToArray();
        Assert.Equal(1u, words[0]);
        AssertCompletePartition(words, inputs);
        Assert.All(workspace.Build(inputs, enabled: false).ToArray(), word => Assert.Equal(0u, word));
        Assert.All(workspace.Build([], enabled: true).ToArray(), word => Assert.Equal(0u, word));
    }

    private static SdfInstanceGridInput[] SmallBounds() {
        var result = new SdfInstanceGridInput[64];
        for (var index = 0; index < result.Length; index++) {
            var center = new Vector3((index % 4) - 1.5f, ((index / 4) % 4) - 1.5f, (index / 16) - 1.5f);
            result[index] = new SdfInstanceGridInput(center, 0.1f, true, true);
        }
        return result;
    }

    private static float Float(uint word) => BitConverter.UInt32BitsToSingle(word);

    private static uint[] Always(uint[] words) => words.AsSpan((int)words[12], (int)words[13]).ToArray();

    private static void AssertCompletePartition(uint[] words, SdfInstanceGridInput[] inputs) {
        var entryCount = (int)words[(int)words[10] + (int)words[14]];
        var entries = words.AsSpan((int)words[11], entryCount).ToArray();
        var all = entries.Concat(Always(words)).Order().ToArray();
        var expected = Enumerable.Range(0, inputs.Length).Where(index => inputs[index].Radius >= 0.0f).Select(index => (uint)index).ToArray();
        Assert.Equal(expected, all);
    }

    private static HashSet<uint> Query(uint[] words, Vector3 center, float reach) {
        var candidates = Always(words).ToHashSet();
        var pad = new Vector3(reach + Float(words[9]));
        var origin = new Vector3(Float(words[4]), Float(words[5]), Float(words[6]));
        var inverseCellSize = Float(words[7]);
        var low = (center - pad - origin) * inverseCellSize;
        var high = (center + pad - origin) * inverseCellSize;
        var dx = (int)words[1];
        var dy = (int)words[2];
        var dz = (int)words[3];
        for (var z = Math.Max(0, (int)MathF.Floor(low.Z)); z <= Math.Min(dz - 1, (int)MathF.Floor(high.Z)); z++) {
            for (var y = Math.Max(0, (int)MathF.Floor(low.Y)); y <= Math.Min(dy - 1, (int)MathF.Floor(high.Y)); y++) {
                for (var x = Math.Max(0, (int)MathF.Floor(low.X)); x <= Math.Min(dx - 1, (int)MathF.Floor(high.X)); x++) {
                    var cell = ((z * dy + y) * dx) + x;
                    var start = words[(int)words[10] + cell];
                    var end = words[(int)words[10] + cell + 1];
                    for (var entry = start; entry < end; entry++) {
                        candidates.Add(words[(int)(words[11] + entry)]);
                    }
                }
            }
        }
        return candidates;
    }
}
