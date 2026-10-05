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
        using var rig = new Rig(slots: 1, brickPoolVoxelCapacity: Dim * Dim * Dim);
        var frame = Frame(Program(Vector3.One), 0f, []);
        rig.Warm();
        rig.Engine.UpdateTablesSignature();
        ulong Signature() => wholeView ? rig.Engine.ViewSignature(frame, 0) : rig.Engine.PassSignature(frame, 0, SdfWorldPackage.Parts.Primary);
        void Stands() {
            var signature = Signature();
            rig.Render(time: 0f);
            Assert.Equal(signature, Signature());
            Assert.Equal(0L, rig.Gpu.HostBytes());
        }

        Stands();
        if (!bake) {
            var before = Signature();
            rig.Engine.UploadBrick(slot: 0, dimX: 1, dimY: 1, dimZ: 1, voxels: [1f]);
            rig.Render(time: 0f);
            Assert.Equal(BrickBakeState.Ready, rig.Engine.GetBrickState(0).State);
            Assert.NotEqual(before, Signature());
            Stands();
            return;
        }

        // Signature requests must see the upload that just finished, without another frame's table preparation.
        var request = new BrickBakeRequest(Vector3.Zero, 1f, 1, 1, 1, 1f, ReadOnlyMemory<Vector4>.Empty);
        rig.Engine.RequestBrickBake(0, request);
        var queued = Signature();
        rig.Render(time: 0f);
        Assert.Equal(BrickBakeState.Ready, rig.Engine.GetBrickState(0).State);
        Assert.NotEqual(queued, Signature());
        Stands();

        // 65 cubed exceeds one 256K-voxel slice. The same request serial spans both actual writes.
        rig.Engine.RequestBrickBake(0, request with { DimX = Dim, DimY = Dim, DimZ = Dim });
        queued = Signature();
        rig.Render(time: 0f);
        Assert.True(rig.Engine.LightGeometryMutable);
        var firstSlice = Signature();
        Assert.NotEqual(queued, firstSlice);
        rig.Render(time: 0f);
        Assert.False(rig.Engine.LightGeometryMutable);
        Assert.Equal(BrickBakeState.Ready, rig.Engine.GetBrickState(0).State);
        Assert.NotEqual(firstSlice, Signature());
        Stands();
    }
}
