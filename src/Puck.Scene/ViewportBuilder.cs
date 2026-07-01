using Puck.Cameras;
using Puck.Compositing;

namespace Puck.Scene;

/// <summary>
/// Turns the validated <see cref="Viewport"/> list into the parallel <see cref="ICamera"/> + <see cref="NormalizedRect"/>
/// arrays an <c>ISdfFrameSource</c> drives. Each camera DTO becomes its concrete engine camera (degrees converted to
/// radians by the DTO), and each <c>[x, y, w, h]</c> region becomes a <see cref="NormalizedRect"/>.
/// </summary>
public static class ViewportBuilder {
    /// <summary>Builds the cameras, regions, and live-camera slots for a viewport list.</summary>
    /// <param name="viewports">The validated viewport section.</param>
    /// <returns>The parallel camera and region arrays (one entry per viewport), plus one <see cref="LiveCameraSlot"/>
    /// per viewport whose source is a <see cref="LiveCameraViewportSource"/> — the slots the demo hosts a live-camera
    /// producer node in (their <see cref="ICamera"/> entry is a placeholder whose SDF render is overwritten by that
    /// node's surface, exactly as the child-surface slot is today).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewports"/> is <see langword="null"/>.</exception>
    public static (ICamera[] Cameras, NormalizedRect[] Regions, LiveCameraSlot[] LiveCameraSlots) Build(IReadOnlyList<Viewport> viewports) {
        ArgumentNullException.ThrowIfNull(argument: viewports);

        var cameras = new ICamera[viewports.Count];
        var liveCameraSlots = new List<LiveCameraSlot>();
        var regions = new NormalizedRect[viewports.Count];

        for (var index = 0; (index < viewports.Count); index++) {
            var viewport = viewports[index];
            var region = viewport.Region;

            regions[index] = new NormalizedRect(
                Height: region[3],
                Width: region[2],
                X: region[0],
                Y: region[1]
            );

            // A virtual SDF camera builds its concrete engine camera. A live-camera source builds no camera — a
            // producer node fills its pane instead; it still gets a PLACEHOLDER camera so the per-view arrays stay
            // parallel (its SDF render is discarded, overwritten by the hosted node's surface, like the child slot).
            switch (viewport.Source) {
                case CameraDocument camera:
                    cameras[index] = camera.Build();

                    break;
                case LiveCameraViewportSource liveCamera:
                    cameras[index] = CreatePlaceholderCamera();
                    liveCameraSlots.Add(item: new LiveCameraSlot(PixelSize: liveCamera.PixelSize, Quantize: liveCamera.Quantize, Slot: index));

                    break;
                default:
                    throw new NotSupportedException(message: $"viewport[{index}] source '{viewport.Source?.GetType().Name ?? "null"}' is not a supported ViewportSource kind");
            }
        }

        return (cameras, regions, liveCameraSlots.ToArray());
    }

    // A benign default orbit for a live-camera slot: its rendered output is never shown (the hosted live-camera node's
    // surface replaces it), so the parameters only need to be valid, not meaningful.
    private static ICamera CreatePlaceholderCamera() {
        return new OrbitCamera {
            AngularSpeedRadiansPerSecond = 0f,
            AzimuthRadians = 0f,
            FieldOfViewRadians = (60f * (MathF.PI / 180f)),
            Height = 1.6f,
            Radius = 5f,
            Target = System.Numerics.Vector3.Zero,
        };
    }
}
