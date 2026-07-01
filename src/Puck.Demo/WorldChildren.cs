using Puck.Hosting;
using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// Builds a <see cref="WorldProducerNode"/>'s slot→child map from the two per-viewport child sources: the legacy
/// <c>--world-child</c> test pattern (a <see cref="ChildSurfaceNode"/> at the bottom-right slot) and each document
/// <see cref="LiveCameraSource"/> viewport (a <see cref="CameraChildNode"/> at its slot). Both produce a same-device
/// General-layout storage image the world composites into a viewport. The children are built with the world's GPU
/// services (<paramref name="gpuServices"/>) so they run on the compositor's device; the live camera's CPU pixels come
/// from the application services (<paramref name="cameraServices"/>). Returns <see langword="null"/> when neither source
/// contributes a child (the world renders every slot as an SDF camera).
/// </summary>
internal static class WorldChildren {
    /// <summary>Merges the legacy test child and the document live-camera slots into one slot→child map.</summary>
    /// <param name="gpuServices">The world's neutral GPU compute services (the child produces on the world's device).</param>
    /// <param name="cameraServices">The application services that resolve the camera-capture backend.</param>
    /// <param name="testChild">Whether to add the legacy <see cref="ChildSurfaceNode"/> test pattern at its slot.</param>
    /// <param name="liveSources">The document's live-camera viewport slots (or <see langword="null"/>/empty for none).</param>
    /// <param name="directX">Whether the world device is Direct3D 12 (selects the DXIL child kernels).</param>
    /// <returns>The merged slot→child map, or <see langword="null"/> when empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gpuServices"/> or <paramref name="cameraServices"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<int, IRenderNode>? Build(IServiceProvider gpuServices, IServiceProvider cameraServices, bool testChild, IReadOnlyDictionary<int, LiveCameraSource>? liveSources, bool directX) {
        ArgumentNullException.ThrowIfNull(cameraServices);
        ArgumentNullException.ThrowIfNull(gpuServices);

        Dictionary<int, IRenderNode>? children = null;

        if (testChild) {
            foreach (var (slot, node) in ChildSurfaceNode.CreateWorldChildren(serviceProvider: gpuServices, directX: directX)) {
                (children ??= [])[slot] = node;
            }
        }

        if (liveSources is not null) {
            foreach (var (slot, source) in liveSources) {
                (children ??= [])[slot] = new CameraChildNode(cameraServices: cameraServices, directX: directX, gpuServices: gpuServices, source: source);
            }
        }

        return children;
    }
}
