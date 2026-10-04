using System.Numerics;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Theory]
    [InlineData("translation")]
    [InlineData("rotation")]
    [InlineData("lanes")]
    [InlineData("shadow-suppression")]
    public void ActualPackedPoseChangesWithdrawTransportAtTheOldGeometry(string change) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 1f, indirect: SdfIndirectParticipation.Cast)
            .ResetPoint().TransformDynamic(0).Box(halfExtents: new Vector3(0.4f, 0.2f, 0.1f), round: 0f, material: material).EndInstance();
        var transform = new DynamicTransform(Vector3.Zero, Quaternion.Identity);
        var source = Frame() with { Program = builder.Build(), FarDistance = 1f,
            IndirectTier = SdfIndirectTier.Medium, DynamicTransforms = new[] { transform } };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(() => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-motion", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(new FakeGpuDevice());
        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;
        // Keep demand fixed so new brick admission cannot masquerade as invalidation of the old geometry.
        var demand = SdfWorldTables.IndirectInputs(source);
        TestLiveness.Within(frames: 64, step: () => {
            cache.Plan(demand);
            cache.Submitted();
            return cache.IsComplete;
        }, building: () => false, reason: () => "The fixed transport demand has not completed.");
        var before = cache.Snapshot();
        Assert.Contains(before.Bricks, brick => brick.SubmittedStrata.Any(mask => mask != 0u));
        var certificate = cache.CertificateRevision;

        // An identical freshly captured table is not motion, even though the ordinary pack path visits every row.
        source = source with { DynamicTransforms = new[] { transform } };
        residency.BeginFrame();
        Assert.True(residency.Prepare(context));
        Assert.True(cache.IsComplete);
        Assert.Equal(certificate, cache.CertificateRevision);
        transform = change switch {
            "translation" => transform with { Position = new Vector3(64f, 0f, 0f) },
            "rotation" => transform with { Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f) },
            "lanes" => transform with { Lanes = new Vector4(0.5f, 0f, 0f, 0f) },
            _ => transform with { CastsSoftShadow = false },
        };
        source = source with { DynamicTransforms = new[] { transform } };
        residency.BeginFrame();
        Assert.True(residency.Prepare(context));
        if (change == "shadow-suppression") {
            Assert.True(cache.IsComplete);
            Assert.Equal(certificate, cache.CertificateRevision);
            return;
        }
        Assert.False(cache.IsComplete);
        Assert.Equal(certificate, cache.CertificateRevision);
        cache.Plan(demand);
        Assert.True(cache.CertificateRevision > certificate);
        Assert.Equal(before.Allocation, cache.History.Allocation);
        Assert.Equal(before.Epoch, cache.Epoch);
        var after = cache.Snapshot();
        Assert.Contains(after.Bricks, brick => brick.SubmittedStrata.All(mask => mask == 0u) &&
            before.Bricks.Single(previous => previous.Key == brick.Key).SubmittedStrata.Any(mask => mask != 0u));
    }
}
