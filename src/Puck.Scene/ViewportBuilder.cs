using System.Numerics;
using Puck.Cameras;
using Puck.Compositing;

namespace Puck.Scene;

/// <summary>
/// Turns the validated <see cref="Viewport"/> list into the parallel <see cref="ICamera"/> + <see cref="NormalizedRect"/>
/// arrays an <c>ISdfFrameSource</c> drives. Each camera DTO becomes its concrete engine camera (degrees converted to
/// radians by the DTO), and each <c>[x, y, w, h]</c> region becomes a <see cref="NormalizedRect"/>. A child-content
/// slot (<see cref="LiveCameraSource"/>, <see cref="GamingBrickSource"/>) builds a placeholder camera here — the host
/// overrides that slot's rendered view with the child's produced surface — and is surfaced separately by
/// <see cref="ChildSources"/>.
/// </summary>
public static class ViewportBuilder {
    private static readonly IReadOnlyDictionary<int, ViewportSource> EmptyChildSources = new Dictionary<int, ViewportSource>();

    /// <summary>Builds the cameras and regions for a viewport list. A <see cref="LiveCameraSource"/> slot gets a
    /// placeholder camera (its rendered view is overridden by the imported camera surface at composite).</summary>
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

            cameras[index] = viewport.Source switch {
                CameraDocument camera => camera.Build(),
                // A child-content slot still renders an SDF view (a placeholder camera keeps the frame well-formed); the
                // host composites the child's produced surface over it — the camera's parameters are never seen.
                LiveCameraSource or GamingBrickSource => PlaceholderCamera(),
                _ => throw new NotSupportedException(message: $"viewport[{index}] source '{viewport.Source?.GetType().Name ?? "null"}' is not a buildable viewport source"),
            };
            regions[index] = new NormalizedRect(
                Height: region[3],
                Width: region[2],
                X: region[0],
                Y: region[1]
            );
        }

        return (cameras, regions);
    }

    /// <summary>Extracts the child-content slots (viewport index → its <see cref="LiveCameraSource"/> or
    /// <see cref="GamingBrickSource"/>) so the host can build a per-slot child node. Empty when every viewport is a
    /// virtual SDF camera.</summary>
    /// <param name="viewports">The validated viewport section.</param>
    /// <returns>A slot → source map (empty if none).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewports"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<int, ViewportSource> ChildSources(IReadOnlyList<Viewport> viewports) {
        ArgumentNullException.ThrowIfNull(argument: viewports);

        Dictionary<int, ViewportSource>? children = null;

        for (var index = 0; (index < viewports.Count); index++) {
            if (viewports[index].Source is { } source and (LiveCameraSource or GamingBrickSource)) {
                (children ??= []).Add(key: index, value: source);
            }
        }

        return (children ?? EmptyChildSources);
    }

    // A benign stand-in camera for a child-content slot; never visible (the child's produced surface overrides the slot).
    private static ICamera PlaceholderCamera() {
        return new OrbitCamera {
            FieldOfViewRadians = (MathF.PI / 3f),
            Height = 2f,
            Radius = 5f,
            Target = Vector3.Zero,
        };
    }
}
