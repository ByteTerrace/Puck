using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed class RenderGraphTransferBufferLawTests {
    [Fact]
    public void ADeclaredBufferCopyOrdersTheActualTransferAfterItsComputeWriter() {
        var plan = Plan(previous: false, history: false);
        var read = Assert.Single(plan.Passes[1].Accesses, access => access.Version == "source");
        Assert.Equal(GpuStage.Transfer, read.Use.Stage);
        Assert.Equal(GpuAccess.TransferRead, read.Use.Access);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, read.Barrier.Kind);
        Assert.Equal(GpuStage.ComputeShader, read.Barrier.SourceStage);
        Assert.Equal(GpuAccess.ShaderWrite, read.Barrier.SourceAccess);
        Assert.Equal(GpuStage.Transfer, read.Barrier.DestinationStage);
        Assert.Equal(GpuAccess.TransferRead, read.Barrier.DestinationAccess);
        Assert.False(RenderGraphPackagePort.Image(RenderGraphPortAccess.TransferRead).IsValid);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ATransferPortCannotCopyHistory(bool previous, bool history) {
        var refused = Assert.Throws<ShaderPipelineCompilationException>(() => Plan(previous, history));
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_TRANSFER_INPUT");
    }

    private static ShaderPipelinePlan Plan(bool previous, bool history) {
        var catalog = new RenderGraphPackageCatalog([
            new(Id: "write", Inputs: [], Outputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeWrite, null, null)], Members: [], Summary: "Writes the source."),
            new(Id: "copy", Inputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.TransferRead, null, null)],
                Outputs: [RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Copies the source before its compute output."),
        ]);
        return new RenderGraphCompiler(catalog).Compile(new RenderGraphDefinition(Name: "transfer", Schema: RenderGraphSchemas.Graph,
            Resources: [new(Name: "source", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 256, History: history),
                new(Name: "result", Format: nameof(GpuPixelFormat.R8G8B8A8Unorm), Dimensions: ShaderPipelineDimensions.Relative())],
            Outputs: ["result"], Packages: [new(Name: "write", Package: "write", Inputs: [], Outputs: ["source"]),
                new(Name: "copy", Package: "copy", Inputs: [new("source", PreviousFrame: previous)], Outputs: ["result"])])).Pipeline;
    }
}
