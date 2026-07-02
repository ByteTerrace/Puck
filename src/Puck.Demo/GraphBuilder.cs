using Puck.Hosting;
using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// Turns a validated run-document composition graph into a concrete root <see cref="IRenderNode"/> — the data-driven
/// counterpart of the demo's flag-driven node selection. Each <see cref="NodeDocument"/> kind maps to the SAME
/// <c>Create*</c> helpers the flags use, but the producer and its backend come from the document (node kind +
/// <c>produce</c>) rather than CLI booleans. The world/ray-query producers are driven by a frame source compiled from
/// the document's scene + viewports; the showcase renders its own built-in scene and consumes neither. OS/feature gates
/// (PUCK_RAY_QUERY, DXR support) live in those helpers, never in the document, so a document can never request a path
/// the hardware cannot run.
/// </summary>
internal static class GraphBuilder {
    /// <summary>Builds the root node for a run document's graph.</summary>
    /// <param name="document">The validated run document.</param>
    /// <param name="serviceProvider">The application service provider (resolves backend GPU services).</param>
    /// <param name="capturePath">An optional CLI capture path; overrides the graph node's own capture field when set.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (from the document's host section):
    /// the world/rt nodes then host same-device on Direct3D 12, and the showcase starts on Direct3D 12. <c>produce</c>
    /// still selects which backend RENDERS the world for a Vulkan-hosted run.</param>
    /// <param name="width">The resolved render width in pixels (from <c>host.size</c>); the world/rt producers render
    /// at this resolution. The showcase is fixed-resolution and ignores it.</param>
    /// <param name="height">The resolved render height in pixels (from <c>host.size</c>).</param>
    /// <returns>The root render node.</returns>
    public static IRenderNode Build(PuckRunDocument document, IServiceProvider serviceProvider, string? capturePath, bool hostsOnDirectX, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: serviceProvider);

        var graph = (document.Graph ?? throw new InvalidOperationException(message: "The run document has no graph node."));
        var capture = (capturePath ?? graph.Capture);

        _ = graph.TryResolveProduce(backend: out var produce);

        // The world/ray-query producers consume the document's scene + viewports through a frame source; the showcase
        // renders its own built-in scene, so it is never built one (and a showcase document carries no viewports, which
        // a frame source would reject).
        return graph switch {
            ShowcaseNode => CrossBackendShowcase.CreateRootNode(
                capturePath: capture,
                produceBackend: produce,
                serviceProvider: serviceProvider,
                startWithDirectX: hostsOnDirectX
            ),
            WorldNode world => DemoRootNode.CreateWorldRootNode(
                capturePath: capture,
                frameSource: RunDocument.CreateFrameSource(document: document),
                height: height,
                liveSources: ViewportBuilder.LiveSources(viewports: document.Viewports),
                onDirectX: hostsOnDirectX,
                produceBackend: produce,
                serviceProvider: serviceProvider,
                width: width,
                withChild: world.Child
            ),
            RtNode => DemoRootNode.CreateRtWorldNode(
                capturePath: capture,
                frameSource: RunDocument.CreateFrameSource(document: document),
                height: height,
                onDirectX: hostsOnDirectX,
                serviceProvider: serviceProvider,
                width: width
            ),
            MiniActionNode => DemoRootNode.CreateMiniActionRootNode(
                capturePath: capture,
                height: height,
                serviceProvider: serviceProvider,
                width: width
            ),
            CameraNode => new LiveCameraNode(
                height: height,
                serviceProvider: serviceProvider,
                width: width
            ),
            _ => throw new NotSupportedException(message: $"Unsupported graph node kind '{graph.GetType().Name}'."),
        };
    }
}
