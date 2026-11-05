using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [Fact]
    public void BakePointStorageStartsAtOneRowAndGrowsToActualInputs() {
        using var rig = new Rig(slots: 1);
        Assert.Equal(expected: new byte[16], actual: rig.Gpu.DeviceLocal(part: "mesh-bake-points", sizeBytes: 16));
        rig.Warm();
        rig.Engine.WriteMeshBakePoints(input: default);
        rig.Render(time: 0f);
        Assert.Equal(expected: new byte[16], actual: rig.Gpu.DeviceLocal(part: "mesh-bake-points", sizeBytes: 16));
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());

        // An all-zero demand still grows the buffer. The ordinary carry clears its new tail even when no zero differs.
        rig.Engine.WriteMeshBakePoints(input: Whole(rows: new Vector4[3]));
        rig.Render(time: 0f);
        Assert.Equal(expected: new byte[48], actual: rig.Gpu.DeviceLocal(part: "mesh-bake-points", sizeBytes: 48));
        Assert.Equal(expected: 16UL, actual: rig.Gpu.BufferCopyBytes);

        Vector4[] points = [
            new(x: 1f, y: 2f, z: 3f, w: 4f),
            Vector4.Zero,
            new(x: -1f, y: -2f, z: -3f, w: -4f),
            new(value: 17f),
            new(x: BitConverter.Int32BitsToSingle(value: unchecked((int)0x80000000)), y: 5f, z: 6f, w: 7f),
        ];
        rig.Engine.WriteMeshBakePoints(input: Whole(rows: points));
        rig.Render(time: 0f);
        var device = rig.Gpu.DeviceLocal(part: "mesh-bake-points", sizeBytes: 96).ToArray();
        Assert.Equal(expected: MemoryMarshal.AsBytes(span: points.AsSpan()).ToArray(), actual: device[..80]);
        Assert.Equal(expected: new byte[16], actual: device[80..]);
        Assert.Equal(expected: 48UL, actual: rig.Gpu.BufferCopyBytes);

        rig.Engine.WriteMeshBakePoints(input: default);
        rig.Render(time: 0f);
        Assert.Equal(expected: device, actual: rig.Gpu.DeviceLocal(part: "mesh-bake-points", sizeBytes: 96));
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());
        Assert.Equal(expected: 0UL, actual: rig.Gpu.BufferCopyBytes);
        Assert.Empty(collection: rig.Gpu.StateConflicts);
    }
    // A table whose every row the batch wrote: one run over all of its words.
    private static SdfMeshBakeInput Whole(Vector4[] rows) {
        var runs = new GpuUploadRuns(capacity: 2);

        runs.Add(length: (4 * rows.Length), start: 0);
        return new SdfMeshBakeInput(Rows: rows, Runs: runs);
    }
}
