using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void EachViewsDispatchOrdersItsOwnFourByteResetBeforeItsAtomicCompletionCount() {
        var fragment = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, 4096);
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(item => item.Id == RenderGraphPackageCatalog.SdfWorld) with {
            Fragment = fragment with { Resources = [.. fragment.Resources.Where(resource => !fragment.InputVersions.Contains(resource.Name))] },
            Inputs = [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeReadWrite, strideBytes: 4, count: null)],
        };
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog([package])).Compile(new RenderGraphDefinition(
            Name: "receivers", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.Color],
            Resources: [new(Name: SdfWorldPackage.Color, Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative()),
                new(Name: SdfWorldPackage.IndirectCache, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 4096, StrideBytes: 4,
                    Initialization: ShaderPipelineInitialization.External)],
            Packages: [new(Name: Sdf, Package: package.Id, Inputs: [SdfWorldPackage.IndirectCache], Outputs: [SdfWorldPackage.Color])])).Pipeline;
        var reset = Assert.Single(plan.Passes, pass => pass.Package!.Part == SdfWorldPackage.IndirectReceiverReset);
        var views = Assert.Single(plan.Passes, pass => pass.Package!.Part == SdfWorldPackage.Parts.Views);
        var counter = Assert.Single(plan.Storages, storage => storage.Name == $"{Sdf}${SdfWorldPackage.IndirectDeferredClear}");
        var clear = Assert.Single(reset.Accesses, access => access.Use.Writes);
        var read = Assert.Single(views.Accesses, access => access.Version == $"{Sdf}${SdfWorldPackage.IndirectDeferredClear}");
        var count = Assert.Single(views.Accesses, access => access.Version == $"{Sdf}${SdfWorldPackage.IndirectDeferred}");

        Assert.Equal(4UL, counter.Declaration.ResolveSizeBytes(new ShaderPipelineStorageCounts(Height: 17, Width: 23)));
        Assert.True(counter.Declaration.Retained);
        Assert.Equal(counter.Index, clear.Storage);
        Assert.Equal(counter.Index, read.Storage);
        Assert.Equal(counter.Index, count.Storage);
        Assert.True(reset.Index < views.Index);
        Assert.Equal(GpuAccess.TransferWrite, clear.Use.Access);
        Assert.Equal(GpuStage.Transfer, clear.Use.Stage);
        Assert.Equal(reset.Index, read.PriorPass);
        Assert.Equal(GpuAccess.ShaderRead, read.Use.Access);
        Assert.Equal(GpuStage.ComputeShader, read.Use.Stage);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, read.Barrier.Kind);
        Assert.Equal(GpuAccess.TransferWrite, read.Barrier.SourceAccess);
        Assert.Equal(GpuStage.Transfer, read.Barrier.SourceStage);
        Assert.Equal(GpuAccess.ShaderRead, read.Barrier.DestinationAccess);
        Assert.Equal(GpuStage.ComputeShader, read.Barrier.DestinationStage);
        // Views reads its cleared input before the preserving output reaches the same retained allocation.
        Assert.Equal(views.Index, count.PriorPass);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, count.Barrier.Kind);
        Assert.Equal(GpuAccess.ShaderRead, count.Barrier.SourceAccess);
        Assert.Equal(GpuStage.ComputeShader, count.Barrier.SourceStage);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, count.Barrier.DestinationAccess);
        Assert.Equal(GpuStage.ComputeShader, count.Barrier.DestinationStage);
        Assert.False(reset.Package!.CountsKernelWork);
    }
}
