using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData("color")]
    [InlineData("direction")]
    [InlineData("point-position")]
    [InlineData("slot-owner")]
    [Theory]
    public void EveryChangedLightSourceRelightsStoredHitsWithoutReadmittingTransport(string change) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.BeginInstance(Vector3.Zero, 1f).ResetPoint().Sphere(.5f, material).EndInstance();
        var lights = SdfLights.Default();

        lights.ShadowSlots.SetOwner(owner: "held", slot: 0);
        lights.Set(index: 1, light: new SdfLight(SdfLightKind.Point, new Vector3(value: 100), Vector3.One, 1, .1f, Shadows: false));
        lights.Count = 2;
        var source = Frame() with { Program = builder.Build(), Lights = lights, FarDistance = 1f, IndirectTier = SdfIndirectTier.Medium };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-light-changes", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(gpu: new FakeGpuDevice());

        residency.ProduceFirstFrame(context: context);
        var tables = residency.Tables!;
        var cache = tables.Indirect!;

        (int Places, int Partitions, int Traces, int Shades) Step() {
            residency.BeginFrame();
            Assert.True(condition: residency.Prepare(context: context));
            _ = residency.Submit(context: context);
            var admitted = (cache.PlaceCount, cache.ClassifyCount, cache.TraceCount, cache.ShadeCount);
            // Commit the actual admitted host batches; the fake device supplies no classification or radiance answer.
            cache.Submitted();
            cache.SubmittedLighting();
            return admitted;
        }
        TestLiveness.Within(frames: IndirectCompletionFrames(cache: cache, source: source), step: () => {
            _ = Step();
            return (cache.IsComplete && cache.LightingComplete);
        }, building: () => false, reason: () => "The initial finite host solve has not completed.");
        var held = cache.PublishedLightingSource;

        Assert.NotNull(@object: held);
        var epoch = cache.Epoch;
        var certificate = cache.CertificateRevision;
        var geometry = tables.LightGeometry;
        var primary = tables.PassSignature(frame: source, part: SdfWorldPackage.Parts.Primary, view: 0);
        var expectedShades = ((cache.Snapshot().Bricks.Count * SdfIndirectLayout.ProbesPerBrick) * (cache.Layout.BounceLimit + 1));
        var changed = new SdfLights();

        changed.CopyFrom(source: lights);
        switch (change) {
            case "color": changed.Set(index: 0, light: changed[0] with { Color = Vector3.UnitX }); break;
            case "direction": changed.Set(index: 0, light: changed[0] with { Direction = Vector3.UnitY }); break;
            case "point-position": changed.Set(index: 1, light: changed[1] with { Direction = new Vector3(x: 0, y: .6f, z: 0) }); break;
            case "slot-owner": changed.ShadowSlots.SetOwner(owner: "replacement", slot: 0); break;
        }
        source = source with { Lights = changed };
        var first = Step();

        Assert.Equal(actual: (first.Places, first.Partitions, first.Traces), expected: (0, 0, 0));
        Assert.True(condition: (first.Shades > 0));
        Assert.Equal(epoch, cache.Epoch);
        Assert.Equal(certificate, cache.CertificateRevision);
        Assert.Equal(geometry, tables.LightGeometry);
        Assert.Equal(primary, tables.PassSignature(frame: source, part: SdfWorldPackage.Parts.Primary, view: 0));
        var totalShades = first.Shades;

        TestLiveness.Within(frames: IndirectCompletionFrames(cache: cache, source: source), step: () => {
            if (cache.LightingComplete) { return true; }
            var admitted = Step();

            Assert.Equal(actual: (admitted.Places, admitted.Partitions, admitted.Traces), expected: (0, 0, 0));
            totalShades += admitted.Shades;
            return cache.LightingComplete;
        }, building: () => false, reason: () => "The changed light's finite host solve has not completed.");
        Assert.Equal(actual: totalShades, expected: expectedShades);
        Assert.NotSame(held, cache.PublishedLightingSource);
        var published = cache.PublishedLightingSource!.CopyFrame();

        Assert.Equal(changed.Records.ToArray(), published.Lights.Records.ToArray());
        Assert.Equal(changed.ShadowSlots.Owner(slot: 0), published.Lights.ShadowSlots.Owner(slot: 0));
        Assert.Equal(lights.Records.ToArray(), held.CopyFrame().Lights.Records.ToArray());
        Assert.Equal("held", held.CopyFrame().Lights.ShadowSlots.Owner(slot: 0));
        Assert.Equal((0, 0, 0, 0), Step());
    }
}
