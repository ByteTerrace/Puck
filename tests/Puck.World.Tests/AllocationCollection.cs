using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The serialized home for full scene capacity probes and laws that bound the calling thread's allocation.
/// A probe materializes the entire reserved scene, so only one CPU law may own that temporary program at a time.
/// The allocation count is per thread, but a
/// collection running beside it moves the garbage collector, and each collection retires this thread's allocation
/// context, so a bound measured under parallel classes is not the bound the code pays alone. Runs after every
/// parallel collection has finished, one class at a time.
/// </summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class AllocationCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(AllocationCollection.Name)]</c>.</summary>
    public const string Name = "allocation";
}
