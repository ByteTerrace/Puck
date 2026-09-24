using Puck.Abstractions.Counting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for the compiler's <c>shaders.compiler</c> counts over a fake tool runner: each request counts once, a warm
/// cache answer counts one hit and runs no tool, each tool run the runner receives counts once under its own tool, the
/// counts never go down across compilers sharing a directory, and each kind carries the class its definition gives.
/// </summary>
public sealed partial class ShaderCompilerTests {
    private static long Count(ShaderCompiler compiler, WorkKind kind) {
        Assert.True(condition: compiler.Work.TryRead(kind: kind, value: out var value));

        return value;
    }

    [Fact]
    public async Task Each_request_hit_and_tool_run_is_counted_once_under_its_own_kind() {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var compiler = new ShaderCompiler(
            fixture.Path,
            runner
        );
        var hlsl = new ShaderCompilationRequest(
            name: "counted",
            stages: [new ShaderStageSource(
                ShaderStage.Compute,
                Path.Combine(
                    path1: fixture.Path,
                    path2: "counted.hlsl"
                ),
                "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }"
            )]
        );
        var graphics = new ShaderCompilationRequest(
            name: "counted-graphics",
            stages: [
                new ShaderStageSource(
                    ShaderStage.Vertex,
                    Path.Combine(
                        path1: fixture.Path,
                        path2: "counted.vert.hlsl"
                    ),
                    "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }"
                ),
                new ShaderStageSource(
                    ShaderStage.Fragment,
                    Path.Combine(
                        path1: fixture.Path,
                        path2: "counted.frag.hlsl"
                    ),
                    "float4 main() : SV_Target { return 1; }"
                ),
            ]
        );

        Assert.Equal(expected: "shaders.compiler", actual: compiler.Work.Name);
        Assert.All(
            action: kind => Assert.Equal(expected: 0L, actual: Count(compiler: compiler, kind: kind)),
            collection: ShaderCompiler.WorkKinds.ToArray()
        );

        Assert.True(condition: (await compiler.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, descriptor: hlsl)).IsSuccess);
        Assert.Equal(expected: 1L, actual: Count(compiler: compiler, kind: ShaderCompiler.Requests));
        Assert.Equal(expected: 0L, actual: Count(compiler: compiler, kind: ShaderCompiler.CacheHits));
        Assert.Equal(expected: 2L, actual: Count(compiler: compiler, kind: ShaderCompiler.DxcRuns));
        Assert.Equal(expected: 2, actual: runner.Calls.Count);

        Assert.True(condition: (await compiler.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, descriptor: hlsl)).IsSuccess);
        Assert.Equal(expected: 2L, actual: Count(compiler: compiler, kind: ShaderCompiler.Requests));
        Assert.Equal(expected: 1L, actual: Count(compiler: compiler, kind: ShaderCompiler.CacheHits));
        Assert.Equal(expected: 2L, actual: Count(compiler: compiler, kind: ShaderCompiler.DxcRuns));
        Assert.Equal(expected: 2, actual: runner.Calls.Count);

        Assert.True(condition: (await compiler.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, descriptor: graphics)).IsSuccess);
        Assert.Equal(expected: 3L, actual: Count(compiler: compiler, kind: ShaderCompiler.Requests));
        Assert.Equal(expected: 1L, actual: Count(compiler: compiler, kind: ShaderCompiler.CacheHits));
        Assert.Equal(expected: 6L, actual: Count(compiler: compiler, kind: ShaderCompiler.DxcRuns));
        // Every run the runner received is counted, and nothing it did not receive is.
        Assert.Equal(
            expected: runner.Calls.Count,
            actual: Count(compiler: compiler, kind: ShaderCompiler.DxcRuns)
        );
    }
    [Fact]
    public async Task A_refused_closure_is_a_request_that_runs_no_tool() {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var compiler = new ShaderCompiler(
            fixture.Path,
            runner
        );
        var result = await compiler.CompileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            descriptor: new ShaderCompilationRequest(
                name: "refused",
                stages: [new ShaderStageSource(
                    ShaderStage.Compute,
                    Path.Combine(
                        path1: fixture.Path,
                        path2: "refused.hlsl"
                    ),
                    "#include \"missing.hlsl\"\n[numthreads(8,8,1)] void main() { }"
                )]
            )
        );

        Assert.False(condition: result.IsSuccess);
        Assert.Equal(expected: 1L, actual: Count(compiler: compiler, kind: ShaderCompiler.Requests));
        Assert.Equal(expected: 0L, actual: Count(compiler: compiler, kind: ShaderCompiler.DxcRuns));
        Assert.Empty(collection: runner.Calls);
    }
    [Fact]
    public async Task A_second_compiler_over_a_warm_directory_counts_its_own_hit_and_no_run() {
        using var fixture = new Fixture();
        var request = new ShaderCompilationRequest(
            name: "warm",
            stages: [new ShaderStageSource(
                ShaderStage.Compute,
                Path.Combine(
                    path1: fixture.Path,
                    path2: "warm.hlsl"
                ),
                "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }"
            )]
        );
        var cold = new ShaderCompiler(
            fixture.Path,
            new FakeRunner()
        );
        var warm = new ShaderCompiler(
            fixture.Path,
            new FakeRunner()
        );

        _ = await cold.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, descriptor: request);
        _ = await warm.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, descriptor: request);

        // The counts belong to each compiler; the directory is what the hit depends on.
        Assert.Equal(expected: 2L, actual: Count(compiler: cold, kind: ShaderCompiler.DxcRuns));
        Assert.Equal(expected: 0L, actual: Count(compiler: cold, kind: ShaderCompiler.CacheHits));
        Assert.Equal(expected: 0L, actual: Count(compiler: warm, kind: ShaderCompiler.DxcRuns));
        Assert.Equal(expected: 1L, actual: Count(compiler: warm, kind: ShaderCompiler.CacheHits));
    }
    [Fact]
    public void The_compiler_kinds_are_classified_by_what_they_depend_on() {
        Assert.Equal(
            expected: ["shaders.compiler.requests", "shaders.compiler.cache-hits", "shaders.compiler.runs.dxc"],
            actual: ShaderCompiler.WorkKinds.ToArray().Select(selector: static kind => kind.Name)
        );
        Assert.Equal(expected: WorkClass.Pacing, actual: ShaderCompiler.CacheHits.Class);
        Assert.All(
            action: static kind => Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: kind.Class),
            collection: [ShaderCompiler.Requests, ShaderCompiler.DxcRuns]
        );
    }
}
