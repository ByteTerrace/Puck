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

        LandRootCut(frame: first, rig: rig);
        var firstDraw = Assert.Single(collection: first.DressedDraws());
        var moving = producer.Publish(rootX: 2, memberY: 1, unusedZ: 1);

        if (!skipMotion) { RenderRootCut(frame: moving, rig: rig); }
        var generation = RootCutGeneration(rig: rig);
        var destination = (skipMotion ? 2f : 9f);
        var cut = producer.Publish(cutSlot: 0, memberY: 1, rootX: destination, unusedZ: 1);

        if (skipMotion) { Assert.False(condition: producer.RootBytesMoved); }
        RenderRootCut(frame: cut, rig: rig);

        Assert.Same(first.Program, cut.Program);
        Assert.Same(firstDraw.Mesh, Assert.Single(collection: cut.DressedDraws()).Mesh);
        Assert.Equal(firstDraw.Identity, cut.DressedDraws()[0].Identity);
        Assert.Equal(0, cut.DressedDraws()[0].FrameSlot);
        AssertRootCutPrevious(rig: rig, x: destination);
        Assert.True(condition: (RootCutGeneration(rig: rig) > generation));
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

        if (!skipMotion) { RenderRootCut(frame: moving, rig: rig); }
        var generation = RootCutGeneration(rig: rig);
        var root = (skipMotion ? 2f : 3f);
        var member = (skipMotion ? 2f : 9f);

        RenderRootCut(rig, producer.Publish(cutSlot: 1, memberY: member, rootX: root, unusedZ: 1));

        AssertRootCutPrevious(rig: rig, x: root);
        Assert.Equal(member, RootCutPreviousMemberY(rig: rig));
        Assert.True(condition: (RootCutGeneration(rig: rig) > generation));

        generation = RootCutGeneration(rig: rig);
        RenderRootCut(rig, producer.Publish(cutSlot: 2, memberY: (member + 1), rootX: (root + 1), unusedZ: 9));
        AssertRootCutPrevious(rig: rig, x: root);
        Assert.Equal(member, RootCutPreviousMemberY(rig: rig));
        Assert.Equal(generation, RootCutGeneration(rig: rig));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnEqualRowReseatSeedsDynamicHistoryWithoutClaimingByteMotionIncludingInsideARepack(bool repack) {
        using var rig = new Rig(slots: 1);
        var poses = new[] { new DynamicTransform(new Vector3(x: 1, y: 0, z: 0), Quaternion.Identity) };
        var moved = new SdfMovedTransforms();

        moved.Begin(everything: true, tableRows: 1);
        RenderRootCut(rig, Frame(program: rig.LiveProgram, time: 0, transforms: poses) with { MovedTransforms = moved });
        moved.Begin(everything: false, tableRows: 1);
        var before = poses.ToArray();

        poses[0] = new DynamicTransform(new Vector3(x: 2, y: 0, z: 0), Quaternion.Identity);
        Assert.True(condition: moved.Commit(poses, 0, before, reseat: false));
        // The consumer skipped that ordinary movement. A cut at the same current pose still has to reach it.
        before = poses.ToArray();
        if (repack) { moved.BeginRepack(tableRows: 1); } else { moved.Begin(everything: false, tableRows: 1); }
        Assert.False(condition: moved.Commit(poses, 0, before, reseat: true));
        if (repack) { moved.CompleteRepack(current: poses, previous: before); }
        RenderRootCut(rig, Frame(program: rig.LiveProgram, time: 0, transforms: poses) with { MovedTransforms = moved });

        Assert.Equal(2f, MemoryMarshal.Read<float>(source: rig.Gpu.DeviceLocal(part: "previous-dynamic-transforms", sizeBytes: DynamicTransformBytes)));
    }

    internal static void RenderRootCut(Rig rig, SdfFrame frame) {
        rig.Engine.GpuWork.Pump(tables: rig.Engine);
        rig.Engine.Pack(frame: frame);
        rig.Engine.SubmitUpload();
        rig.Engine.GpuWork.Pump(tables: rig.Engine);
    }
    // Renders a frame until the mesh region owes none of its meshes.
    internal static void LandRootCut(Rig rig, SdfFrame frame) {
        RenderRootCut(frame: frame, rig: rig);
        for (var count = 0; (rig.Engine.MeshesLanding || rig.Engine.DrawsAwaitMeshes); count++) {
            Assert.True(condition: (count < 64));
            RenderRootCut(frame: frame, rig: rig);
        }
    }

    private static uint RootCutGeneration(Rig rig) => MemoryMarshal.Cast<byte, uint>(span: rig.Gpu.DeviceLocal(sizeBytes: rig.Engine.MeshRegionBytes, part: "mesh-region"))[(SdfMeshRecord.SelectionWord + 11)];
    private static float RootCutPreviousMemberY(Rig rig) => MemoryMarshal.Cast<byte, float>(span: rig.Gpu.DeviceLocal(part: "previous-dynamic-transforms", sizeBytes: (3 * DynamicTransformBytes)))[13];
    private static void AssertRootCutPrevious(Rig rig, float x) {
        uint[] expected = [0x3f800000, 0, 0, 0, 0, 0x3f800000, 0, 0, 0, 0, 0x3f800000, 0, BitConverter.SingleToUInt32Bits(value: x), 0, 0, 0x3f800000];
        var words = MemoryMarshal.Cast<byte, uint>(span: rig.Gpu.DeviceLocal(part: "previous-mesh-transforms", sizeBytes: (SdfMeshRecord.MatrixWords * sizeof(uint))));

        Assert.Equal(expected, words[..SdfMeshRecord.MatrixWords].ToArray());
    }

    internal sealed class RootCutProducer : IDisposable {
        private readonly MeshBakeTestRun m_run;

        private readonly SdfMovedTransforms m_moved = new();
        private readonly DynamicTransform[] m_poses = [new(Vector3.Zero, Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity)];

        public SdfProgram Program { get; }
        public bool RootBytesMoved { get; private set; }

        public RootCutProducer(bool skinned, bool secondGroup = false) {
            var builder = new SdfProgramBuilder();

            builder.AddMaterial(material: new SdfMaterial(Vector3.One));
            builder.BeginInstanceDynamic(0, Vector3.Zero, 2, separateBake: true, boundInSlotFrame: true).ResetPoint().TransformDynamic(slot: 0).Sphere(1, 0).EndInstance();
            if (secondGroup) { builder.BeginInstanceDynamic(1, Vector3.Zero, 2, separateBake: true, boundInSlotFrame: true).ResetPoint().TransformDynamic(slot: 1).Sphere(1, 0).EndInstance(); }
            Program = builder.Build();
            var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 },
                members: (skinned ? new[] { new SdfMeshMember(1, Vector3.Zero, Quaternion.Identity), new SdfMeshMember(2, Vector3.Zero, Quaternion.Identity) } : default),
                vertexMembers: (skinned ? new byte[] { 0, 0, SdfMesh.NoMember } : default));

            m_run = new MeshBakeTestRun(new SdfMeshPathBake { PlannedTier = static _ => 0 });
            foreach (var group in SdfMeshBakeGroup.Plan(Program, m_poses, 40, static (slot, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: slot))) {
                m_run.Bake.Library.Store(group.Sealed, mesh, group.MaterialOrder, form: group.Form);
            }
        }

        public SdfFrame Publish(float rootX, float memberY, float unusedZ, int cutSlot = -1) {
            return Dress(frame: FrameAt(cutSlot: cutSlot, memberY: memberY, rootX: rootX, unusedZ: unusedZ));
        }
        public SdfFrame FrameAt(float rootX, float memberY, float unusedZ, int cutSlot = -1) {
            var previous = m_poses.ToArray();

            m_moved.Begin(everything: false, tableRows: m_poses.Length);
            m_poses[0] = new DynamicTransform(new Vector3(x: rootX, y: 0, z: 0), Quaternion.Identity);
            m_poses[1] = new DynamicTransform(new Vector3(x: 0, y: memberY, z: 0), Quaternion.Identity);
            m_poses[2] = new DynamicTransform(new Vector3(x: 0, y: 0, z: unusedZ), Quaternion.Identity);
            for (var slot = 0; (slot < m_poses.Length); slot++) {
                var moved = m_moved.Commit(m_poses, slot, previous.AsSpan(length: 1, start: slot), reseat: (slot == cutSlot));

                if (slot == 0) { RootBytesMoved = moved; }
            }
            return Frame(program: Program, time: 0, transforms: m_poses) with { MovedTransforms = m_moved };
        }
        public SdfFrame Dress(SdfFrame frame) => m_run.Dresser.Dress(frame: frame);
        public void Dispose() => m_run.Dispose();
    }
}
