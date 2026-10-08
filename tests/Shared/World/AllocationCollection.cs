using Xunit;

namespace Puck.World.Testing;

/// <summary>
/// The serialized home for laws that bound the calling thread's allocation over a full host or scene, and for laws
/// that change process-wide state. The allocation count is per thread, but a collection running beside it moves the
/// garbage collector, and each collection retires this thread's allocation context, so a bound measured under parallel
/// classes is not the bound the code pays alone. Runs after every parallel collection has finished, one class at a
/// time. A scene probe that measures no allocation belongs in <c>SceneProbeCollection</c>, which runs beside the
/// parallel collections.
/// </summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class AllocationCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(AllocationCollection.Name)]</c>.</summary>
    public const string Name = "allocation";
}
