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
            _ = builder.AddMaterial(new SdfMaterial(color));
            _ = builder.AddMaterial(new SdfMaterial(Vector3.UnitY));
            builder.BeginInstance(Vector3.Zero, 1f).ResetPoint().Sphere(.5f, binding).EndInstance();
            return builder.Build();
        }
        var source = Frame() with { Program = Program(Vector3.One), FarDistance = 1f, IndirectTier = SdfIndirectTier.Medium };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(() => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-materials", pipelines: SdfTestPipelines.Cache(), width: Extent);
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
            // This law checks the existing host admission/commit contract; no fake shader result is asserted.
            cache.Submitted();
            cache.SubmittedLighting();
            return admitted;
        }
        void Complete(bool transportStands) => TestLiveness.Within(frames: 64, step: () => {
            var admitted = Step();
            if (transportStands) { Assert.Equal((0, 0, 0), (admitted.Places, admitted.Partitions, admitted.Traces)); }
            return cache.IsComplete && cache.LightingComplete;
        }, building: () => false, reason: () => "The fixed host solve has not completed.");
        Complete(transportStands: false);
        var published = cache.PublishedLightingSource;
        Assert.NotNull(published);
        var epoch = cache.Epoch;
        var certificate = cache.CertificateRevision;
        var geometry = tables.LightGeometry;
        var primary = tables.PassSignature(source, 0, SdfWorldPackage.Parts.Primary);
        var shadow = tables.PassSignature(source, 0, SdfWorldPackage.Parts.Shadow);
        var views = tables.PassSignature(source, 0, SdfWorldPackage.Parts.Views);

        source = source with { Program = Program(Vector3.UnitX), ProgramChanged = true };
        var recolor = Step();
        Assert.Equal((0, 0, 0), (recolor.Places, recolor.Partitions, recolor.Traces));
        Assert.True(recolor.Shades > 0);
        Assert.Equal(epoch, cache.Epoch);
        Assert.Equal(certificate, cache.CertificateRevision);
        Assert.Equal(geometry, tables.LightGeometry);
        Assert.Equal(primary, tables.PassSignature(source, 0, SdfWorldPackage.Parts.Primary));
        Assert.Equal(shadow, tables.PassSignature(source, 0, SdfWorldPackage.Parts.Shadow));
        Assert.NotEqual(views, tables.PassSignature(source, 0, SdfWorldPackage.Parts.Views));
        Assert.Equal(Vector3.One, published.Program.Materials[0].Albedo);
        Complete(transportStands: true);
        Assert.Equal(Vector3.UnitX, cache.PublishedLightingSource!.Program.Materials[0].Albedo);
        Assert.NotSame(published, cache.PublishedLightingSource);
        var standing = Step();
        Assert.Equal((0, 0, 0, 0), standing);

        source = source with { Program = Program(Vector3.UnitX, binding: 1) };
        var reassigned = Step();
        Assert.NotEqual(epoch, cache.Epoch);
        Assert.True(cache.CertificateRevision > certificate);
        Assert.NotEqual(geometry, tables.LightGeometry);
        Assert.True(reassigned.Places > 0);
        Assert.Null(cache.PublishedLightingSource);
    }
}
