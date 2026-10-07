using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void PaletteValuesRestartOnlyLightingWhileMaterialBindingsWithdrawTransport() {
        static SdfProgram Program(Vector3 color, int binding = 0) {
            var builder = new SdfProgramBuilder();

            _ = builder.AddMaterial(material: new SdfMaterial(color));
            _ = builder.AddMaterial(material: new SdfMaterial(Vector3.UnitY));
            builder.BeginInstance(Vector3.Zero, 1f).ResetPoint().Sphere(.5f, binding).EndInstance();
            return builder.Build();
        }
        var source = Frame() with { Program = Program(Vector3.One), FarDistance = 1f, IndirectTier = SdfIndirectTier.Medium };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-materials", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(gpu: new FakeGpuDevice());

        residency.ProduceFirstFrame(context: context);
        var tables = residency.Tables!;
        var cache = tables.Indirect!;

        (int Places, int Partitions, int Traces, int Shades) Step() {
            residency.BeginFrame();
            Assert.True(condition: residency.Prepare(context: context));
            _ = residency.Submit(context: context);
            var admitted = (cache.PlaceCount, cache.ClassifyCount, cache.TraceCount, cache.ShadeCount);
            // This law checks the existing host admission/commit contract; no fake shader result is asserted.
            cache.Submitted();
            cache.SubmittedLighting();
            return admitted;
        }
        void Complete(bool transportStands) => TestLiveness.Within(frames: IndirectCompletionFrames(cache: cache, source: source), step: () => {
            var admitted = Step();

            if (transportStands) { Assert.Equal(actual: (admitted.Places, admitted.Partitions, admitted.Traces), expected: (0, 0, 0)); }
            return (cache.IsComplete && cache.LightingComplete);
        }, building: () => false, reason: () => "The fixed host solve has not completed.");
        Complete(transportStands: false);
        var published = cache.PublishedLightingSource;

        Assert.NotNull(@object: published);
        var epoch = cache.Epoch;
        var certificate = cache.CertificateRevision;
        var geometry = tables.LightGeometry;
        var primary = tables.PassSignature(frame: source, part: SdfWorldPackage.Parts.Primary, view: 0);
        var shadow = tables.PassSignature(frame: source, part: SdfWorldPackage.Parts.Shadow, view: 0);
        var views = tables.PassSignature(frame: source, part: SdfWorldPackage.Parts.Views, view: 0);

        source = source with { Program = Program(Vector3.UnitX), ProgramChanged = true };
        var recolor = Step();

        Assert.Equal(actual: (recolor.Places, recolor.Partitions, recolor.Traces), expected: (0, 0, 0));
        Assert.True(condition: (recolor.Shades > 0));
        Assert.Equal(epoch, cache.Epoch);
        Assert.Equal(certificate, cache.CertificateRevision);
        Assert.Equal(geometry, tables.LightGeometry);
        Assert.Equal(primary, tables.PassSignature(frame: source, part: SdfWorldPackage.Parts.Primary, view: 0));
        Assert.Equal(shadow, tables.PassSignature(frame: source, part: SdfWorldPackage.Parts.Shadow, view: 0));
        Assert.NotEqual(views, tables.PassSignature(frame: source, part: SdfWorldPackage.Parts.Views, view: 0));
        Assert.Equal(Vector3.One, published.Program.Materials[0].Albedo);
        Complete(transportStands: true);
        Assert.Equal(Vector3.UnitX, cache.PublishedLightingSource!.Program.Materials[0].Albedo);
        Assert.NotSame(published, cache.PublishedLightingSource);
        var standing = Step();

        Assert.Equal(actual: standing, expected: (0, 0, 0, 0));

        source = source with { Program = Program(Vector3.UnitX, binding: 1) };
        var reassigned = Step();

        Assert.NotEqual(epoch, cache.Epoch);
        Assert.True(condition: (cache.CertificateRevision > certificate));
        Assert.NotEqual(geometry, tables.LightGeometry);
        Assert.True(condition: (reassigned.Places > 0));
        Assert.Null(@object: cache.PublishedLightingSource);
    }
}
