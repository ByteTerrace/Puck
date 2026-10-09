using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>A point query of the instance grid walks only its block of cells; every binned instance it skips must keep at
/// least the block's bound from the point, or the complete field it answers would be too large.</summary>
public sealed class SdfInstanceGridPointQueryLawTests {
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [Theory]
    public void EveryBinnedInstanceOutsideThePointBlockKeepsTheBlocksBoundFromThePoint(int seed) {
        var random = new Random(Seed: seed);
        var inputs = new SdfInstanceGridInput[512];

        for (var index = 0; (index < inputs.Length); index++) {
            // Mostly small bounds with a few large ones, so the largest binned radius pads every face.
            var radius = ((index % 37) == 0) ? Next(random, 0.5f, 2.5f) : Next(random, 0.02f, 0.4f);

            inputs[index] = new SdfInstanceGridInput(Binnable: true, Center: new Vector3(Next(random, -20f, 20f), Next(random, -4f, 4f), Next(random, -20f, 20f)),
                FrameBinnable: true, Radius: radius);
        }
        var workspace = new SdfInstanceGrid.Workspace(maxInstances: inputs.Length);
        var block = workspace.Build(instances: inputs, enabled: true).ToArray();

        Assert.NotEqual(0u, block[0]);
        var dimensions = (X: ((int)block[1]), Y: ((int)block[2]), Z: ((int)block[3]));
        var home = HomeCells(block: block, dimensions: dimensions);
        var answered = 0;
        var skipped = 0;

        for (var sample = 0; (sample < 4096); sample++) {
            var point = new Vector3(Next(random, -22f, 22f), Next(random, -6f, 6f), Next(random, -22f, 22f));

            if (!SdfInstanceGridPointQuery.TryBlock(block: block, point: point, result: out var query)) { continue; }
            answered++;
            foreach (var (instance, cell) in home) {
                if ((cell.X >= query.Min.X) && (cell.X <= query.Max.X) && (cell.Y >= query.Min.Y) && (cell.Y <= query.Max.Y) &&
                    (cell.Z >= query.Min.Z) && (cell.Z <= query.Max.Z)) { continue; }
                skipped++;
                var input = inputs[instance];
                var clearance = (Vector3.Distance(value1: point, value2: input.Center) - input.Radius);

                Assert.True(condition: (clearance >= query.Bound),
                    userMessage: $"Instance {instance} outside the block of {point} keeps {clearance}, below the block's bound {query.Bound}.");
            }
        }
        Assert.True(condition: (answered > 1000), userMessage: $"Only {answered} points fell inside the grid.");
        Assert.True(condition: (skipped > 100_000), userMessage: "The blocks must actually skip instances.");
    }

    // Each binned instance with its home cell, read back from the packed CSR directory.
    private static List<(int Instance, (int X, int Y, int Z) Cell)> HomeCells(uint[] block, (int X, int Y, int Z) dimensions) {
        var cellStart = ((int)block[10]);
        var entries = ((int)block[11]);
        var cells = ((int)block[14]);
        var result = new List<(int, (int, int, int))>();

        for (var cell = 0; (cell < cells); cell++) {
            for (var entry = ((int)block[(cellStart + cell)]); (entry < ((int)block[((cellStart + cell) + 1)])); entry++) {
                result.Add(((int)block[(entries + entry)], ((cell % dimensions.X), ((cell / dimensions.X) % dimensions.Y), (cell / (dimensions.X * dimensions.Y)))));
            }
        }
        return result;
    }
    private static float Next(Random random, float low, float high) => (low + ((high - low) * random.NextSingle()));
}
