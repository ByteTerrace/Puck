using Puck.SdfVm;

namespace Puck.World;

/// <summary>
/// A mutable singleton holder for the live <see cref="SdfEngineNode"/>, so the <c>world.gpu</c> verb can read the
/// previous frame's per-pass GPU times without the command module depending on the render composition. Program's
/// <see cref="Puck.Hosting.IRenderNode"/> factory stores the built producer here; <see cref="Node"/> is
/// <see langword="null"/> until the renderer is built on the first frame.
/// </summary>
internal sealed class WorldRenderProbe {
    /// <summary>The SDF engine node the render root wraps, or <see langword="null"/> until the render factory has run.</summary>
    public SdfEngineNode? Node { get; set; }
}
