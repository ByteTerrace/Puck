using Puck.Abstractions.Counting;

namespace Puck.Shaders;

public sealed partial class ShaderCompiler {
    /// <summary>The name a counters report heads a compiler's <see cref="Work"/> section with.</summary>
    public const string WorkSourceName = "shaders.compiler";

    /// <summary>Gets the kind counting compile requests: one per request the compiler starts on, whatever answers it
    /// — a refused closure, a cache hit, or a build.</summary>
    public static WorkKind Requests { get; } = new(name: "shaders.compiler.requests", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting the requests the compile cache answered with every stage's bytecode, running no
    /// tool. It depends on what the cache directory already holds, which outlives the process.</summary>
    public static WorkKind CacheHits { get; } = new(name: "shaders.compiler.cache-hits", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting <see cref="DxcTool"/> runs, one per compile step that ran it.</summary>
    public static WorkKind DxcRuns { get; } = new(name: "shaders.compiler.runs.dxc", unit: "count", workClass: WorkClass.PerBackendDeterministic);

    /// <summary>Gets the compiler's kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> WorkKinds =>
        KindOrder.Kinds;
    /// <summary>Gets this compiler's lifetime counts under <see cref="WorkSourceName"/>: <see cref="Requests"/>,
    /// <see cref="CacheHits"/>, and one count of native tool runs per tool. A tool run is counted where
    /// <see cref="StepsOf"/>'s steps run, once the process returns, whatever its exit code. With a cache directory that
    /// starts empty, the runs are the same on every run of one workload; <see cref="CacheHits"/> is not, because the
    /// directory outlives the process. Version probes (<see cref="ToolVersionAsync"/>) are not compile steps and are
    /// not counted. Compiles run on the thread pool, so every count is written interlocked.</summary>
    public WorkCounterSet Work { get; } = new(
        kinds: WorkKinds,
        name: WorkSourceName
    );

    private static WorkKind RunsOf(string tool) =>
        tool switch {
            DxcTool => DxcRuns,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: tool,
                message: $"'{tool}' is not a compile step's tool.",
                paramName: nameof(tool)
            ),
        };

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class KindOrder {
        internal static readonly WorkKind[] Kinds = [Requests, CacheHits, DxcRuns];
    }
}
