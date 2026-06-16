using Puck.Demo.Cameras;
using Puck.SdfVm;

namespace Puck.Demo.Viewports;

/// <summary>
/// A first-class viewport: an identified pairing of a <see cref="ICamera"/> with the <see cref="SdfProgram"/>
/// it looks at. This is the unit the engine treats as data — swap the camera to change the vantage (a
/// Lakitu/lure follow cam), swap the program to change the world, and (in a later phase) give several
/// viewports screen regions and a transition to get split-screen, or sample one viewport's rendered
/// texture onto a SCREEN_SLAB in another to get a jumbotron.
/// </summary>
/// <remarks>Phase 1 drives exactly one viewport, rendered fullscreen straight to the swapchain; the
/// region/render-target machinery arrives with the compositor phase.</remarks>
internal sealed class Viewport(ViewportId id, ICamera camera, SdfProgram program) {
    public ViewportId Id { get; } = id;

    /// <summary>The vantage this viewport renders from.</summary>
    public ICamera Camera { get; set; } = camera;

    /// <summary>The SDF program this viewport renders.</summary>
    public SdfProgram Program { get; set; } = program;
}
