using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class InstanceGridScaleLawTests {
    private static uint[] Always(uint[] words) => words.AsSpan(
        ((int)words[12]),
        ((int)words[13])
    ).ToArray();
    private static void AssertCompletePartition(uint[] words, SdfInstanceGridInput[] inputs) {
        var entryCount = ((int)words[(((int)words[10]) + ((int)words[14]))]);
        var entries = words.AsSpan(
            ((int)words[11]),
            entryCount
        ).ToArray();
        var all = entries.Concat(second: Always(words: words)).Order().ToArray();
        var expected = Enumerable.Range(
            0,
            inputs.Length
        ).Where(predicate: index => (inputs[index].Radius >= 0.0f)).Select(selector: index => ((uint)index)).ToArray();

        Assert.Equal(
            actual: all,
            expected: expected
        );
    }
    private static float Float(uint word) => BitConverter.UInt32BitsToSingle(value: word);
    private static HashSet<uint> Query(uint[] words, Vector3 center, float reach) {
        var candidates = Always(words: words).ToHashSet();
        var pad = new Vector3(value: (reach + Float(word: words[9])));
        var origin = new Vector3(
            x: Float(word: words[4]),
            y: Float(word: words[5]),
            z: Float(word: words[6])
        );
        var inverseCellSize = Float(word: words[7]);
        var low = (((center - pad) - origin) * inverseCellSize);
        var high = (((center + pad) - origin) * inverseCellSize);
        var dx = ((int)words[1]);
        var dy = ((int)words[2]);
        var dz = ((int)words[3]);

        for (var z = Math.Max(
            val1: 0,
            val2: ((int)MathF.Floor(x: low.Z))
        ); (z <= Math.Min(
            val1: (dz - 1),
            val2: ((int)MathF.Floor(x: high.Z))
        )); z++) {
            for (var y = Math.Max(
                val1: 0,
                val2: ((int)MathF.Floor(x: low.Y))
            ); (y <= Math.Min(
                val1: (dy - 1),
                val2: ((int)MathF.Floor(x: high.Y))
            )); y++) {
                for (var x = Math.Max(
                    val1: 0,
                    val2: ((int)MathF.Floor(x: low.X))
                ); (x <= Math.Min(
                    val1: (dx - 1),
                    val2: ((int)MathF.Floor(x: high.X))
                )); x++) {
                    var cell = ((((z * dy) + y) * dx) + x);
                    var start = words[(((int)words[10]) + cell)];
                    var end = words[((((int)words[10]) + cell) + 1)];

                    for (var entry = start; (entry < end); entry++) {
                        candidates.Add(item: words[((int)(words[11] + entry))]);
                    }
                }
            }
        }
        return candidates;
    }
    private static SdfInstanceGridInput[] SmallBounds() {
        var result = new SdfInstanceGridInput[64];

        for (var index = 0; (index < result.Length); index++) {
            var center = new Vector3(
                x: ((index % 4) - 1.5f),
                y: (((index / 4) % 4) - 1.5f),
                z: ((index / 16) - 1.5f)
            );

            result[index] = new SdfInstanceGridInput(
                Binnable: true,
                Center: center,
                FrameBinnable: true,
                Radius: 0.1f
            );
        }
        return result;
    }

    [Fact]
    public void AllocatingAndPooledProgramBuildsPackIdenticalBytes() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        foreach (var bound in SmallBounds().Append(element: new SdfInstanceGridInput(
            Vector3.Zero,
            114.0f,
            true,
            true
        ))) {
            builder.BeginInstance(
                boundCenter: bound.Center,
                boundRadius: bound.Radius
            );
            builder.ResetPoint().Translate(offset: bound.Center).Sphere(
                bound.Radius,
                material
            );
            builder.EndInstance();
        }

        var allocated = builder.Build();
        var pooled = builder.Build(gridWorkspace: new SdfInstanceGrid.Workspace(maxInstances: SdfProgramBuilder.MaxInstances));

        Assert.Equal(
            allocated.Words.ToArray(),
            pooled.Words.ToArray()
        );
    }
    [InlineData(0.0f)]
    [InlineData(1000.0f)]
    [Theory]
    public void LargeBoundsRemainQueryableWithoutInflatingTheFineGrid(float largeCenter) {
        var inputs = SmallBounds().Append(element: new SdfInstanceGridInput(
            new Vector3(value: largeCenter),
            114.0f,
            true,
            true
        )).ToArray();
        var workspace = new SdfInstanceGrid.Workspace(maxInstances: inputs.Length);
        var words = workspace.Build(
            inputs,
            enabled: true
        ).ToArray();

        Assert.Equal(
            1u,
            words[0]
        );
        Assert.InRange(
            Float(word: words[9]),
            0.1f,
            0.11f
        );
        var always = Always(words: words);

        Assert.Equal(
            [((uint)(inputs.Length - 1))],
            always
        );
        AssertCompletePartition(
            inputs: inputs,
            words: words
        );

        // Independent geometric oracle: every sphere intersecting a probe sphere must be present in the grid's
        // padded box query or always-list. This exercises interior, boundary and off-grid probes at several scales.
        foreach (var reach in new[] { 0.0f, 0.1f, 0.9f, 12.0f }) {
            for (var x = -8; (x <= 8); x++) {
                for (var z = -8; (z <= 8); z++) {
                    var point = new Vector3(
                        x: (x * 0.5f),
                        y: 0.0f,
                        z: (z * 0.5f)
                    );
                    var candidates = Query(
                        center: point,
                        reach: reach,
                        words: words
                    );

                    for (var index = 0; (index < inputs.Length); index++) {
                        var bound = inputs[index];

                        if (Vector3.Distance(
                            value1: point,
                            value2: bound.Center
                        ) <= (reach + bound.Radius)) {
                            Assert.Contains(
                                expected: ((uint)index),
                                set: candidates
                            );
                        }
                    }
                }
            }
        }
    }
    [Fact]
    public void RebuildRetainsEveryLiveBoundAndClearsPreviousAlwaysEntries() {
        var inputs = SmallBounds().Concat(second: [
            new SdfInstanceGridInput(
                Vector3.Zero,
                114.0f,
                true,
                true
            ),
            new SdfInstanceGridInput(
                Vector3.Zero,
                0.2f,
                false,
                true
            ),
            new SdfInstanceGridInput(
                Vector3.Zero,
                -1.0f,
                false,
                false
            ),
        ]).ToArray();
        var workspace = new SdfInstanceGrid.Workspace(maxInstances: inputs.Length);
        var first = workspace.Build(
            inputs,
            enabled: true
        ).ToArray();

        Assert.Equal(
            2u,
            first[13]
        );
        AssertCompletePartition(
            inputs: inputs,
            words: first
        );

        inputs[^3] = new SdfInstanceGridInput(
            new Vector3(value: 0.5f),
            0.1f,
            true,
            true
        );
        inputs[^2] = new SdfInstanceGridInput(
            new Vector3(value: -0.5f),
            0.2f,
            true,
            true
        );
        var second = workspace.Build(
            inputs,
            enabled: true
        ).ToArray();

        Assert.Empty(collection: Always(words: second));
        AssertCompletePartition(
            inputs: inputs,
            words: second
        );
    }
    [Fact]
    public void ZeroRadiusPopulationAndDisabledRebuildStayTotal() {
        var inputs = SmallBounds().Select(selector: bound => bound with { Radius = 0.0f }).Append(element: new SdfInstanceGridInput(
            Vector3.Zero,
            100.0f,
            true,
            true
        )).ToArray();
        var workspace = new SdfInstanceGrid.Workspace(maxInstances: inputs.Length);
        var words = workspace.Build(
            inputs,
            enabled: true
        ).ToArray();

        Assert.Equal(
            1u,
            words[0]
        );
        AssertCompletePartition(
            inputs: inputs,
            words: words
        );
        Assert.All(
            workspace.Build(
                inputs,
                enabled: false
            ).ToArray(),
            word => Assert.Equal(
                actual: word,
                expected: 0u
            )
        );
        Assert.All(
            workspace.Build(
                [],
                enabled: true
            ).ToArray(),
            word => Assert.Equal(
                actual: word,
                expected: 0u
            )
        );
    }
}
