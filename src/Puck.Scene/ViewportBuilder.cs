using System.Numerics;
using Puck.Cameras;
using Puck.Compositing;

namespace Puck.Scene;

/// <summary>
/// Turns the validated <see cref="Viewport"/> list into the parallel <see cref="ICamera"/> + <see cref="NormalizedRect"/>
/// arrays an <c>ISdfFrameSource</c> drives. Each camera DTO becomes its concrete engine camera (degrees converted to
/// radians by the DTO), and each <c>[x, y, w, h]</c> region becomes a <see cref="NormalizedRect"/>. A live-capture
/// (<see cref="LiveCameraSource"/>) slot builds a placeholder camera here — the host overrides that slot's rendered view
/// with the imported camera surface — and is surfaced separately by <see cref="LiveSources"/>.
/// </summary>
public static class ViewportBuilder {
    private static readonly IReadOnlyDictionary<int, LiveCameraSource> EmptyLiveSources = new Dictionary<int, LiveCameraSource>();

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
                // A live-capture slot still renders an SDF view (a placeholder camera keeps the frame well-formed); the
                // host composites the imported camera surface over it — the camera's parameters are never seen.
                LiveCameraSource => PlaceholderCamera(),
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

    /// <summary>Extracts the live-capture slots (viewport index → its <see cref="LiveCameraSource"/>) so the host can
    /// build a per-slot camera child node. Empty when no viewport uses a live camera.</summary>
    /// <param name="viewports">The validated viewport section.</param>
    /// <returns>A slot → source map (empty if none).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewports"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<int, LiveCameraSource> LiveSources(IReadOnlyList<Viewport> viewports) {
        ArgumentNullException.ThrowIfNull(argument: viewports);

        Dictionary<int, LiveCameraSource>? live = null;

        for (var index = 0; (index < viewports.Count); index++) {
            if (viewports[index].Source is LiveCameraSource source) {
                (live ??= []).Add(key: index, value: source);
            }
        }

        return (live ?? EmptyLiveSources);
    }

    // A benign stand-in camera for a live-capture slot; never visible (the imported camera surface overrides the slot).
    private static ICamera PlaceholderCamera() {
        return new OrbitCamera {
            FieldOfViewRadians = (MathF.PI / 3f),
            Height = 2f,
            Radius = 5f,
            Target = Vector3.Zero,
        };
    }
}
