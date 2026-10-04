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
        Assert.False(RenderGraphPackagePort.Image(RenderGraphPortAccess.TransferWrite).IsValid);
        var written = Assert.Single(plan.Passes[1].Accesses, access => access.Version == "snapshot");
        Assert.Equal(GpuStage.Transfer, written.Use.Stage);
        Assert.Equal(GpuAccess.TransferWrite, written.Use.Access);
        var sampled = Assert.Single(plan.Passes[2].Accesses, access => access.Version == "snapshot");
        Assert.Equal(GpuStage.Transfer, sampled.Barrier.SourceStage);
        Assert.Equal(GpuAccess.TransferWrite, sampled.Barrier.SourceAccess);
        Assert.Equal(GpuStage.ComputeShader, sampled.Barrier.DestinationStage);
        Assert.Equal(GpuAccess.ShaderRead, sampled.Barrier.DestinationAccess);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ATransferPortCannotCopyHistory(bool previous, bool history) {
        var refused = Assert.Throws<ShaderPipelineCompilationException>(() => Plan(previous, history));
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_TRANSFER_INPUT");
    }

    [Fact]
    public void ATransferDestinationCannotBeHistory() {
        var refused = Assert.Throws<ShaderPipelineCompilationException>(() => Plan(false, false, outputHistory: true));
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_TRANSFER_OUTPUT");
    }

    private static ShaderPipelinePlan Plan(bool previous, bool history, bool outputHistory = false) {
        var catalog = new RenderGraphPackageCatalog([
            new(Id: "write", Inputs: [], Outputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeWrite, null, null)], Members: [], Summary: "Writes the source."),
            new(Id: "copy", Inputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.TransferRead, null, null)],
                Outputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.TransferWrite, null, null)], Members: [], Summary: "Copies the source into an immutable snapshot."),
            new(Id: "read", Inputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeRead, null, null)],
                Outputs: [RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Samples the copied snapshot."),
        ]);
        return new RenderGraphCompiler(catalog).Compile(new RenderGraphDefinition(Name: "transfer", Schema: RenderGraphSchemas.Graph,
            Resources: [new(Name: "source", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 256, History: history),
                new(Name: "snapshot", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 256, History: outputHistory),
                new(Name: "result", Format: nameof(GpuPixelFormat.R8G8B8A8Unorm), Dimensions: ShaderPipelineDimensions.Relative())],
            Outputs: ["result"], Packages: [new(Name: "write", Package: "write", Inputs: [], Outputs: ["source"]),
                new(Name: "copy", Package: "copy", Inputs: [new("source", PreviousFrame: previous)], Outputs: ["snapshot"]),
                new(Name: "read", Package: "read", Inputs: ["snapshot"], Outputs: ["result"])])).Pipeline;
    }
}
