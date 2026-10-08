using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void EachReceiverDispatchOrdersItsOwnResetBeforeItsAtomicCountsAndViewsReadsThem() {
        var fragment = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, 4096);
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: item => (item.Id == RenderGraphPackageCatalog.SdfWorld)) with {
            Fragment = fragment with { Resources = [.. fragment.Resources.Where(predicate: resource => !fragment.InputVersions.Contains(value: resource.Name))] },
            Inputs = [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeReadWrite, strideBytes: 4, count: null)],
        };
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog(packages: [package])).Compile(definition: new RenderGraphDefinition(
            Name: "receivers", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.Color],
            Resources: [new(Name: SdfWorldPackage.Color, Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative()),
                new(Name: SdfWorldPackage.IndirectCache, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 4096, StrideBytes: 4,
                    Initialization: ShaderPipelineInitialization.External)],
            Packages: [new(Name: Sdf, Package: package.Id, Inputs: [SdfWorldPackage.IndirectCache], Outputs: [SdfWorldPackage.Color])])).Pipeline;
        var reset = Assert.Single(collection: plan.Passes, predicate: pass => (pass.Package!.Part == SdfWorldPackage.IndirectReceiverReset));
        var receiver = Assert.Single(collection: plan.Passes, predicate: pass => (pass.Package!.Part == SdfWorldPackage.Parts.Receiver));
        var views = Assert.Single(collection: plan.Passes, predicate: pass => (pass.Package!.Part == SdfWorldPackage.Parts.Views));
        var counter = Assert.Single(collection: plan.Storages, predicate: storage => (storage.Name == $"{Sdf}${SdfWorldPackage.IndirectDeferredClear}"));
        var clear = Assert.Single(collection: reset.Accesses, predicate: access => access.Use.Writes);
        var count = Assert.Single(collection: receiver.Accesses, predicate: access => (access.Storage == counter.Index));
        var copied = Assert.Single(collection: views.Accesses, predicate: access => (access.Storage == counter.Index));

        Assert.Equal(8UL, counter.Declaration.ResolveSizeBytes(counts: new ShaderPipelineStorageCounts(Height: 17, Width: 23)));
        Assert.True(condition: counter.Declaration.Retained);
        Assert.Equal(counter.Index, clear.Storage);
        Assert.Equal(counter.Index, count.Storage);
        Assert.Contains(collection: receiver.Inputs, filter: reference => (reference.Name == $"{Sdf}${SdfWorldPackage.IndirectDeferredClear}"));
        Assert.Equal($"{Sdf}${SdfWorldPackage.IndirectDeferred}", count.Version);
        Assert.True(condition: ((reset.Index < receiver.Index) && (receiver.Index < views.Index)));
        Assert.Equal(GpuAccess.TransferWrite, clear.Use.Access);
        Assert.Equal(GpuStage.Transfer, clear.Use.Stage);
        Assert.Equal(reset.Index, count.PriorPass);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, count.Use.Access);
        Assert.Equal(GpuStage.ComputeShader, count.Use.Stage);
        // The clear orders both atomic counters through one combined shader access.
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, count.Barrier.Kind);
        Assert.Equal(GpuAccess.TransferWrite, count.Barrier.SourceAccess);
        Assert.Equal(GpuStage.Transfer, count.Barrier.SourceStage);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, count.Barrier.DestinationAccess);
        Assert.Equal(GpuStage.ComputeShader, count.Barrier.DestinationStage);
        // Views reads the counts its readback copies after the receiver's atomic writes.
        Assert.Equal($"{Sdf}${SdfWorldPackage.IndirectDeferred}", copied.Version);
        Assert.Equal(receiver.Index, copied.PriorPass);
        Assert.False(condition: copied.Use.Writes);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, copied.Barrier.SourceAccess);
        Assert.False(condition: reset.Package!.CountsKernelWork);
    }
}
