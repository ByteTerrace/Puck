using Xunit;

namespace Puck.World.Testing;

/// <summary>
/// The home of the CPU laws that own a full host or a scene probe and measure no allocation. A probe materializes the
/// entire reserved scene, whose instruction streams are large even when the live world is empty, so two probes at once
/// multiply that temporary storage. The collection runs one class at a time, beside the suite's other parallel
/// collections, so at most one probe holds its scene while the rest of the suite runs. A probe law that bounds the
/// calling thread's allocation belongs in <see cref="AllocationCollection"/>, which runs alone.
/// </summary>
[CollectionDefinition(name: Name)]
public sealed class SceneProbeCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(SceneProbeCollection.Name)]</c>.</summary>
    public const string Name = "scene-probe";
}
