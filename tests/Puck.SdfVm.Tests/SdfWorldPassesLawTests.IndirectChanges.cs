using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Theory]
    [InlineData("color")]
    [InlineData("direction")]
    [InlineData("point-position")]
    [InlineData("slot-owner")]
    public void EveryChangedLightSourceRelightsStoredHitsWithoutReadmittingTransport(string change) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.BeginInstance(Vector3.Zero, 1f).ResetPoint().Sphere(.5f, material).EndInstance();
        var lights = SdfLights.Default();
        lights.ShadowSlots.SetOwner(0, "held");
        lights.Set(1, new SdfLight(SdfLightKind.Point, new Vector3(100), Vector3.One, 1, .1f, Shadows: false));
        lights.Count = 2;
        var source = Frame() with { Program = builder.Build(), Lights = lights, FarDistance = 1f, IndirectTier = SdfIndirectTier.Medium };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(() => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-light-changes", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(new FakeGpuDevice());
        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var tables = residency.Tables!;
        var cache = tables.Indirect!;
        (int Places, int Partitions, int Traces, int Shades) Step() {
            residency.BeginFrame();
            Assert.True(residency.Prepare(context));
            _ = residency.Submit(context);
            var admitted = (cache.PlaceCount, cache.ClassifyCount, cache.TraceCount, cache.ShadeCount);
            // Commit the actual admitted host batches; the fake device supplies no classification or radiance answer.
            cache.Submitted();
            cache.SubmittedLighting();
            return admitted;
        }
        TestLiveness.Within(frames: 64, step: () => {
            _ = Step();
            return cache.IsComplete && cache.LightingComplete;
        }, building: () => false, reason: () => "The initial finite host solve has not completed.");
        var held = cache.PublishedLightingSource;
        Assert.NotNull(held);
        var epoch = cache.Epoch;
        var certificate = cache.CertificateRevision;
        var geometry = tables.LightGeometry;
        var primary = tables.PassSignature(source, 0, SdfWorldPackage.Parts.Primary);
        var expectedShades = cache.Snapshot().Bricks.Count * SdfIndirectLayout.ProbesPerBrick * (cache.Layout.BounceLimit + 1);
        var changed = new SdfLights();
        changed.CopyFrom(lights);
        switch (change) {
            case "color": changed.Set(0, changed[0] with { Color = Vector3.UnitX }); break;
            case "direction": changed.Set(0, changed[0] with { Direction = Vector3.UnitY }); break;
            case "point-position": changed.Set(1, changed[1] with { Direction = new Vector3(0, .6f, 0) }); break;
            case "slot-owner": changed.ShadowSlots.SetOwner(0, "replacement"); break;
        }
        source = source with { Lights = changed };
        var first = Step();
        Assert.Equal((0, 0, 0), (first.Places, first.Partitions, first.Traces));
        Assert.True(first.Shades > 0);
        Assert.Equal(epoch, cache.Epoch);
        Assert.Equal(certificate, cache.CertificateRevision);
        Assert.Equal(geometry, tables.LightGeometry);
        Assert.Equal(primary, tables.PassSignature(source, 0, SdfWorldPackage.Parts.Primary));
        var totalShades = first.Shades;
        TestLiveness.Within(frames: 64, step: () => {
            if (cache.LightingComplete) { return true; }
            var admitted = Step();
            Assert.Equal((0, 0, 0), (admitted.Places, admitted.Partitions, admitted.Traces));
            totalShades += admitted.Shades;
            return cache.LightingComplete;
        }, building: () => false, reason: () => "The changed light's finite host solve has not completed.");
        Assert.Equal(expectedShades, totalShades);
        Assert.NotSame(held, cache.PublishedLightingSource);
        var published = cache.PublishedLightingSource!.CopyFrame();
        Assert.Equal(changed.Records.ToArray(), published.Lights.Records.ToArray());
        Assert.Equal(changed.ShadowSlots.Owner(0), published.Lights.ShadowSlots.Owner(0));
        Assert.Equal(lights.Records.ToArray(), held.CopyFrame().Lights.Records.ToArray());
        Assert.Equal("held", held.CopyFrame().Lights.ShadowSlots.Owner(0));
        Assert.Equal((0, 0, 0, 0), Step());
    }
}
