namespace Puck.Shaders.Tests;

/// <summary>
/// The cache keys an output by what the tool reads, never by where it lies: the same stage source and includes, laid
/// out alike under two different directories, compile once, and an edit to one file compiles again.
/// </summary>
public sealed partial class ShaderCompilerTests {
    [Fact]
    public async Task A_closure_compiled_under_one_directory_is_a_cache_hit_under_any_other() {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var compiler = new ShaderCompiler(
            Path.Combine(
                path1: fixture.Path,
                path2: "cache"
            ),
            runner
        );

        ShaderCompilationRequest RequestUnder(string root) {
            var source = Path.Combine(path1: root, path2: "src", path3: "a.hlsl");
            const string Text = "#include \"../inc/shared.hlsli\"\n[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }";

            Directory.CreateDirectory(path: Path.Combine(path1: root, path2: "inc"));
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: source)!);
            if (!File.Exists(path: Path.Combine(path1: root, path2: "inc", path3: "shared.hlsli"))) {
                File.WriteAllText(contents: "#define VALUE 1", path: Path.Combine(path1: root, path2: "inc", path3: "shared.hlsli"));
            }

            return new ShaderCompilationRequest(
                name: "moved",
                stages: [new ShaderStageSource(ShaderStage.Compute, source, Text)]
            );
        }

        var one = Path.Combine(path1: fixture.Path, path2: "one");
        var two = Path.Combine(path1: fixture.Path, path2: "elsewhere", path3: "deeper", path4: "two");

        Assert.True(condition: (await compiler.CompileAsync(RequestUnder(root: one), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(expected: 2, actual: runner.Calls.Count);
        Assert.True(condition: (await compiler.CompileAsync(RequestUnder(root: two), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(expected: 2, actual: runner.Calls.Count);

        File.WriteAllText(contents: "#define VALUE 2", path: Path.Combine(path1: two, path2: "inc", path3: "shared.hlsli"));
        Assert.True(condition: (await compiler.CompileAsync(RequestUnder(root: two), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(expected: 4, actual: runner.Calls.Count);
    }
}
