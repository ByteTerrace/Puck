using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a draw placed by its slot (<see cref="SdfMeshDraw.PlacedBySlot"/>) keeps no pose, so a body that only moved
/// makes the dresser write no draw: neither a moving group's rows nor a moving rigid copy's own mesh change, the rows'
/// revision and journal stand, and every CPU reader places the draw where its slot's row now stands
/// (<see cref="SdfMeshDraw.PlacementIn"/>). A static copy's pose is its draws' own, so moving one changes its revision.
/// </summary>
public sealed class SdfMeshDresserSlotLawTests {
    [Fact]
    public void ABodyThatOnlyMovedMakesTheDresserWriteNoDraw() {
        using var producer = new SdfWorldTablesUploadLawTests.RootCutProducer(skinned: false, secondGroup: true);
        var mesh = SdfWorldTablesUploadLawTests.HostDraw(identity: "mesh", x: 0f).Mesh;
        var copy = new SdfMeshInstance(-1, "rigid", new(Vector3.Zero, Quaternion.Identity), 1f, true) { Mesh = mesh };
        var first = producer.Dress(frame: (producer.FrameAt(1, 1, 1) with { MeshInstances = [copy] }));
        var rows = first.MeshRows!;
        var (revision, serial, live) = (rows.Revision, rows.Serial, rows.LiveCount);
        var copyRevision = copy.Revision;

        Assert.True(condition: copy.Update(-1, "rigid", null, new(new Vector3(x: 4f, y: 0f, z: 0f), Quaternion.Identity), 1f, true, null, mesh, 0, []));
        Assert.Equal(expected: copyRevision, actual: copy.Revision);

        var moved = producer.Dress(frame: (producer.FrameAt(5, 7, 1) with { MeshInstances = [copy] }));

        Assert.Same(expected: rows, actual: moved.MeshRows);
        Assert.Equal(expected: revision, actual: rows.Revision);
        Assert.Equal(expected: serial, actual: rows.Serial);
        Assert.Equal(expected: live, actual: rows.LiveCount);
        Assert.Equal(expected: 3, actual: live);
        foreach (var (_, draw) in rows.Live()) {
            Assert.True(condition: draw.PlacedBySlot);
            Assert.Equal(expected: moved.DynamicTransforms[draw.FrameSlot!.Value].Position, actual: draw.PlacementIn(transforms: moved.DynamicTransforms).Translation);
            Assert.Throws<InvalidOperationException>(testCode: () => draw.FixedPlacement);
        }
    }
    [Fact]
    public void AStaticCopyThatMovedChangesItsRevision() {
        var copy = new SdfMeshInstance(-1, "fixed", new(Vector3.Zero, Quaternion.Identity), 1f, false);
        var revision = copy.Revision;

        Assert.True(condition: copy.Update(-1, "fixed", null, new(Vector3.UnitX, Quaternion.Identity), 1f, false, null, null, 0, []));
        Assert.Equal(expected: (revision + 1), actual: copy.Revision);
    }
    [Fact]
    public void ADrawPlacedByItsSlotComparesEqualWhereverItsProducerPosedIt() {
        var mesh = SdfWorldTablesUploadLawTests.HostDraw(identity: "mesh", x: 0f).Mesh;
        var here = new SdfMeshDraw(identity: "body", material: 0, mesh: mesh, objectToWorld: (Matrix4x4.CreateScale(scale: 2f) * Matrix4x4.CreateTranslation(xPosition: 1f, yPosition: 0f, zPosition: 0f))) { FrameSlot = 0, IsDynamic = true };
        var there = here.PlacedAt(objectToWorld: (Matrix4x4.CreateScale(scale: 2f) * Matrix4x4.CreateTranslation(xPosition: 9f, yPosition: 3f, zPosition: 0f)));
        var larger = here.PlacedAt(objectToWorld: Matrix4x4.CreateScale(scale: 3f));
        DynamicTransform[] transforms = [new(new Vector3(x: 5f, y: 6f, z: 7f), Quaternion.Identity)];

        Assert.Equal(expected: here, actual: there);
        Assert.True(condition: here.SameStagedContent(other: in there));
        Assert.Equal(expected: SdfMeshDrawDifference.None, actual: there.StagedDifference(other: in here));
        Assert.NotEqual(expected: here, actual: larger);
        Assert.Equal(expected: SdfMeshDrawDifference.Matrix, actual: larger.StagedDifference(other: in here));
        Assert.Equal(expected: (Matrix4x4.CreateScale(scale: 2f) * Matrix4x4.CreateTranslation(xPosition: 5f, yPosition: 6f, zPosition: 7f)), actual: there.PlacementIn(transforms: transforms));
    }
}
