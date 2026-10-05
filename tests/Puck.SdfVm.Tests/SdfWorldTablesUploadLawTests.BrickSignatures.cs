using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void BrickWritesChangeTheCurrentGeometrySignatureAndUnchangedUploadsStand(bool bake, bool wholeView) {
        const int Dim = 65;
        using var rig = new Rig(slots: 1, brickPoolVoxelCapacity: ((Dim * Dim) * Dim));
        var frame = Frame(program: Program(albedo: Vector3.One), time: 0f, transforms: []);

        rig.Warm();
        rig.Engine.UpdateTablesSignature();
        ulong Signature() => (wholeView ? rig.Engine.ViewSignature(frame: frame, view: 0) : rig.Engine.PassSignature(frame: frame, part: SdfWorldPackage.Parts.Primary, view: 0));
        void Stands() {
            var signature = Signature();

            rig.Render(time: 0f);
            Assert.Equal(signature, Signature());
            Assert.Equal(0L, rig.Gpu.HostBytes());
        }

        Stands();
        if (!bake) {
            var before = Signature();

            rig.Engine.UploadBrick(dimX: 1, dimY: 1, dimZ: 1, slot: 0, voxels: [1f]);
            rig.Render(time: 0f);
            Assert.Equal(BrickBakeState.Ready, rig.Engine.GetBrickState(slot: 0).State);
            Assert.NotEqual(before, Signature());
            Stands();
            return;
        }

        // Signature requests must see the upload that just finished, without another frame's table preparation.
        var request = new BrickBakeRequest(Vector3.Zero, 1f, 1, 1, 1, 1f, ReadOnlyMemory<Vector4>.Empty);

        rig.Engine.RequestBrickBake(request: request, slot: 0);
        var queued = Signature();

        rig.Render(time: 0f);
        Assert.Equal(BrickBakeState.Ready, rig.Engine.GetBrickState(slot: 0).State);
        Assert.NotEqual(queued, Signature());
        Stands();

        // 65 cubed exceeds one 256K-voxel slice. The same request serial spans both actual writes.
        rig.Engine.RequestBrickBake(0, request with { DimX = Dim, DimY = Dim, DimZ = Dim });
        queued = Signature();
        rig.Render(time: 0f);
        Assert.True(condition: rig.Engine.LightGeometryMutable);
        var firstSlice = Signature();

        Assert.NotEqual(actual: firstSlice, expected: queued);
        rig.Render(time: 0f);
        Assert.False(condition: rig.Engine.LightGeometryMutable);
        Assert.Equal(BrickBakeState.Ready, rig.Engine.GetBrickState(slot: 0).State);
        Assert.NotEqual(firstSlice, Signature());
        Stands();
    }
}
