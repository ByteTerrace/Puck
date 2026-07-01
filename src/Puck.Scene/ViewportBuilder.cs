using Puck.Cameras;
using Puck.Compositing;

namespace Puck.Scene;

/// <summary>
/// Turns the validated <see cref="Viewport"/> list into the parallel <see cref="ICamera"/> + <see cref="NormalizedRect"/>
/// arrays an <c>ISdfFrameSource</c> drives. Each camera DTO becomes its concrete engine camera (degrees converted to
/// radians by the DTO), and each <c>[x, y, w, h]</c> region becomes a <see cref="NormalizedRect"/>.
/// </summary>
public static class ViewportBuilder {
    /// <summary>Builds the cameras and regions for a viewport list.</summary>
    /// <param name="viewports">The validated viewport section.</param>
    /// <returns>The parallel camera and region arrays, one entry per viewport.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewports"/> is <see langword="null"/>.</exception>
    public static (ICamera[] Cameras, NormalizedRect[] Regions) Build(IReadOnlyList<Viewport> viewports) {
        ArgumentNullException.ThrowIfNull(argument: viewports);

        var cameras = new ICamera[viewports.Count];
        var regions = new NormalizedRect[viewports.Count];

        for (var index = 0; (index < viewports.Count); index++) {
            var viewport = viewports[index];
            var region = viewport.Region;

            // M0: every source is a virtual camera (the only registered ViewportSource kinds are orbit/perspective, so
            // the validator has already guaranteed this). A live capture source will grow a parallel slot->IRenderNode
            // output here rather than an ICamera.
            if (viewport.Source is not CameraDocument camera) {
                throw new NotSupportedException(message: $"viewport[{index}] source '{viewport.Source?.GetType().Name ?? "null"}' is not a camera; ViewportBuilder does not yet build non-camera sources");
            }

            cameras[index] = camera.Build();
            regions[index] = new NormalizedRect(
                Height: region[3],
                Width: region[2],
                X: region[0],
                Y: region[1]
            );
        }

        return (cameras, regions);
    }
}
