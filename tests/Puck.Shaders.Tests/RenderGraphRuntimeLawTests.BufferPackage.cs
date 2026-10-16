using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    // An engine package whose producer supplies its own fragment, standing for any package that publishes one buffer
    // over four chained passes, and the view fragment that reads that buffer: the runtime's borrowed, named and mutable
    // buffer edges are exercised through them.
    private const string BufferPackage = RenderGraphPackageCatalog.SkyEnvironment;
    // The producer's published buffer version.
    private const string CacheVersion = "cache";
    // The producer's first pass.
    private const string PlacePass = "place";

    private static RenderGraphPackageFragment ProducerFragment(ulong bytes) => new(
        InputVersions: [], OutputVersions: [CacheVersion],
        Passes: [
            BufferPass(name: PlacePass, output: "placed"),
            BufferPass(name: "classify", output: "partitioned"),
            BufferPass(name: "trace", output: "traced"),
            BufferPass(name: "shade", output: CacheVersion),
        ],
        Resources: [
            new(Name: "placed", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: bytes, StrideBytes: sizeof(uint)),
            new(Name: "partitioned", Kind: ShaderPipelineResourceKind.Buffer, From: "placed", SizeBytes: bytes, StrideBytes: sizeof(uint)),
            new(Name: "traced", Kind: ShaderPipelineResourceKind.Buffer, From: "partitioned", SizeBytes: bytes, StrideBytes: sizeof(uint)),
            new(Name: CacheVersion, Kind: ShaderPipelineResourceKind.Buffer, From: "traced", SizeBytes: bytes, StrideBytes: sizeof(uint)),
        ]);
    // The view fragment with the producer's buffer as an external input its views pass reads.
    private static RenderGraphPackageFragment ReaderFragment(RenderGraphPackageFragment fragment, ulong bytes) => fragment with {
        InputVersions = [.. fragment.InputVersions, CacheVersion],
        Resources = [.. fragment.Resources, new ShaderPipelineResource(Name: CacheVersion, Kind: ShaderPipelineResourceKind.Buffer,
            SizeBytes: bytes, StrideBytes: sizeof(uint), Initialization: ShaderPipelineInitialization.External)],
        Passes = [.. fragment.Passes.Select(selector: static pass => ((pass.Name == SdfWorldPackage.Parts.Views) ? pass with {
            Inputs = [.. pass.Inputs, new ResourceReference(Name: CacheVersion)],
            InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
        } : pass))],
    };
    private static RenderGraphFragmentPass BufferPass(string name, string output) => new(
        CountsKernelWork: true,
        InputAccesses: [],
        Inputs: [],
        Name: name,
        OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
        Outputs: [new ResourceReference(Name: output)]
    );
}
