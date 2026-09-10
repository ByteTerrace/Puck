using Puck.Shaders.Study;

namespace Puck.Shaders.Tests;

public sealed class StudyShaderCompilerTests {
    private static string MothShadertoyPath =>
        RepositoryPaths.Resolve(relativePath: "docs/art/moth-concept-pack-2026-09-09/moth-shadertoy.glsl");

    [Fact]
    public void Moth_shadertoy_reference_compiles_to_both_bytecodes_with_no_errors() {
        var compiler = new StudyShaderCompiler(cacheDirectory: FreshCacheDirectory());
        StudyProgram program;

        try {
            program = compiler.Compile(name: "moth", sourcePath: MothShadertoyPath, sourceText: File.ReadAllText(path: MothShadertoyPath));
        } catch (StudyToolMissingException exception) {
            Assert.Skip($"study toolchain unavailable: {exception.Message}");
            return;
        }

        Assert.DoesNotContain(collection: program.Diagnostics, filter: static d => d.IsError);
        Assert.False(condition: program.IsError);
        Assert.NotEmpty(collection: program.Spirv.ToArray());
        Assert.NotEmpty(collection: program.Dxil.ToArray());
    }
    [Fact]
    public void A_reference_to_an_undeclared_symbol_reports_one_error_on_its_own_source_line() {
        const string source = """
            void mainImage(out vec4 fragColor, in vec2 fragCoord) {
                vec2 uv = fragCoord / iResolution.xy;
                float a = 1.0;
                float b = 2.0;
                float c = 3.0;
                float d = 4.0;
                fragColor = vec4(uv, undefined_symbol, 1.0);
            }
            """;
        var compiler = new StudyShaderCompiler(cacheDirectory: FreshCacheDirectory());
        StudyProgram program;

        try {
            program = compiler.Compile(name: "broken", sourcePath: "broken.glsl", sourceText: source);
        } catch (StudyToolMissingException exception) {
            Assert.Skip($"study toolchain unavailable: {exception.Message}");
            return;
        }

        var errors = program.Diagnostics.Where(predicate: static d => d.IsError).ToArray();

        Assert.True(condition: program.IsError);
        Assert.Empty(collection: program.Spirv.ToArray());
        Assert.Empty(collection: program.Dxil.ToArray());
        Assert.Single(collection: errors);
        Assert.Equal(expected: 7, actual: errors[0].Line);
    }
    [Fact]
    public void An_iChannel_reference_is_refused_by_name_without_invoking_any_tool() {
        const string source = """
            void mainImage(out vec4 fragColor, in vec2 fragCoord) {
                fragColor = texture(iChannel0, fragCoord);
            }
            """;
        var runner = new CountingProcessRunner();
        var compiler = new StudyShaderCompiler(cacheDirectory: FreshCacheDirectory(), processRunner: runner, toolchainDirectory: null);
        var program = compiler.Compile(name: "channel", sourcePath: "channel.glsl", sourceText: source);

        Assert.True(condition: program.IsError);
        Assert.Equal(expected: 0, actual: runner.CallCount);
        Assert.Single(collection: program.Diagnostics);
        Assert.Equal(expected: 2, actual: program.Diagnostics[0].Line);
        Assert.Contains(expectedSubstring: "iChannel0", actualString: program.Diagnostics[0].Message);
    }
    [Fact]
    public void A_second_compile_of_identical_source_is_served_from_cache_without_invoking_any_tool() {
        const string source = "void mainImage(out vec4 fragColor, in vec2 fragCoord) { fragColor = vec4(1.0); }";
        var runner = new CountingProcessRunner();
        var compiler = new StudyShaderCompiler(cacheDirectory: FreshCacheDirectory(), processRunner: runner, toolchainDirectory: null);
        var first = compiler.Compile(name: "cached", sourcePath: "cached.glsl", sourceText: source);

        Assert.False(condition: first.IsError);
        Assert.Equal(expected: 3, actual: runner.CallCount);

        var second = compiler.Compile(name: "cached", sourcePath: "cached.glsl", sourceText: source);

        Assert.False(condition: second.IsError);
        Assert.Equal(expected: 3, actual: runner.CallCount);
        Assert.Equal(expected: first.SourceHash, actual: second.SourceHash);
        Assert.Equal(expected: first.Spirv.ToArray(), actual: second.Spirv.ToArray());
        Assert.Equal(expected: first.Dxil.ToArray(), actual: second.Dxil.ToArray());
    }

    private static string FreshCacheDirectory() =>
        Path.Combine(path1: Path.GetTempPath(), path2: $"puck-study-tests-{Guid.NewGuid():N}");

    // Stands in for glslang/spirv-cross/dxc without touching a real toolchain: writes a placeholder byte at
    // whichever output path the call named (-o, --output, or -Fo) and reports success, so a caching test can prove
    // "zero further invocations" without depending on the Vulkan SDK being installed.
    private sealed class CountingProcessRunner : IStudyProcessRunner {
        public int CallCount { get; private set; }

        public StudyProcessResult Run(string fileName, IReadOnlyList<string> arguments) {
            CallCount++;

            for (var index = 0; (index < (arguments.Count - 1)); index++) {
                if (arguments[index] is "-o" or "--output" or "-Fo") {
                    File.WriteAllBytes(bytes: [1, 2, 3, 4], path: arguments[index + 1]);
                }
            }

            return new StudyProcessResult(ExitCode: 0, Stdout: string.Empty, Stderr: string.Empty);
        }
    }
}
