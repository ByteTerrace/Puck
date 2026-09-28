using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The serialized home for every device law that turns the Direct3D 12 debug layer on. Turning it on removes every
/// device the process already holds, so a law that takes <c>DirectXTestDevices.Debug</c> runs after every parallel
/// collection has finished, one class at a time.
/// </summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class DebugLayerCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(DebugLayerCollection.Name)]</c>.</summary>
    public const string Name = "debug-layer";
}
