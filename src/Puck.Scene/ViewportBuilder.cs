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

            cameras[index] = viewport.Camera!.Build();
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
