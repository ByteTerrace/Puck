using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The serialized home for every test class that swaps the process-global <see cref="Console.Error"/> or
/// <see cref="Console.Out"/> writer to capture narration. The swap is process-wide while xUnit's default
/// parallelism is per-class, so a concurrently running test that narrates to stderr (server fault narration
/// such as <c>WorldOutputHub.Detach</c>) can capture the swapped-in writer and write to it after the swapping
/// test restores and disposes it. <c>DisableParallelization</c> runs this collection's classes one at a time,
/// after every parallel collection has finished, so nothing else narrates while the writer is swapped. Every
/// test class that calls <see cref="Console.SetError"/> or <see cref="Console.SetOut"/> joins this collection.
/// </summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class ConsoleRedirectionCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(ConsoleRedirectionCollection.Name)]</c>.</summary>
    public const string Name = "console-redirection";
}
