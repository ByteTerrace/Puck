using Puck.Hosting;
using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// Turns a validated run-document composition graph into a concrete root <see cref="IRenderNode"/> — the data-driven
/// counterpart of the demo's flag-driven node selection. <see cref="OverworldNode"/> builds its own dynamic scene +
/// camera each frame; <see cref="WorldNode"/> renders the document's scene + viewports through the shared
/// <see cref="SdfWorldRenderBuilder"/> on the HOST backend. OS/feature gates live in the <c>Create*</c> helpers,
/// never in the document, so a document can never request a path the hardware cannot run.
/// </summary>
internal static class GraphBuilder {
    /// <summary>Builds the root node for a run document's graph.</summary>
    /// <param name="document">The validated run document.</param>
    /// <param name="serviceProvider">The application service provider (resolves backend GPU services).</param>
    /// <param name="capturePath">An optional CLI capture path; overrides the graph node's own capture field when set.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (from the document's host section).</param>
    /// <param name="width">The resolved render width in pixels (from <c>host.size</c>); the showcase is
    /// fixed-resolution and ignores it.</param>
    /// <param name="height">The resolved render height in pixels (from <c>host.size</c>).</param>
    /// <returns>The root render node.</returns>
    public static IRenderNode Build(PuckRunDocument document, IServiceProvider serviceProvider, string? capturePath, bool hostsOnDirectX, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: serviceProvider);

        var graph = (document.Graph ?? throw new InvalidOperationException(message: "The run document has no graph node."));
        var capture = (capturePath ?? graph.Capture);

        return graph switch {
            OverworldNode overworld => DemoRootNode.CreateOverworldRootNode(
                capturePath: capture,
                // Nullable-optional-field pattern: an omitted `consoles` arrives null despite the initializer.
                consoles: (overworld.Consoles ?? []),
                height: height,
                hostsOnDirectX: hostsOnDirectX,
                // Nullable-optional-field pattern (see OverworldNode.Immersed's note): an omitted member arrives null.
                immersed: (overworld.Immersed == true),
                library: (overworld.Library ?? []),
                serviceProvider: serviceProvider,
                width: width
            ),
            WorldNode world => CreateWorldRootNode(
                capturePath: capture,
                document: document,
                height: height,
                hostsOnDirectX: hostsOnDirectX,
                serviceProvider: serviceProvider,
                width: width,
                world: world
            ),
            _ => throw new NotSupportedException(message: $"Unsupported graph node kind '{graph.GetType().Name}'."),
        };
    }

    /// <summary>The reason a document's graph cannot run on the RESOLVED host, or <see langword="null"/> when it can —
    /// the pre-flight the entry point runs BEFORE the window host is built, so an unsupported document exits with an
    /// attributed error instead of a mid-host crash. The build path throws the same reason (single owner), so the
    /// check can never drift from what the builder actually rejects.</summary>
    /// <param name="document">The validated run document.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12.</param>
    public static string? UnsupportedReason(PuckRunDocument document, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return (document.Graph is WorldNode world)
            ? WorldUnsupportedReason(hostsOnDirectX: hostsOnDirectX, viewports: (document.Viewports ?? []), world: world)
            : null;
    }

    // The ONE statement of the world graph's CAPABILITY gaps — deferred or host-dependent paths a VALID document may
    // name (retired shapes like `child` are the validator's job: host-independent, never valid). Cross-backend
    // `produce` (a bespoke producer device + shared-image handoff) is DEFERRED until the shared same-device host is
    // stable — an explicit produce that disagrees with the host is rejected loudly, never silently re-hosted; a null
    // produce simply renders on the host.
    private static string? WorldUnsupportedReason(WorldNode world, IReadOnlyList<Viewport> viewports, bool hostsOnDirectX) {
        var hostBackend = (hostsOnDirectX ? "directx" : "vulkan");

        if ((world.Produce is not null) && world.TryResolveProduce(backend: out var produce) && !string.Equals(produce, hostBackend, StringComparison.Ordinal)) {
            return $"graph.produce:\"{produce}\" on a {hostBackend} host is the cross-backend producer path, which is not yet re-hosted on the shared world renderer; render on the host backend (drop 'produce' or match host.backend).";
        }

        foreach (var (slot, source) in ViewportBuilder.ChildSources(viewports: viewports)) {
            switch (source) {
                case GamingBrickSource brick when (brick.RomPath is null):
                    return $"viewport[{slot}]: a world document's gaming-brick source requires a romPath (the cartridge shelf is an overworld affordance).";
                case GamingBrickSource brick when !File.Exists(path: brick.RomPath):
                    return $"viewport[{slot}]: romPath '{brick.RomPath}' does not exist.";
                case GamingBrickSource:
                    break;
                case LiveCameraSource:
                    return $"viewport[{slot}]: the live-camera viewport source is not yet re-hosted on the shared world renderer (the capture stack lives in Puck.Platform; its child render node is the open piece).";
                default:
                    return $"viewport[{slot}]: source '{source.GetType().Name}' is not a buildable child viewport source.";
            }
        }

        return null;
    }

    // The document-driven world: the scene + viewports compile to a static frame source and render through the
    // shared builder on the HOST backend.
    private static IRenderNode CreateWorldRootNode(WorldNode world, PuckRunDocument document, IServiceProvider serviceProvider, string? capturePath, bool hostsOnDirectX, uint width, uint height) {
        var viewports = (document.Viewports ?? []);

        // Defense in depth: the entry point pre-flights the same reason before the host is built, so this throw only
        // fires for a caller that skipped the pre-flight.
        if (WorldUnsupportedReason(hostsOnDirectX: hostsOnDirectX, viewports: viewports, world: world) is { } reason) {
            throw new NotSupportedException(message: reason);
        }

        var children = BuildWorldChildren(hostsOnDirectX: hostsOnDirectX, serviceProvider: serviceProvider, viewports: viewports);

        return SdfWorldRenderBuilder.Build(
            serviceProvider: serviceProvider,
            spec: new SdfWorldRenderSpec(
                FrameSource: RunDocument.CreateFrameSource(document: document),
                Height: height,
                Width: width
            ) {
                CapturePath = capturePath,
                Children = children,
                HostsOnDirectX = hostsOnDirectX,
                ScreenSources = BuildWorldScreenSources(children: children, screenSources: document.ScreenSources),
            }
        ).Root;
    }

    // The document's screen-source table → per-screen providers. A viewport provider resolves the gaming-brick child
    // at its slot and hands the screen seam the machine's NATIVE (unresampled) framebuffer — the validator already
    // proved the slot names a gaming-brick viewport, and BuildWorldChildren built a brick for every such slot.
    private static IReadOnlyDictionary<int, Func<nint>>? BuildWorldScreenSources(IReadOnlyList<ScreenSourceDocument>? screenSources, IReadOnlyDictionary<int, IRenderNode>? children) {
        if ((screenSources is null) || (screenSources.Count == 0)) {
            return null;
        }

        var providers = new Dictionary<int, Func<nint>>(capacity: screenSources.Count);

        foreach (var entry in screenSources) {
            switch (entry.Source) {
                case ViewportScreenSource viewport: {
                    var brick = (GamingBrickChildNode)children![viewport.Slot];

                    providers[entry.ScreenIndex] = () => (brick.IsAssigned ? brick.NativeImageViewHandle : 0);

                    break;
                }
                default:
                    // The seam future provider kinds grow through: a kind added to the MODEL before it is hosted here
                    // must fail loudly at build, not silently render the procedural fallback.
                    throw new NotSupportedException(message: $"screenSources[{entry.ScreenIndex}]: provider '{entry.Source?.GetType().Name ?? "null"}' is not yet hosted by the world render path.");
            }
        }

        return providers;
    }

    // The viewport-source → child-node factory: each child-content slot the document declares becomes a render node
    // produced at its slot's pixel rect. ROM loading stays eager and fail-fast here, before any frame renders. A
    // static document region never animates, so a brick's output follows the target extent (no fixed allocation).
    private static IReadOnlyDictionary<int, IRenderNode>? BuildWorldChildren(IReadOnlyList<Viewport> viewports, IServiceProvider serviceProvider, bool hostsOnDirectX) {
        var sources = ViewportBuilder.ChildSources(viewports: viewports);

        if (sources.Count == 0) {
            return null;
        }

        var children = new Dictionary<int, IRenderNode>(capacity: sources.Count);
        var brickOrdinal = 0;

        foreach (var (slot, source) in sources.OrderBy(keySelector: static entry => entry.Key)) {
            // Non-brick sources were rejected by WorldUnsupportedReason before this runs.
            var brick = (GamingBrickSource)source;

            children[slot] = new GamingBrickChildNode(
                appServices: serviceProvider,
                brickOrdinal: brickOrdinal++,
                cartridgeRom: File.ReadAllBytes(path: brick.RomPath!),
                directX: hostsOnDirectX,
                exitCondition: brick.Exit,
                gpuServices: serviceProvider,
                savePath: $"{brick.RomPath}.sav",
                source: brick,
                sourceId: $"gaming-brick:{slot}"
            );
        }

        return children;
    }
}
