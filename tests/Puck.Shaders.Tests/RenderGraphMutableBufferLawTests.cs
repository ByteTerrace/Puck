using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed class RenderGraphMutableBufferLawTests {
    private static ShaderPipelineResource Cache(ShaderPipelineInitialization initialization = ShaderPipelineInitialization.External) =>
        new(Name: "cache", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 1024, Initialization: initialization);

    private static ShaderPipelineResource Image(string name) => new(Name: name,
        Dimensions: ShaderPipelineDimensions.Relative(), Format: nameof(GpuPixelFormat.R8G8B8A8Unorm));

    private static RenderGraphPackage Package(RenderGraphPortAccess access = RenderGraphPortAccess.ComputeReadWrite) => new(
        Id: "cache.update", Inputs: [RenderGraphPackagePort.Buffer(access, null, null)],
        Outputs: [RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Updates shared cache proofs.");

    private static RenderGraphDefinition Graph(ShaderPipelineResource cache) => new(Name: "cache-reader",
        Resources: [cache, Image("first"), Image("second")], Outputs: ["second"], Schema: RenderGraphSchemas.Graph,
        Packages: [new(Name: "update", Package: "cache.update", Inputs: ["cache"], Outputs: ["first"]),
            new(Name: "read", Package: "cache.read", Inputs: ["cache", "first"], Outputs: ["second"])]);

    private static RenderGraphPackageCatalog Catalog(RenderGraphPackage? update = null) => new(packages: [update ?? Package(),
        new(Id: "cache.read", Inputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeRead, null, null),
            RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeRead)],
            Outputs: [RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Reads completed proofs.")]);

    [Fact]
    public void AMutableImportPreservesContentsAndOrdersItsWriteBeforeTheFollowingRead() {
        var plan = new RenderGraphCompiler(Catalog()).Compile(Graph(Cache())).Pipeline;
        var cache = plan.FindResource("cache")!;
        var update = Assert.Single(plan.Passes[0].Accesses, access => access.Version == "cache");
        var read = Assert.Single(plan.Passes[1].Accesses, access => access.Version == "cache");

        Assert.Equal(ShaderPipelineContents.External, cache.Contents);
        Assert.Equal(-1, cache.WriterPassIndex);
        Assert.Equal(ShaderPipelinePriorKind.Host, update.PriorKind);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, update.Use.Access);
        Assert.Equal(GpuStage.ComputeShader, update.Use.Stage);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, update.Barrier.Kind);
        Assert.True(update.Barrier.SourceAccess.HasFlag(GpuAccess.TransferWrite));
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, read.Barrier.Kind);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, read.Barrier.SourceAccess);
        Assert.Equal(GpuAccess.ShaderRead, read.Barrier.DestinationAccess);
        Assert.Equal(GpuStage.ComputeShader, read.Barrier.SourceStage);
        Assert.Equal(GpuStage.ComputeShader, read.Barrier.DestinationStage);
        Assert.Equal(ShaderPipelineClear.None, plan.Storages[cache.Storage].Clear);
    }

    [Theory]
    [InlineData(ShaderPipelineInitialization.Zero)]
    [InlineData(ShaderPipelineInitialization.Host)]
    public void AMutableInputCannotReplaceAnOwnedVersionOrWriteAHostUpload(ShaderPipelineInitialization initialization) {
        var refused = Assert.Throws<ShaderPipelineCompilationException>(() =>
            new RenderGraphCompiler(Catalog()).Compile(Graph(Cache(initialization))));
        Assert.Contains(refused.Diagnostics, diagnostic =>
            ((diagnostic.Code == "SHADERPIPE_MUTABLE_INPUT") && (diagnostic.Name == "cache")));
    }

    [Fact]
    public void AFragmentCannotTurnAReadOnlyInputIntoAMutableOne() {
        var package = Package(RenderGraphPortAccess.ComputeRead) with {
            Fragment = new(Resources: [Image("result")], InputVersions: ["borrowed"], OutputVersions: ["result"],
                Passes: [new(Name: "update", Inputs: ["borrowed"], InputAccesses: [RenderGraphPortAccess.ComputeReadWrite],
                    Outputs: ["result"], OutputAccesses: [RenderGraphPortAccess.ComputeWrite])]),
        };
        var refused = Assert.Throws<ShaderPipelineCompilationException>(() =>
            new RenderGraphCompiler(Catalog(package)).Compile(Graph(Cache())));
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "RENDERGRAPH_MUTABLE_INPUT");
        Assert.False(RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeReadWrite).IsValid);
    }
}
