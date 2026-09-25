using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>The pipeline documents the <c>pipeline-feedback</c> and <c>pipeline-edit</c> canaries boot must plan, and
/// compile for both backends — or, for the broken edit, fail in its middle pass — wherever DXC is on the search path,
/// before a GPU run can say anything about them.</summary>
public sealed class PipelineCanaryFixtureTests {
    private static string FixturePath(string fileName, string canary = "pipeline-feedback") => RepositoryPaths.Resolve(relativePath: $"tests/Puck.World.Canaries/{canary}/{fileName}");
    private static ShaderPipelineLoadResult LoadEdit(string fileName, string cache) => new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache)).Load(
        cancellationToken: TestContext.Current.CancellationToken,
        name: "feedback",
        path: FixturePath(
            canary: "pipeline-edit",
            fileName: fileName
        )
    );

    [InlineData("feedback.pipeline.json", "history")]
    [InlineData("wrong-history.pipeline.json", "stale")]
    [Theory]
    public void Both_feedback_documents_plan_three_passes_whose_accumulator_reads_the_named_history(string fileName, string history) {
        var plan = new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: "feedback",
            path: FixturePath(fileName: fileName)
        ));

        Assert.Equal(
            expected: ["accumulate", "convert", "copy"],
            actual: plan.Passes.Select(selector: static pass => pass.Name)
        );
        var input = Assert.Single(collection: plan.Passes[0].Declaration.InputReferences);

        Assert.Equal(
            expected: history,
            actual: input.Name
        );
        Assert.True(condition: input.PreviousFrame);
        Assert.Equal(
            expected: "image",
            actual: plan.DefaultOutput
        );
        Assert.All(
            collection: plan.Resources.Where(predicate: static resource => (resource.Declaration.Kind == ShaderPipelineResourceKind.Image)),
            action: static resource => Assert.Equal(
                expected: (32u, 32u),
                actual: resource.Declaration.Dimensions!.Resolve(
                    frameHeight: 900,
                    frameWidth: 1600
                )
            )
        );
    }
    [InlineData("feedback.pipeline.json")]
    [InlineData("wrong-history.pipeline.json")]
    [Theory]
    public void Both_feedback_documents_compile_for_both_backends(string fileName) {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: "dxc") is null),
            reason: "DXC is required to compile the canary pipeline sources."
        );
        var cache = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-pipeline-canary-" + Guid.NewGuid().ToString(format: "N"))
        );

        try {
            var result = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache)).Load(
                cancellationToken: TestContext.Current.CancellationToken,
                name: "feedback",
                path: FixturePath(fileName: fileName)
            );

            Assert.True(
                condition: (result.Status == ShaderPipelineLoadStatus.Compiled),
                userMessage: result.Message
            );
            foreach (var shader in result.Pipeline!.Shaders.Values) {
                Assert.NotEmpty(collection: shader.SpirvByStage);
                Assert.NotEmpty(collection: shader.DxilByStage);
            }
        } finally {
            if (Directory.Exists(path: cache)) {
                Directory.Delete(
                    path: cache,
                    recursive: true
                );
            }
        }
    }
    [InlineData("broken.pipeline.json", "convert-broken.hlsl")]
    [InlineData("corrected.pipeline.json", "convert-inverted.hlsl")]
    [Theory]
    public void Both_edits_plan_the_feedback_graph_with_only_the_middle_pass_changed(string fileName, string convertSource) {
        var original = new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: "feedback",
            path: FixturePath(fileName: "feedback.pipeline.json")
        ));
        var edited = new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: "feedback",
            path: FixturePath(
                canary: "pipeline-edit",
                fileName: fileName
            )
        ));

        Assert.Equal(
            expected: ["accumulate", "convert", "copy"],
            actual: edited.Passes.Select(selector: static pass => pass.Name)
        );
        Assert.Equal(
            expected: original.Resources.Select(selector: static resource => resource.Declaration),
            actual: edited.Resources.Select(selector: static resource => resource.Declaration)
        );
        Assert.Equal(
            expected: convertSource,
            actual: edited.Passes[1].Declaration.Source
        );
        Assert.Equal(
            expected: ["../pipeline-feedback/accumulate.hlsl", "../pipeline-feedback/copy.hlsl"],
            actual: [edited.Passes[0].Declaration.Source, edited.Passes[2].Declaration.Source]
        );
    }
    [InlineData("pipeline-supersede", "halved.pipeline.json")]
    [InlineData("pipeline-shapes", "shapes.pipeline.json")]
    [InlineData("pipeline-shapes", "misaddressed.pipeline.json")]
    [InlineData("pipeline-resize", "resize.pipeline.json")]
    [InlineData("pipeline-resize", "fixed-history.pipeline.json")]
    [InlineData("pipeline-counters", "four-pass.pipeline.json")]
    [InlineData("source-conversion", "conversion.pipeline.json")]
    [InlineData("source-conversion", "discriminating.pipeline.json")]
    [Theory]
    public void The_slice_three_documents_compile_for_both_backends(string canary, string fileName) {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: "dxc") is null),
            reason: "DXC is required to compile the canary pipeline sources."
        );
        var cache = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-pipeline-canary-" + Guid.NewGuid().ToString(format: "N"))
        );

        try {
            var result = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache)).Load(
                cancellationToken: TestContext.Current.CancellationToken,
                name: "fixture",
                path: FixturePath(
                    canary: canary,
                    fileName: fileName
                )
            );

            Assert.True(
                condition: (result.Status == ShaderPipelineLoadStatus.Compiled),
                userMessage: result.Message
            );
            foreach (var shader in result.Pipeline!.Shaders.Values) {
                Assert.NotEmpty(collection: shader.SpirvByStage);
                Assert.NotEmpty(collection: shader.DxilByStage);
            }
        } finally {
            if (Directory.Exists(path: cache)) {
                Directory.Delete(
                    path: cache,
                    recursive: true
                );
            }
        }
    }
    [Fact]
    public void The_shapes_document_plans_compute_compute_fullscreen_over_sparse_bindings_a_raw_buffer_and_the_position_adapter() {
        var plan = new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: "shapes",
            path: FixturePath(
                canary: "pipeline-shapes",
                fileName: "shapes.pipeline.json"
            )
        ));

        Assert.Equal(
            expected: [("seed", ShaderPipelineDocumentPassKind.Compute), ("combine", ShaderPipelineDocumentPassKind.Compute), ("present", ShaderPipelineDocumentPassKind.Fullscreen)],
            actual: plan.Passes.Select(selector: static pass => (pass.Name, pass.Declaration.Kind))
        );
        Assert.Equal(
            expected: [4u, 9u, 2u, 7u, 5u],
            actual: [.. plan.Passes[0].Declaration.OutputReferences.Select(selector: static output => output.Binding!.Value), .. plan.Passes[1].Declaration.InputReferences.Select(selector: static input => input.Binding!.Value), .. plan.Passes[1].Declaration.OutputReferences.Select(selector: static output => output.Binding!.Value)]
        );
        Assert.Equal(
            expected: ShaderPipelineVertexInput.Position,
            actual: plan.Passes[2].Declaration.Vertex
        );
        Assert.Equal(
            expected: ShaderPipelineResourceKind.Buffer,
            actual: plan.Resources.Single(predicate: static resource => (resource.Name == "words")).Declaration.Kind
        );
        Assert.Equal(
            expected: ["image", "mixed", "field"],
            actual: plan.Outputs
        );
    }
    [Fact]
    public void The_corrected_edit_compiles_and_the_broken_edit_fails_in_its_middle_pass() {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: "dxc") is null),
            reason: "DXC is required to compile the canary pipeline sources."
        );
        var cache = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-pipeline-canary-" + Guid.NewGuid().ToString(format: "N"))
        );

        try {
            var corrected = LoadEdit(
                cache: cache,
                fileName: "corrected.pipeline.json"
            );
            var broken = LoadEdit(
                cache: cache,
                fileName: "broken.pipeline.json"
            );

            Assert.True(
                condition: (corrected.Status == ShaderPipelineLoadStatus.Compiled),
                userMessage: corrected.Message
            );
            Assert.Equal(
                expected: ShaderPipelineLoadStatus.Failed,
                actual: broken.Status
            );
            Assert.Null(@object: broken.Pipeline);
            Assert.Contains(
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "convert",
                actualString: broken.Message
            );
        } finally {
            if (Directory.Exists(path: cache)) {
                Directory.Delete(
                    path: cache,
                    recursive: true
                );
            }
        }
    }
}
