using System.Runtime.CompilerServices;

namespace Puck.Cli.Tests;

/// <summary>
/// Raises the thread pool's minimum before any test runs. This suite runs in parallel, and many of its tests block
/// a pool thread on a child process (the real <c>Puck.World</c> executable, <c>dotnet build</c>) for seconds at a
/// time. Above the minimum the pool injects threads slowly, so without this floor the in-process MCP servers and
/// HTTP clients beside them wait on starved continuations and miss their own deadlines.
/// </summary>
internal static class ThreadPoolFloor {
    [ModuleInitializer]
    internal static void Raise() {
        ThreadPool.GetMinThreads(
            completionPortThreads: out var completionPortThreads,
            workerThreads: out var workerThreads
        );

        var floor = (Environment.ProcessorCount * 8);

        _ = ThreadPool.SetMinThreads(
            completionPortThreads: Math.Max(
                val1: completionPortThreads,
                val2: floor
            ),
            workerThreads: Math.Max(
                val1: workerThreads,
                val2: floor
            )
        );
    }
}
