using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [Fact]
    public void PreviousTransformsFollowConsumedFramesAndSettleWithoutAnotherHostUpload() {
        using var rig = new Rig(slots: 4);
        rig.Warm();
        byte[] Current() => rig.Gpu.DeviceLocal(sizeBytes: (4 * DynamicTransformBytes), part: "dynamic-transforms").ToArray();
        byte[] Previous() => rig.Gpu.DeviceLocal(sizeBytes: (4 * DynamicTransformBytes), part: "previous-dynamic-transforms").ToArray();
        var first = Current();
        Assert.Equal(expected: first, actual: Previous());
        rig.Move(slot: 1);
        rig.Render(time: 0f);
        Assert.Equal(expected: first, actual: Previous());
        Assert.Equal(expected: 0UL, actual: rig.Gpu.BufferCopyBytes);
        var second = Current();
        rig.Move(slot: 2);
        rig.Render(time: 0f);
        Assert.Equal(expected: second, actual: Previous());
        Assert.Equal(expected: ((ulong)DynamicTransformBytes), actual: rig.Gpu.BufferCopyBytes);
        var third = Current();
        // The first still frame settles the last moved row; it sends no host bytes, and touches no other history row.
        rig.Render(time: 0f);
        Assert.Equal(expected: third, actual: Previous());
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());
        Assert.Equal(expected: ((ulong)DynamicTransformBytes), actual: rig.Gpu.BufferCopyBytes);
        rig.Render(time: 0f);
        Assert.Equal(expected: third, actual: Previous());
        Assert.Equal(expected: 0UL, actual: rig.Gpu.BufferCopyBytes);
        rig.Move(slot: 3);
        rig.Rebuild();
        rig.Render(time: 0f);
        Assert.Equal(expected: Current(), actual: Previous());
        Assert.Empty(collection: rig.Gpu.StateConflicts);
    }
    [Fact]
    public void PreviousMeshMatricesKeepTheLastConsumedDrawAndLeaveUnmovedDrawsUntouched() {
        using var rig = new Rig(slots: 1);
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            indices: new uint[] { 0, 1, 2 });
        var first = Matrix4x4.CreateTranslation(xPosition: 1f, yPosition: 2f, zPosition: 3f);
        var second = Matrix4x4.CreateScale(xScale: 2f, yScale: 3f, zScale: 4f);
        SdfMeshDraw[] Draws(Matrix4x4 a, Matrix4x4 b) => [
            new(Mesh: mesh, Material: 0, ObjectToWorld: a), new(Mesh: mesh, Material: 0, ObjectToWorld: b),
        ];
        Matrix4x4[] Previous() => MemoryMarshal.Cast<byte, Matrix4x4>(
            span: rig.Gpu.DeviceLocal(sizeBytes: 128, part: "previous-mesh-transforms")).ToArray();
        rig.Render(time: 0f, meshDraws: Draws(a: first, b: second));
        Assert.Equal(expected: new[] { first, second }, actual: Previous());
        var movedFirst = first * Matrix4x4.CreateRotationY(radians: 0.3f);
        rig.Render(time: 0f, meshDraws: Draws(a: movedFirst, b: second));
        Assert.Equal(expected: new[] { first, second }, actual: Previous());
        var movedSecond = second * Matrix4x4.CreateTranslation(xPosition: 4f, yPosition: 5f, zPosition: 6f);
        rig.Render(time: 0f, meshDraws: Draws(a: movedFirst, b: movedSecond));
        Assert.Equal(expected: new[] { movedFirst, second }, actual: Previous());
        Assert.Equal(expected: 64UL, actual: rig.Gpu.BufferCopyBytes);
        rig.Render(time: 0f, meshDraws: Draws(a: movedFirst, b: movedSecond));
        Assert.Equal(expected: new[] { movedFirst, movedSecond }, actual: Previous());
        Assert.Equal(expected: 64UL, actual: rig.Gpu.BufferCopyBytes);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());
        rig.Render(time: 0f, meshDraws: Draws(a: movedFirst, b: movedSecond));
        Assert.Equal(expected: 0UL, actual: rig.Gpu.BufferCopyBytes);
        Assert.Empty(collection: rig.Gpu.StateConflicts);
    }
}
