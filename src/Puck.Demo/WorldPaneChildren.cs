using Puck.Hosting;
using Puck.Scene;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// Builds the <c>slot → IRenderNode</c> map <see cref="WorldProducerNode"/> hosts in place of SDF cameras: the legacy
/// bottom-right test-pattern child (<c>--world-child</c>), plus one <see cref="CameraPaneNode"/> for every
/// <c>live-camera</c> viewport the document declares. This is the single seam that makes a hardware camera a first-class
/// per-viewport source — the same children machinery the child surface already flows through, now populated from the
/// document instead of a hard-coded slot. The backend (SPIR-V vs DXIL) is known here, so each hosted node loads the
/// right bytecode; both node kinds run on the host device and hand back a same-device storage-image surface.
/// </summary>
internal static class WorldPaneChildren {
    /// <summary>Builds the hosted-children map for a world producer, or <see langword="null"/> when there are none.</summary>
    /// <param name="serviceProvider">The neutral compute service provider for the host backend.</param>
    /// <param name="directX">Whether to load Direct3D 12 (DXIL) kernels rather than Vulkan (SPIR-V) ones.</param>
    /// <param name="withChild">Whether to host the legacy bottom-right test-pattern child (slot 3).</param>
    /// <param name="frameSource">The frame source; its live-camera slots (when it is a <see cref="JsonSdfFrameSource"/>) are hosted as camera panes.</param>
    /// <returns>The slot → node map, or <see langword="null"/> when neither a child nor any live camera is present.</returns>
    public static IReadOnlyDictionary<int, IRenderNode>? Build(IServiceProvider serviceProvider, bool directX, bool withChild, ISdfFrameSource frameSource) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(frameSource);

        var liveCameraSlots = ((frameSource is JsonSdfFrameSource json) ? json.LiveCameraSlots : []);

        if (!withChild && (liveCameraSlots.Count == 0)) {
            return null;
        }

        var children = new Dictionary<int, IRenderNode>();

        if (withChild) {
            foreach (var (slot, child) in ChildSurfaceNode.CreateWorldChildren(serviceProvider: serviceProvider, directX: directX)) {
                children[slot] = child;
            }
        }

        if (liveCameraSlots.Count > 0) {
            var extension = (directX ? "dxil" : "spv");
            var resampleBytecode = File.ReadAllBytes(path: Path.Combine(
                path1: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: "Resample"),
                path2: $"resample.comp.{extension}"
            ));

            // A live-camera slot wins any collision with the legacy child (an explicit document source beats it).
            foreach (var live in liveCameraSlots) {
                children[live.Slot] = new CameraPaneNode(
                    pixelSize: live.PixelSize,
                    quantize: live.Quantize,
                    resampleBytecode: resampleBytecode,
                    serviceProvider: serviceProvider
                );
            }
        }

        return children;
    }
}
