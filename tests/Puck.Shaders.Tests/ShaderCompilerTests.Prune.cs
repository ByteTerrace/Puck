namespace Puck.Shaders.Tests;

/// <summary>
/// A cache file's last write time is when a compile last used it: a hit stamps the entries it reads, planning stamps the
/// duration record it reads and checking a plan stamps its entry, and a prune removes exactly the entries, duration records
/// and abandoned staged publications no compile used since its cutoff, so what it keeps still answers the next compile.
/// </summary>
public sealed partial class ShaderCompilerTests {
    private static readonly DateTime LongAgo = new(day: 1, hour: 0, kind: DateTimeKind.Utc, minute: 0, month: 1, second: 0, year: 2000);

    private static ShaderCompilationRequest ComputeRequest(string directory, string name, string body) {
        var path = Path.Combine(path1: directory, path2: $"{name}.hlsl");

        Directory.CreateDirectory(path: directory);
        File.WriteAllText(contents: body, path: path);

        return new ShaderCompilationRequest(
            name: name,
            stages: [new ShaderStageSource(ShaderStage.Compute, path, body)]
        );
    }
    private static void Age(string directory) {
        foreach (var file in Directory.EnumerateFiles(path: directory, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: LongAgo, path: file);
        }
    }
    private static bool IsStamped(string path) => (File.GetLastWriteTimeUtc(path: path) > LongAgo.AddDays(value: 1));

    [Fact]
    public async Task A_cache_hit_stamps_the_entries_it_reads() {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var cache = Path.Combine(path1: fixture.Path, path2: "cache");
        var compiler = new ShaderCompiler(cache, runner);
        var request = ComputeRequest(body: "[numthreads(8,8,1)] void main() { }", directory: Path.Combine(path1: fixture.Path, path2: "src"), name: "hit");

        Assert.True(condition: (await compiler.CompileAsync(request, TestContext.Current.CancellationToken)).IsSuccess);
        Age(directory: cache);
        Assert.True(condition: (await compiler.CompileAsync(request, TestContext.Current.CancellationToken)).IsSuccess);

        Assert.Equal(expected: 2, actual: runner.Calls.Count);
        Assert.Equal(expected: 2, actual: Entries(directory: cache).Length);
        Assert.All(collection: Entries(directory: cache), action: static entry => Assert.True(condition: IsStamped(path: entry), userMessage: $"{entry} was read by a cache hit but still carries its old last write time."));
    }
    [Fact]
    public async Task Planning_stamps_the_duration_record_and_checking_a_plan_stamps_its_entry() {
        using var fixture = new Fixture();
        var cache = Path.Combine(path1: fixture.Path, path2: "cache");
        var compiler = new ShaderCompiler(cache, new FakeRunner());
        var request = ComputeRequest(body: "[numthreads(8,8,1)] void main() { }", directory: Path.Combine(path1: fixture.Path, path2: "src"), name: "planned");
        var stage = request.Stages[0];
        var plan = compiler.Plan(stage: stage, target: ShaderTarget.Spirv);

        Assert.True(condition: (await compiler.CompileOutputAsync(plan, TestContext.Current.CancellationToken)).Compiled);
        Age(directory: cache);

        var replanned = compiler.Plan(stage: stage, target: ShaderTarget.Spirv);
        var durations = Directory.GetFiles(path: Path.Combine(path1: cache, path2: "durations"));

        Assert.NotNull(@object: replanned.RecordedDuration);
        Assert.Single(collection: durations);
        Assert.True(condition: IsStamped(path: durations[0]), userMessage: "Planning read the output's duration record but left its old last write time.");
        Assert.True(condition: compiler.IsCached(plan: replanned));
        Assert.True(condition: IsStamped(path: Assert.Single(collection: Entries(directory: cache))), userMessage: "Checking the plan found its entry but left its old last write time.");
    }
    [Fact]
    public async Task Prune_removes_what_no_compile_used_since_the_cutoff_and_what_it_keeps_still_answers() {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var cache = Path.Combine(path1: fixture.Path, path2: "cache");
        var compiler = new ShaderCompiler(cache, runner);
        var source = Path.Combine(path1: fixture.Path, path2: "src");
        var used = ComputeRequest(body: "[numthreads(8,8,1)] void main() { }", directory: source, name: "used");
        var unused = ComputeRequest(body: "[numthreads(4,4,1)] void main() { }", directory: source, name: "unused");

        Assert.True(condition: (await compiler.CompileAsync(used, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True(condition: (await compiler.CompileAsync(unused, TestContext.Current.CancellationToken)).IsSuccess);
        File.WriteAllText(contents: "abandoned", path: Path.Combine(path1: cache, path2: "0123.spv.abcdef.tmp"));
        File.WriteAllText(contents: "not the compiler's", path: Path.Combine(path1: cache, path2: "notes.txt"));
        Age(directory: cache);
        Assert.True(condition: (await compiler.CompileAsync(used, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(expected: 4, actual: runner.Calls.Count);

        var result = ShaderCompiler.Prune(cacheDirectory: cache, cutoffUtc: DateTime.UtcNow.AddMinutes(value: -1));

        // The unused stage's two entries, all four duration records (a hit reads none) and the abandoned staging file go;
        // the used stage's two entries, four bytes each from the fake compiler, stay.
        Assert.Equal(expected: 7, actual: result.Removed);
        Assert.Equal(expected: 2, actual: result.Kept);
        Assert.Equal(expected: 8, actual: result.KeptBytes);
        Assert.Equal(expected: 2, actual: Entries(directory: cache).Length);
        Assert.Empty(collection: Directory.GetFiles(path: cache, searchPattern: "*.tmp"));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: cache, path2: "notes.txt")), userMessage: "Prune removed a file the compiler never writes.");

        Assert.True(condition: (await compiler.CompileAsync(used, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(expected: 4, actual: runner.Calls.Count);
        Assert.True(condition: (await compiler.CompileAsync(unused, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(expected: 6, actual: runner.Calls.Count);
    }
}
