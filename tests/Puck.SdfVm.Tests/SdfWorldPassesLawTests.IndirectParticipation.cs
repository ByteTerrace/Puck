using System.Numerics;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void ReceiverOnlyBodyMotionStandsButChangingItsCastingPolicyWithdrawsTransport() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.BeginInstanceDynamic(0, Vector3.Zero, 1f).ResetPoint().TransformDynamic(slot: 0).Sphere(0.5f, material).EndInstance();
        var pose = new DynamicTransform(Vector3.Zero, Quaternion.Identity);
        var mesh = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        var draw = new SdfMeshDraw(mesh, Matrix4x4.Identity, material, "body") { FieldBacked = true, IsDynamic = true };
        var source = Frame() with {
            Program = builder.Build(),
            FarDistance = 1f,
            IndirectTier = SdfIndirectTier.Medium,
            DynamicTransforms = new[] { pose },
            MeshDraws = new[] { draw },
        };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-participation", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(gpu: new FakeGpuDevice());

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var tables = residency.Tables!;
        var cache = tables.Indirect!;
        var demand = SdfWorldTables.IndirectInputs(frame: source);

        TestLiveness.Within(frames: 64, step: () => { cache.Plan(inputs: demand); cache.Submitted(); return cache.IsComplete; },
            building: () => false, reason: () => "The fixed transport demand has not completed.");
        var geometry = tables.LightGeometry;
        var certificate = cache.CertificateRevision;

        source = source with {
            DynamicTransforms = new[] { pose with { Position = new Vector3(x: 64f, y: 0f, z: 0f) } },
            MeshDraws = new[] { draw with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 64f, yPosition: 0f, zPosition: 0f) } },
            MeshDrawsRevision = 1,
        };
        residency.BeginFrame();
        Assert.True(condition: residency.Prepare(context: context));
        Assert.True(condition: cache.IsComplete);
        Assert.Equal(geometry, tables.LightGeometry);
        Assert.Equal(certificate, cache.CertificateRevision);

        source = source with { IndirectBodies = SdfIndirectParticipation.Cast };
        residency.BeginFrame();
        Assert.True(condition: residency.Prepare(context: context));
        Assert.False(condition: cache.IsComplete);
        Assert.NotEqual(geometry, tables.LightGeometry);
        cache.Plan(inputs: demand);
        Assert.True(condition: (cache.CertificateRevision > certificate));
        Assert.All(cache.Snapshot().Bricks, brick => Assert.All(brick.SubmittedStrata, mask => Assert.Equal(actual: mask, expected: 0u)));
    }
}
