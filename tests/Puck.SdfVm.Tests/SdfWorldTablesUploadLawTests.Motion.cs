using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [Fact]
    public void PreviousTransformsFollowConsumedFramesAndSettleWithoutAnotherHostUpload() {
        using var rig = new Rig(slots: 4);

        rig.Warm();
        byte[] Current() => rig.Gpu.DeviceLocal(part: "dynamic-transforms", sizeBytes: (4 * DynamicTransformBytes)).ToArray();
        byte[] Previous() => rig.Gpu.DeviceLocal(part: "previous-dynamic-transforms", sizeBytes: (4 * DynamicTransformBytes)).ToArray();
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
            new(Identity: "a", Material: 0, Mesh: mesh, ObjectToWorld: a), new(Identity: "b", Material: 0, Mesh: mesh, ObjectToWorld: b),
        ];
        Matrix4x4[] Previous() => MemoryMarshal.Cast<byte, Matrix4x4>(
            span: rig.Gpu.DeviceLocal(part: "previous-mesh-transforms", sizeBytes: 128)).ToArray();
        rig.Render(time: 0f, meshDraws: Draws(a: first, b: second));
        Assert.Equal(expected: new[] { first, second }, actual: Previous());
        var movedFirst = (first * Matrix4x4.CreateRotationY(radians: 0.3f));

        rig.Render(time: 0f, meshDraws: Draws(a: movedFirst, b: second));
        Assert.Equal(expected: new[] { first, second }, actual: Previous());
        var movedSecond = (second * Matrix4x4.CreateTranslation(xPosition: 4f, yPosition: 5f, zPosition: 6f));

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
    // A slot that changed owner has no previous pose of its own: a program upload into the same tables seeds every
    // previous row, a reseated slot seeds its own, and an ordinary move beside it keeps its motion. The pose revisions
    // move only on an upload that changes a pose.
    [Fact]
    public void AProgramUploadAndAReseatedSlotSeedTheirPreviousPosesWithoutARebuild() {
        using var rig = new Rig(slots: 4);

        rig.Warm();
        byte[] Current() => rig.Gpu.DeviceLocal(part: "dynamic-transforms", sizeBytes: (4 * DynamicTransformBytes)).ToArray();
        byte[] Previous() => rig.Gpu.DeviceLocal(part: "previous-dynamic-transforms", sizeBytes: (4 * DynamicTransformBytes)).ToArray();
        byte[] Row(byte[] table, int slot) => table.AsSpan(length: DynamicTransformBytes, start: (slot * DynamicTransformBytes)).ToArray();
        var still = rig.Engine.PoseRevision;

        rig.Render(time: 0f);
        Assert.Equal(expected: still, actual: rig.Engine.PoseRevision);
        Assert.Equal(expected: still, actual: rig.Engine.PreviousPoseRevision);

        var before = Current();

        rig.Move(slot: 3);
        rig.Reseat(slot: 2);
        rig.Render(time: 0f);
        Assert.Equal(expected: (still + 1), actual: rig.Engine.PoseRevision);
        Assert.Equal(expected: still, actual: rig.Engine.PreviousPoseRevision);
        Assert.Equal(expected: Row(table: Current(), slot: 2), actual: Row(table: Previous(), slot: 2));
        Assert.Equal(expected: Row(slot: 3, table: before), actual: Row(table: Previous(), slot: 3));
        Assert.NotEqual(expected: Row(slot: 3, table: before), actual: Row(table: Current(), slot: 3));

        rig.Move(slot: 1);
        rig.UploadProgram();
        rig.Render(time: 0f);
        Assert.Equal(expected: Current(), actual: Previous());
        Assert.Equal(expected: (still + 2), actual: rig.Engine.PoseRevision);
        Assert.Empty(collection: rig.Gpu.StateConflicts);
    }
    // A draw is continuous by identity, not by index: a list reordered at the same count, or a new draw at an index
    // another held, reads as still rather than as the other draw's motion.
    [Fact]
    public void AMeshDrawWhoseIdentityChangesAtItsIndexIsSeeded() {
        using var rig = new Rig(slots: 1);
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            indices: new uint[] { 0, 1, 2 });
        var first = Matrix4x4.CreateTranslation(xPosition: 1f, yPosition: 2f, zPosition: 3f);
        var second = Matrix4x4.CreateScale(xScale: 2f, yScale: 3f, zScale: 4f);
        var third = Matrix4x4.CreateTranslation(xPosition: -4f, yPosition: 0f, zPosition: 1f);

        Matrix4x4[] Previous() => MemoryMarshal.Cast<byte, Matrix4x4>(
            span: rig.Gpu.DeviceLocal(part: "previous-mesh-transforms", sizeBytes: 128)).ToArray();

        rig.Render(time: 0f, meshDraws: [
            new(Identity: "a", Material: 0, Mesh: mesh, ObjectToWorld: first), new(Identity: "b", Material: 0, Mesh: mesh, ObjectToWorld: second),
        ]);
        rig.Render(time: 0f, meshDraws: [
            new(Identity: "b", Material: 0, Mesh: mesh, ObjectToWorld: second), new(Identity: "a", Material: 0, Mesh: mesh, ObjectToWorld: first),
        ]);
        Assert.Equal(expected: new[] { second, first }, actual: Previous());
        rig.Render(time: 0f, meshDraws: [
            new(Identity: "b", Material: 0, Mesh: mesh, ObjectToWorld: second), new(Identity: "c", Material: 0, Mesh: mesh, ObjectToWorld: third),
        ]);
        Assert.Equal(expected: new[] { second, third }, actual: Previous());
        Assert.Empty(collection: rig.Gpu.StateConflicts);
    }
}
