using Puck.Abstractions.Gpu;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    /// <summary>Finds a graph view's completed work source without waiting for its pending submission.</summary>
    /// <param name="instance">The rendered view instance.</param>
    /// <returns>The instance's work source, or null before it is attached or after it is removed.</returns>
    public IGpuWorkSource? WorkOf(string instance) => m_runtime?.NodeOf(instance: instance);
}
