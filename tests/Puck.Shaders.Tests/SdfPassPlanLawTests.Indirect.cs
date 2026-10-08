using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void TheIndirectProducerPublishesOneForwardedBufferWithPlacementPartitionTraceAndShadeBarriers() {
        const ulong Bytes = 4096;
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: item => (item.Id == RenderGraphPackageCatalog.Indirect));
        var fragment = SdfWorldPackage.IndirectFragment(bytes: Bytes);
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog(packages: [package with { Fragment = fragment }])).Compile(definition: new RenderGraphDefinition(
            Name: "cache", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.IndirectCache],
            Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.IndirectCache, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: Bytes, StrideBytes: 4)],
            Packages: [new RenderGraphPackagePass(Name: "indirect", Package: RenderGraphPackageCatalog.Indirect, Outputs: [SdfWorldPackage.IndirectCache])]
        ));

        Assert.Equal(new[] { "indirect$place", "indirect$classify", "indirect$trace", "indirect$shade" }, plan.Pipeline.PassOrder);
        var storage = Assert.Single(collection: plan.Pipeline.Storages);

        Assert.Equal(Bytes, storage.Declaration.ResolveSizeBytes(counts: new ShaderPipelineStorageCounts(Height: 1, Width: 1)));
        foreach (var pass in plan.Pipeline.Passes.Skip(count: 1)) {
            var access = Assert.Single(collection: pass.Accesses);

            Assert.Equal(ShaderPipelineBarrierKind.Buffer, access.Barrier.Kind);
            Assert.True(condition: ((access.Barrier.SourceAccess & GpuAccess.ShaderWrite) != 0));
            Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, access.Barrier.DestinationAccess);
            Assert.Equal(GpuStage.ComputeShader, access.Barrier.DestinationStage);
        }
        Assert.All(plan.Pipeline.Passes, pass => Assert.True(condition: pass.Package!.CountsKernelWork));
    }
    [Fact]
    public void TheReceiverProvesAgainstTheCacheAndViewsReadsItsCertificateAndAnswers() {
        var fragment = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, 4096);

        Assert.Equal([SdfWorldPackage.IndirectCache], fragment.InputVersions);
        var external = Assert.Single(collection: fragment.Resources, predicate: resource => (resource.Name == SdfWorldPackage.IndirectCache));

        Assert.True(condition: external.IsExternal);
        Assert.Equal(ShaderPipelineResourceKind.Buffer, external.Kind);
        Assert.Equal([SdfWorldPackage.Parts.Receiver, SdfWorldPackage.Parts.Views],
            fragment.Passes.Where(predicate: pass => pass.Inputs.Any(predicate: input => (input.Name == SdfWorldPackage.IndirectCache))).Select(selector: pass => pass.Name));
        var certificate = Assert.Single(collection: fragment.Resources, predicate: resource => (resource.Name == SdfWorldPackage.IndirectVisibility));

        Assert.Equal(SdfWorldPackage.Parts.ShadowVisibility, certificate.From);
        Assert.True(condition: certificate.PreservesPredecessor);
        Assert.Equal(96u, certificate.StrideBytes);
        var answers = Assert.Single(collection: fragment.Resources, predicate: resource => (resource.Name == SdfWorldPackage.IndirectAnswer));

        // One four-word answer per pixel of the visibility records' extent.
        Assert.Equal(SdfWorldPackage.IndirectAnswerByteLength, answers.StrideBytes);
        Assert.Equal(certificate.Count, answers.Count);
        Assert.Null(@object: answers.From);
        var receiver = Assert.Single(collection: fragment.Passes, predicate: pass => (pass.Name == SdfWorldPackage.Parts.Receiver));
        var views = Assert.Single(collection: fragment.Passes, predicate: pass => (pass.Name == SdfWorldPackage.Parts.Views));
        var cache = receiver.Inputs.ToList().FindIndex(match: input => (input.Name == SdfWorldPackage.IndirectCache));

        Assert.Equal(RenderGraphPortAccess.ComputeReadWrite, receiver.InputAccesses[cache]);
        Assert.Contains(collection: receiver.Outputs, filter: output => (output.Name == SdfWorldPackage.IndirectVisibility));
        Assert.Contains(collection: receiver.Outputs, filter: output => (output.Name == SdfWorldPackage.IndirectAnswer));
        Assert.Contains(collection: views.Inputs, filter: input => (input.Name == SdfWorldPackage.IndirectVisibility));
        Assert.Contains(collection: views.Inputs, filter: input => (input.Name == SdfWorldPackage.IndirectAnswer));
        Assert.DoesNotContain(collection: views.Inputs, filter: input => (input.Name == SdfWorldPackage.Parts.ShadowVisibility));
        Assert.DoesNotContain(collection: views.Outputs, filter: output => (output.Name == SdfWorldPackage.IndirectVisibility));
        Assert.All(views.InputAccesses, access => Assert.Equal(RenderGraphPortAccess.ComputeRead, access));
        Assert.True(condition: (fragment.Passes.ToList().IndexOf(item: receiver) < fragment.Passes.ToList().IndexOf(item: views)));
    }
    [InlineData("native", false)]
    [InlineData("native", true)]
    [InlineData("reduced", false)]
    [InlineData("reduced", true)]
    [InlineData("temporal", false)]
    [InlineData("temporal", true)]
    [Theory]
    public void EveryViewPlansOneWritableVisibilityAllocationBeforeItsLaterReaders(string quality, bool indirect) {
        var fragment = quality switch {
            "native" => SdfWorldPackage.NativeFragment,
            "reduced" => SdfWorldPackage.Fragment,
            _ => SdfWorldPackage.TemporalFragment,
        };

        if (indirect) { fragment = SdfWorldPackage.WithIndirect(bytes: 4096, fragment: fragment); }
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: item => (item.Id == RenderGraphPackageCatalog.SdfWorld)) with {
            // The instance graph owns external inputs; fragment expansion contributes private and output versions.
            Fragment = fragment with {
                Resources = [.. fragment.Resources.Where(predicate: resource => !fragment.InputVersions.Contains(value: resource.Name))],
            },
            Inputs = (indirect ? [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeReadWrite, strideBytes: 4, count: null)] : []),
        };
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog(packages: [package])).Compile(definition: new RenderGraphDefinition(
            Name: "visibility", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.Color],
            Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.Color, Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative()),
                .. (indirect ? new[] { new ShaderPipelineResource(Name: SdfWorldPackage.IndirectCache, Kind: ShaderPipelineResourceKind.Buffer,
                    SizeBytes: 4096, StrideBytes: 4, Initialization: ShaderPipelineInitialization.External) } : [])],
            Packages: [new RenderGraphPackagePass(Name: Sdf, Package: package.Id,
                Inputs: (indirect ? [SdfWorldPackage.IndirectCache] : []), Outputs: [SdfWorldPackage.Color])])).Pipeline;
        // The record's last writer is the receiver, which publishes the certificate, or else the shadow stage.
        var writer = Assert.Single(collection: plan.Passes, predicate: pass => (pass.Package!.Part == (indirect ? SdfWorldPackage.Parts.Receiver : SdfWorldPackage.Parts.Shadow)));
        var views = Assert.Single(collection: plan.Passes, predicate: pass => (pass.Package!.Part == SdfWorldPackage.Parts.Views));
        var visibility = Assert.Single(collection: plan.Storages, predicate: storage => (storage.Name == $"{Sdf}${SdfWorldPackage.Parts.Visibility}"));
        var write = Assert.Single(collection: writer.Accesses, predicate: access => ((access.Storage == visibility.Index) && access.Use.Writes));

        Assert.True(condition: visibility.Declaration.Retained);
        Assert.Equal(96u, visibility.Declaration.StrideBytes);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, write.Use.Access);
        Assert.Equal(GpuStage.ComputeShader, write.Use.Stage);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, write.Barrier.Kind);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, write.Barrier.DestinationAccess);
        // Views only reads the records it shades, after their last writer.
        var shaded = Assert.Single(collection: views.Accesses, predicate: access => (access.Storage == visibility.Index));

        Assert.False(condition: shaded.Use.Writes);
        Assert.Equal(GpuAccess.ShaderRead, shaded.Use.Access);
        Assert.Equal(writer.Index, shaded.PriorPass);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, shaded.Barrier.SourceAccess);
        Assert.Equal(GpuAccess.ShaderRead, shaded.Barrier.DestinationAccess);
        var later = Assert.Single(collection: plan.Passes, predicate: pass => (pass.Package!.Part == ((quality == "native") ? SdfWorldPackage.Parts.Composite : SdfWorldPackage.Resolve)));
        var read = Assert.Single(collection: later.Accesses, predicate: access => (access.Storage == visibility.Index));

        Assert.True(condition: (later.Index > views.Index));
        Assert.False(condition: read.Use.Writes);
        Assert.Equal(fragment.Passes.Count, plan.Passes.Count);
    }
}
