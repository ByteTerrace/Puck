using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed class RenderGraphMutableBufferLawTests {
    private static ShaderPipelineResource Cache(ShaderPipelineInitialization initialization = ShaderPipelineInitialization.External) =>
        new(Name: "cache", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 1024, Initialization: initialization);
    private static ShaderPipelineResource Image(string name) => new(Name: name,
        Dimensions: ShaderPipelineDimensions.Relative(), Format: nameof(GpuPixelFormat.R8G8B8A8Unorm));
    private static RenderGraphPackage Package(RenderGraphPortAccess access = RenderGraphPortAccess.ComputeReadWrite) => new(
        Id: "cache.update", Inputs: [RenderGraphPackagePort.Buffer(access: access, count: null, strideBytes: null)],
        Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Updates shared cache proofs.");
    private static RenderGraphDefinition Graph(ShaderPipelineResource cache) => new(Name: "cache-reader",
        Resources: [cache, Image(name: "first"), Image(name: "second")], Outputs: ["second"], Schema: RenderGraphSchemas.Graph,
        Packages: [new(Name: "update", Package: "cache.update", Inputs: ["cache"], Outputs: ["first"]),
            new(Name: "read", Package: "cache.read", Inputs: ["cache", "first"], Outputs: ["second"])]);
    private static RenderGraphPackageCatalog Catalog(RenderGraphPackage? update = null) => new(packages: [(update ?? Package()),
        new(Id: "cache.read", Inputs: [RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ComputeRead, count: null, strideBytes: null),
            RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeRead)],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Reads completed proofs.")]);

    [Fact]
    public void AMutableImportPreservesContentsAndOrdersItsWriteBeforeTheFollowingRead() {
        var plan = new RenderGraphCompiler(Catalog()).Compile(definition: Graph(cache: Cache())).Pipeline;
        var cache = plan.FindResource(name: "cache")!;
        var update = Assert.Single(collection: plan.Passes[0].Accesses, predicate: access => (access.Version == "cache"));
        var read = Assert.Single(collection: plan.Passes[1].Accesses, predicate: access => (access.Version == "cache"));

        Assert.Equal(ShaderPipelineContents.External, cache.Contents);
        Assert.Equal(-1, cache.WriterPassIndex);
        Assert.Equal(ShaderPipelinePriorKind.Host, update.PriorKind);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, update.Use.Access);
        Assert.Equal(GpuStage.ComputeShader, update.Use.Stage);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, update.Barrier.Kind);
        Assert.True(condition: update.Barrier.SourceAccess.HasFlag(flag: GpuAccess.TransferWrite));
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, read.Barrier.Kind);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, read.Barrier.SourceAccess);
        Assert.Equal(GpuAccess.ShaderRead, read.Barrier.DestinationAccess);
        Assert.Equal(GpuStage.ComputeShader, read.Barrier.SourceStage);
        Assert.Equal(GpuStage.ComputeShader | GpuStage.FragmentShader, read.Barrier.DestinationStage);
        Assert.Equal(ShaderPipelineClear.None, plan.Storages[cache.Storage].Clear);
    }
    [InlineData(ShaderPipelineInitialization.Zero)]
    [InlineData(ShaderPipelineInitialization.Host)]
    [Theory]
    public void AMutableInputCannotReplaceAnOwnedVersionOrWriteAHostUpload(ShaderPipelineInitialization initialization) {
        var refused = Assert.Throws<ShaderPipelineCompilationException>(testCode: () =>
            new RenderGraphCompiler(Catalog()).Compile(definition: Graph(cache: Cache(initialization: initialization))));

        Assert.Contains(collection: refused.Diagnostics, filter: diagnostic =>
            ((diagnostic.Code == "SHADERPIPE_MUTABLE_INPUT") && (diagnostic.Name == "cache")));
    }
    [Fact]
    public void AFragmentCannotTurnAReadOnlyInputIntoAMutableOne() {
        var package = Package(access: RenderGraphPortAccess.ComputeRead) with {
            Fragment = new(Resources: [Image(name: "result")], InputVersions: ["borrowed"], OutputVersions: ["result"],
                Passes: [new(Name: "update", Inputs: ["borrowed"], InputAccesses: [RenderGraphPortAccess.ComputeReadWrite],
                    Outputs: ["result"], OutputAccesses: [RenderGraphPortAccess.ComputeWrite])]),
        };
        var refused = Assert.Throws<ShaderPipelineCompilationException>(testCode: () =>
            new RenderGraphCompiler(Catalog(update: package)).Compile(definition: Graph(cache: Cache())));

        Assert.Contains(collection: refused.Diagnostics, filter: diagnostic => (diagnostic.Code == "RENDERGRAPH_MUTABLE_INPUT"));
        Assert.False(condition: RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeReadWrite).IsValid);
    }

    // A fragment's own retained, zero-initialized buffer that one part updates in place: the stationary window's shape.
    private static ShaderPipelineResource Window() => new(Name: "window", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 1024, StrideBytes: sizeof(uint),
        Initialization: ShaderPipelineInitialization.Zero, Retained: true);
    private static RenderGraphFragmentPass Updater(string output = "result") => new(Name: "update", Inputs: ["window"],
        InputAccesses: [RenderGraphPortAccess.ComputeReadWrite], Outputs: [output], OutputAccesses: [RenderGraphPortAccess.ComputeWrite]);
    private static RenderGraphPlan CompileOwned(RenderGraphPackageFragment fragment, RenderGraphPackagePort[] outputs, string[] bound, ShaderPipelineResource[] resources) {
        var package = new RenderGraphPackage(Id: "window.update", Inputs: [], Outputs: outputs, Members: [], Summary: "Updates its own window.") { Fragment = fragment };
        var graph = new RenderGraphDefinition(Name: "window-owner", Resources: resources, Outputs: ["out"], Schema: RenderGraphSchemas.Graph,
            Packages: [new(Name: "owner", Package: "window.update", Inputs: [], Outputs: [.. bound.Select(selector: static name => new ResourceReference(Name: name))])]);

        return new RenderGraphCompiler(new RenderGraphPackageCatalog(packages: [package])).Compile(definition: graph);
    }

    [Fact]
    public void AFragmentsOwnBufferUpdatedByOnePartIsClearedOnceAndCarriesFromFrameToFrame() {
        var plan = CompileOwned(fragment: new(Resources: [Window(), Image(name: "result")], InputVersions: [], OutputVersions: ["result"], Passes: [Updater()]),
            outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)], bound: ["out"], resources: [Image(name: "out")]).Pipeline;
        var window = plan.Storages.Single(predicate: storage => storage.Versions.Any(predicate: version => version.EndsWith(value: "window", comparisonType: StringComparison.Ordinal)));

        Assert.Equal(ShaderPipelineClear.EveryInstance, window.Clear);
        Assert.True(condition: window.UpdatedInPlace);
        Assert.True(condition: window.PersistsContents);
    }
    // Planning follows versions, which an update in place has none of, so no other pass may reach the buffer: a reader
    // could be ordered before the update, keep a dropped updater's results, or stand on an earlier reading.
    [Fact]
    public void AnInPlaceBufferAnotherPartAlsoReadsIsRefused() {
        var reader = new RenderGraphFragmentPass(Name: "read", Inputs: ["window", "result"],
            InputAccesses: [RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead], Outputs: ["final"], OutputAccesses: [RenderGraphPortAccess.ComputeWrite]);
        var refused = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => CompileOwned(
            fragment: new(Resources: [Window(), Image(name: "result"), Image(name: "final")], InputVersions: [], OutputVersions: ["final"], Passes: [Updater(), reader]),
            outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)], bound: ["out"], resources: [Image(name: "out")]));

        Assert.Contains(collection: refused.Diagnostics, filter: diagnostic => (diagnostic.Code == "SHADERPIPE_MUTABLE_INPUT"));
    }
    // An output version is spliced onto the enclosing graph's storage, which enters only through a mutable input port.
    [Fact]
    public void AFragmentOutputCannotStandInForAMutableInputPort() {
        var refused = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => CompileOwned(
            fragment: new(Resources: [Window(), Image(name: "result")], InputVersions: [], OutputVersions: ["result", "window"], Passes: [Updater()]),
            outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite), RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ComputeWrite, count: null, strideBytes: sizeof(uint))],
            bound: ["out", "shared"],
            resources: [Image(name: "out"), new(Name: "shared", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 1024, StrideBytes: sizeof(uint), Initialization: ShaderPipelineInitialization.External)]));

        Assert.Contains(collection: refused.Diagnostics, filter: diagnostic => (diagnostic.Code == "RENDERGRAPH_MUTABLE_INPUT"));
    }
}
