using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [InlineData(SdfIndirectParticipation.Default)]
    [InlineData(SdfIndirectParticipation.Cast)]
    [InlineData(SdfIndirectParticipation.Receive)]
    [InlineData(SdfIndirectParticipation.Off)]
    [Theory]
    public void PairedMeshParticipationFollowsProgramCompositionWithoutADrawRevision(SdfIndirectParticipation policy) {
        using var rig = new Rig(slots: 1);
        var mesh = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        var draws = new[] {
            new SdfMeshDraw(mesh, Matrix4x4.Identity, 0, "paired") { FieldBacked = true, Indirect = policy, IsDynamic = true },
            new SdfMeshDraw(mesh, Matrix4x4.Identity, 0, "independent") { Indirect = policy, IsDynamic = true },
        };
        var independent = Program(albedo: Vector3.One);
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.BeginInstance(Vector3.Zero, 2f).ResetPoint().Sphere(2f, material).EndInstance();
        builder.BeginInstance(Vector3.Zero, 1f, indirect: policy).ResetPoint().Sphere(1f, material, blend: SdfBlendOp.Subtraction).EndInstance();
        var coupled = builder.Build();
        var transforms = new[] { new DynamicTransform(Vector3.Zero, Quaternion.Identity) };

        Render(program: independent);
        AssertPolicies(paired: policy);
        var meshRevision = rig.Engine.LightGeometry.Mesh;
        var revisionStep = ((policy == SdfIndirectParticipation.Cast) ? 0 : 1);

        rig.Engine.UploadProgram(program: coupled);
        Render(program: coupled);
        AssertPolicies(paired: SdfIndirectParticipation.Cast);
        Assert.Equal(expected: (meshRevision + revisionStep), actual: rig.Engine.LightGeometry.Mesh);
        Render(program: coupled);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());

        rig.Engine.UploadProgram(program: independent);
        Render(program: independent);
        AssertPolicies(paired: policy);
        Assert.Equal(expected: (meshRevision + (2 * revisionStep)), actual: rig.Engine.LightGeometry.Mesh);
        Assert.Empty(collection: rig.Gpu.StateConflicts);

        void Render(SdfProgram program) {
            rig.Gpu.ResetTallies();
            rig.Engine.Pack(frame: Frame(program: program, time: 0f, transforms: transforms) with { MeshDraws = draws });
            rig.Engine.SubmitUpload();
        }
        void AssertPolicies(SdfIndirectParticipation paired) {
            var words = MemoryMarshal.Cast<byte, uint>(span: rig.Gpu.DeviceLocal(part: "mesh-region", sizeBytes: rig.Engine.MeshRegionBytes));

            Assert.Equal(expected: ((uint)paired), actual: ((words[20] & SdfProgram.IndirectInstanceMask) >> SdfProgram.IndirectInstanceShift));
            Assert.Equal(expected: ((uint)policy), actual: ((words[(SdfMeshRegion.DrawWords + 20)] & SdfProgram.IndirectInstanceMask) >> SdfProgram.IndirectInstanceShift));
        }
    }
}
