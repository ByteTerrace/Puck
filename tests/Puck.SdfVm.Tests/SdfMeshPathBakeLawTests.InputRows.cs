using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshPathBakeLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BakeInputRowsKeepSampleOffsetsAndSavedPosesWithoutTrailingGridStorage(bool withPoses) {
        Vector4[] points = [new(x: 11f, y: 12f, z: 13f, w: 14f), new(x: 21f, y: 22f, z: 23f, w: 24f)];
        Vector4[] poses = [new(x: 0f, y: 0f, z: 0f, w: 1f), new(x: 31f, y: 32f, z: 33f, w: 0f), new(x: 0f, y: 0f, z: 0f, w: 1f), Vector4.Zero];
        SdfMeshBakeRequest[] lattices = [
            new(Origin: Vector3.Zero, Cell: Vector3.One, Dims: (3, 1, 1), Selector: -1, Gradients: false),
            new(Origin: Vector3.Zero, Cell: Vector3.One, Dims: (2, 1, 1), Selector: -2, Gradients: false) { Points = points },
            new(Origin: Vector3.Zero, Cell: Vector3.One, Dims: (7, 1, 1), Selector: -3, Gradients: false),
        ];
        var batch = new SdfMeshBakeBatch(Lattices: lattices, Tickets: ["grid", "points", "trailing-grid"]) {
            Poses = (withPoses ? [(0, ReadOnlyMemory<Vector4>.Empty), (2, poses), (0, ReadOnlyMemory<Vector4>.Empty)] : []),
        };
        var untouched = new Vector4(value: -123f);
        var input = Enumerable.Repeat(element: untouched, count: batch.InputRows).ToArray();
        // The former full samples-plus-poses representation remains an independent layout witness.
        var full = Enumerable.Repeat(element: untouched, count: (12 + (withPoses ? 4 : 0))).ToArray();

        Assert.Equal(expected: 12, actual: batch.Samples);
        Assert.Equal(expected: (withPoses ? 16 : 5), actual: batch.InputRows);
        Assert.True(condition: batch.WritePoints(table: full));
        Assert.True(condition: batch.WritePoints(table: input));
        Assert.Equal(expected: full[..input.Length], actual: input);
        Assert.Equal(expected: points, actual: input[3..5]);
        Assert.All(collection: input[..3], action: row => Assert.Equal(expected: untouched, actual: row));
        Assert.Equal(expected: new Vector3(x: 11f, y: 12f, z: 13f), actual: lattices[1].SampleAt(local: 0));
        Assert.Equal(expected: new Vector3(x: 21f, y: 22f, z: 23f), actual: lattices[1].SampleAt(local: 1));
        if (withPoses) {
            Assert.Equal(expected: 12, actual: batch.PoseBase(lattice: 1));
            Assert.Equal(expected: 16, actual: batch.PoseBase(lattice: 2));
            Assert.All(collection: input[5..12], action: row => Assert.Equal(expected: untouched, actual: row));
            Assert.Equal(expected: poses, actual: input[12..]);
        } else {
            Assert.All(collection: full[5..], action: row => Assert.Equal(expected: untouched, actual: row));
        }

        var grid = new SdfMeshBakeBatch(Lattices: lattices.Select(selector: static lattice => lattice with { Points = ReadOnlyMemory<Vector4>.Empty }).ToArray(), Tickets: []);
        Assert.Equal(expected: 12, actual: grid.Samples);
        Assert.Equal(expected: 0, actual: grid.InputRows);
        Assert.False(condition: grid.WritePoints(table: Span<Vector4>.Empty));
    }
}
