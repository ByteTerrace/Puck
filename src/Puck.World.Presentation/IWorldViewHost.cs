using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Hosts the views a world renders beside its own: the camera views its screens film and the session views its
/// session screens show. The render root configures them once it knows the render envelope.</summary>
public interface IWorldViewHost {
    /// <summary>Configures the views the world renders beside its own — called once by the render factory after the frame
    /// source has probed the render envelope (the worst-case program, instance and transform capacities every view's
    /// residency must fit). Registers one camera view per camera a screen names and one session view per session screen,
    /// each an <c>sdf.world</c> instance the render graph runs.</summary>
    /// <param name="pipelines">The composition's pipeline catalog every view's residency leases its pipelines from.</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="programWordCapacity">The world's probed program-word floor.</param>
    /// <param name="instanceCapacity">The world's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The world's dynamic-transform slot count.</param>
    /// <param name="host">The world's frame source, whose glyph atlas, screen decals and moving screens a camera view
    /// shares.</param>
    /// <param name="displayWidth">The display's width, in pixels, which a view's declared extent is a fraction of.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    /// <param name="viewports">The seats' views for each frame just dressed, whose primary render camera a window session fits
    /// its eye to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/>, <paramref name="host"/> or <paramref name="viewports"/> is
    /// <see langword="null"/>.</exception>
    void ConfigureViews(SdfWorldPipelineCatalog pipelines, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity, ISdfFrameSource host, int displayWidth, int displayHeight, WorldSeatViewports viewports);
}
