using Puck.Abstractions.Gpu;
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

    /// <summary>The assembled render host, or <see langword="null"/> until the render factory has run — the
    /// <c>world.screenshot</c> verb arms captures through its <see cref="SdfWorldRender.RequestCapture"/> (which
    /// routes to the OUTERMOST decorator, so the readback lands on the final composed frame).</summary>
    public SdfWorldRender? Render { get; set; }

    /// <summary>The unified overlay decorator's pass-timing source, or <see langword="null"/> when the overlay was
    /// not composed — <c>world.gpu</c> appends its previous drawn frame's overlay-pass milliseconds (the UIE-9
    /// instrument) beside the engine's per-pass digest.</summary>
    public IPassTimingSource? Overlay { get; set; }
}
