using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AStableNoncopyRootCutSeedsItsPreviousMatrixAndGenerationEvenAtTheSamePackedPose(bool skipMotion) {
        using var producer = new RootCutProducer(skinned: false);
        using var rig = new Rig(slots: 3);
        rig.Engine.UploadProgram(producer.Program);
        var first = producer.Publish(rootX: 1, memberY: 1, unusedZ: 1);
        LandRootCut(rig, first);
        var firstDraw = Assert.Single(first.MeshDraws);
        var moving = producer.Publish(rootX: 2, memberY: 1, unusedZ: 1);
        if (!skipMotion) { RenderRootCut(rig, moving); }
        var generation = RootCutGeneration(rig);
        var destination = skipMotion ? 2f : 9f;
        var cut = producer.Publish(rootX: destination, memberY: 1, unusedZ: 1, cutSlot: 0);
        if (skipMotion) { Assert.False(producer.RootBytesMoved); }
        RenderRootCut(rig, cut);

        Assert.Same(first.Program, cut.Program);
        Assert.Same(firstDraw.Mesh, Assert.Single(cut.MeshDraws).Mesh);
        Assert.Equal(firstDraw.Identity, cut.MeshDraws[0].Identity);
        Assert.Equal(0, cut.MeshDraws[0].FrameSlot);
        AssertRootCutPrevious(rig, destination);
        Assert.True(RootCutGeneration(rig) > generation);
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AShaderReachableMemberCutInvalidatesTheDrawWhileAnUnusedMemberEntryDoesNot(bool skipMotion) {
        using var producer = new RootCutProducer(skinned: true);
        using var rig = new Rig(slots: 3);
        rig.Engine.UploadProgram(producer.Program);
        LandRootCut(rig, producer.Publish(rootX: 1, memberY: 1, unusedZ: 1));
        var moving = producer.Publish(rootX: 2, memberY: 2, unusedZ: 1);
        if (!skipMotion) { RenderRootCut(rig, moving); }
        var generation = RootCutGeneration(rig);
        var root = skipMotion ? 2f : 3f;
        var member = skipMotion ? 2f : 9f;
        RenderRootCut(rig, producer.Publish(rootX: root, memberY: member, unusedZ: 1, cutSlot: 1));

        AssertRootCutPrevious(rig, root);
        Assert.Equal(member, RootCutPreviousMemberY(rig));
        Assert.True(RootCutGeneration(rig) > generation);

        generation = RootCutGeneration(rig);
        RenderRootCut(rig, producer.Publish(rootX: root + 1, memberY: member + 1, unusedZ: 9, cutSlot: 2));
        AssertRootCutPrevious(rig, root);
        Assert.Equal(member, RootCutPreviousMemberY(rig));
        Assert.Equal(generation, RootCutGeneration(rig));
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnEqualRowReseatSeedsDynamicHistoryWithoutClaimingByteMotionIncludingInsideARepack(bool repack) {
        using var rig = new Rig(slots: 1);
        var poses = new[] { new DynamicTransform(new Vector3(1, 0, 0), Quaternion.Identity) };
        var moved = new SdfMovedTransforms();
        moved.Begin(everything: true, tableRows: 1);
        RenderRootCut(rig, Frame(rig.LiveProgram, 0, poses) with { MovedTransforms = moved });
        moved.Begin(everything: false, tableRows: 1);
        var before = poses.ToArray();
        poses[0] = new DynamicTransform(new Vector3(2, 0, 0), Quaternion.Identity);
        Assert.True(moved.Commit(poses, 0, before, reseat: false));
        // The consumer skipped that ordinary movement. A cut at the same current pose still has to reach it.
        before = poses.ToArray();
        if (repack) { moved.BeginRepack(tableRows: 1); } else { moved.Begin(everything: false, tableRows: 1); }
        Assert.False(moved.Commit(poses, 0, before, reseat: true));
        if (repack) { moved.CompleteRepack(before, poses); }
        RenderRootCut(rig, Frame(rig.LiveProgram, 0, poses) with { MovedTransforms = moved });

        Assert.Equal(2f, MemoryMarshal.Read<float>(rig.Gpu.DeviceLocal(sizeBytes: DynamicTransformBytes, part: "previous-dynamic-transforms")));
    }

    private static void RenderRootCut(Rig rig, SdfFrame frame) {
        rig.Engine.GpuWork.Pump(rig.Engine);
        rig.Engine.Pack(frame);
        rig.Engine.SubmitUpload();
        rig.Engine.GpuWork.Pump(rig.Engine);
    }
    // Renders a frame until the mesh region owes none of its meshes.
    private static void LandRootCut(Rig rig, SdfFrame frame) {
        RenderRootCut(rig, frame);
        for (var count = 0; rig.Engine.MeshesLanding; count++) {
            Assert.True(count < 64);
            RenderRootCut(rig, frame);
        }
    }

    private static uint RootCutGeneration(Rig rig) => MemoryMarshal.Cast<byte, uint>(rig.Gpu.DeviceLocal(sizeBytes: rig.Engine.MeshRegionBytes, part: "mesh-region"))[SdfMeshRecord.SelectionWord + 11];

    private static float RootCutPreviousMemberY(Rig rig) => MemoryMarshal.Cast<byte, float>(rig.Gpu.DeviceLocal(sizeBytes: (3 * DynamicTransformBytes), part: "previous-dynamic-transforms"))[13];

    private static void AssertRootCutPrevious(Rig rig, float x) {
        uint[] expected = [0x3f800000, 0, 0, 0, 0, 0x3f800000, 0, 0, 0, 0, 0x3f800000, 0, BitConverter.SingleToUInt32Bits(x), 0, 0, 0x3f800000];
        var words = MemoryMarshal.Cast<byte, uint>(rig.Gpu.DeviceLocal(sizeBytes: (SdfMeshRecord.MatrixWords * sizeof(uint)), part: "previous-mesh-transforms"));
        Assert.Equal(expected, words[..SdfMeshRecord.MatrixWords].ToArray());
    }

    private sealed class RootCutProducer : IDisposable {
        private readonly MeshBakeTestRun m_run;
        private readonly SdfMovedTransforms m_moved = new();
        private readonly DynamicTransform[] m_poses = [new(Vector3.Zero, Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity)];
        public SdfProgram Program { get; }
        public bool RootBytesMoved { get; private set; }

        public RootCutProducer(bool skinned, bool secondGroup = false) {
            var builder = new SdfProgramBuilder();
            builder.AddMaterial(new SdfMaterial(Vector3.One));
            builder.BeginInstanceDynamic(0, Vector3.Zero, 2, separateBake: true, boundInSlotFrame: true).ResetPoint().TransformDynamic(0).Sphere(1, 0).EndInstance();
            if (secondGroup) { builder.BeginInstanceDynamic(1, Vector3.Zero, 2, separateBake: true, boundInSlotFrame: true).ResetPoint().TransformDynamic(1).Sphere(1, 0).EndInstance(); }
            Program = builder.Build();
            var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 },
                members: skinned ? new[] { new SdfMeshMember(1, Vector3.Zero, Quaternion.Identity), new SdfMeshMember(2, Vector3.Zero, Quaternion.Identity) } : default,
                vertexMembers: skinned ? new byte[] { 0, 0, SdfMesh.NoMember } : default);
            m_run = new MeshBakeTestRun(new SdfMeshPathBake());
            foreach (var group in SdfMeshBakeGroup.Plan(Program, m_poses, 40, static (slot, dynamic) => new SdfMeshBakeGroup(slot, dynamic))) {
                m_run.Bake.Library.Store(group.Sealed, mesh, group.MaterialOrder);
            }
        }

        public SdfFrame Publish(float rootX, float memberY, float unusedZ, int cutSlot = -1) {
            return Dress(FrameAt(rootX, memberY, unusedZ, cutSlot));
        }

        public SdfFrame FrameAt(float rootX, float memberY, float unusedZ, int cutSlot = -1) {
            var previous = m_poses.ToArray();
            m_moved.Begin(everything: false, tableRows: m_poses.Length);
            m_poses[0] = new DynamicTransform(new Vector3(rootX, 0, 0), Quaternion.Identity);
            m_poses[1] = new DynamicTransform(new Vector3(0, memberY, 0), Quaternion.Identity);
            m_poses[2] = new DynamicTransform(new Vector3(0, 0, unusedZ), Quaternion.Identity);
            for (var slot = 0; slot < m_poses.Length; slot++) {
                var moved = m_moved.Commit(m_poses, slot, previous.AsSpan(slot, 1), reseat: slot == cutSlot);
                if (slot == 0) { RootBytesMoved = moved; }
            }
            return Frame(Program, 0, m_poses) with { MovedTransforms = m_moved };
        }

        public SdfFrame Dress(SdfFrame frame) => m_run.Dresser.Dress(frame);

        public void Dispose() => m_run.Dispose();
    }
}
