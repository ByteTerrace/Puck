using System.Numerics;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData("translation")]
    [InlineData("rotation")]
    [InlineData("lanes")]
    [InlineData("shadow-suppression")]
    [Theory]
    public void ActualPackedPoseChangesWithdrawTransportAtTheOldGeometry(string change) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 1f, indirect: SdfIndirectParticipation.Cast)
            .ResetPoint().TransformDynamic(slot: 0).Box(halfExtents: new Vector3(x: 0.4f, y: 0.2f, z: 0.1f), round: 0f, material: material).EndInstance();
        var transform = new DynamicTransform(Vector3.Zero, Quaternion.Identity);
        var source = Frame() with {
            Program = builder.Build(),
            FarDistance = 1f,
            IndirectTier = SdfIndirectTier.Medium,
            DynamicTransforms = new[] { transform },
        };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-motion", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(gpu: new FakeGpuDevice());

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;
        // Keep demand fixed so new brick admission cannot masquerade as invalidation of the old geometry.
        var demand = SdfWorldTables.IndirectInputs(frame: source);

        TestLiveness.Within(frames: 64, step: () => {
            cache.Plan(inputs: demand);
            cache.Submitted();
            return cache.IsComplete;
        }, building: () => false, reason: () => "The fixed transport demand has not completed.");
        CompleteSolve(cache: cache);
        var before = cache.Snapshot();

        Assert.Contains(collection: before.Bricks, filter: brick => brick.SubmittedStrata.Any(predicate: mask => (mask != 0u)));
        var certificate = cache.CertificateRevision;

        // An identical freshly captured table is not motion, even though the ordinary pack path visits every row.
        source = source with { DynamicTransforms = new[] { transform } };
        residency.BeginFrame();
        Assert.True(condition: residency.Prepare(context: context));
        Assert.True(condition: cache.IsComplete);
        Assert.Equal(certificate, cache.CertificateRevision);
        transform = change switch {
            "translation" => transform with { Position = new Vector3(x: 64f, y: 0f, z: 0f) },
            "rotation" => transform with { Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f) },
            "lanes" => transform with { Lanes = new Vector4(w: 0f, x: 0.5f, y: 0f, z: 0f) },
            _ => transform with { CastsSoftShadow = false },
        };
        source = source with { DynamicTransforms = new[] { transform } };
        residency.BeginFrame();
        Assert.True(condition: residency.Prepare(context: context));
        if (change == "shadow-suppression") {
            Assert.True(condition: cache.IsComplete);
            Assert.Equal(certificate, cache.CertificateRevision);
            return;
        }
        Assert.False(condition: cache.IsComplete);
        Assert.Equal(certificate, cache.CertificateRevision);
        cache.Plan(inputs: demand);
        Assert.True(condition: (cache.CertificateRevision > certificate));
        Assert.Equal(before.Allocation, cache.History.Allocation);
        Assert.Equal(before.Epoch, cache.Epoch);
        var after = cache.Snapshot();

        Assert.Contains(collection: after.Bricks, filter: brick => (brick.SubmittedStrata.All(predicate: mask => (mask == 0u)) &&
            before.Bricks.Single(predicate: previous => (previous.Key == brick.Key)).SubmittedStrata.Any(predicate: mask => (mask != 0u))));
    }

    // Publishes the current transport's finite solve, completing the cycle a queued geometry change waits for.
    private static void CompleteSolve(SdfIndirectCache cache) {
        cache.BeginLighting();
        while (!cache.LightingComplete) { cache.PlanLighting(); cache.SubmittedLighting(); }
    }
}
