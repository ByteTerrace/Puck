using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The overlay glyph pack a windowed presentation draws its overlay text with, loaded once. A pack that cannot
/// be loaded leaves the overlay out of the default render graph, reported once, rather than failing the boot.</summary>
internal sealed class WorldOverlayGlyphs {
    /// <summary>Initializes a new instance of the <see cref="WorldOverlayGlyphs"/> class, loading the shipped pack with
    /// the icon repertoire's extra code points.</summary>
    /// <param name="icons">The world's icon table, whose code points the pack carries beside the font's.</param>
    public WorldOverlayGlyphs(WorldIconTable icons) {
        var fontsDirectory = PuckPaths.Shipped(relativePath: "Assets/Fonts");

        // The prepacked-artifact path: a warm start against the same icon repertoire reads the finished pack beside the
        // atlas; only a cold, rebaked or repertoire-changed start decodes the combined PNG (and persists the pack for the
        // next boot) — see WorldIconTable's remarks.
        Pack = new OverlayGlyphAtlasSet(fontsDirectory: fontsDirectory).LoadOverlayPack(extraCodePoints: icons.ExtraCodePoints);

        if (Pack is null) {
            Console.Error.WriteLine(value: $"[unified-overlay] skipped: no usable glyph atlas under '{fontsDirectory}' (restore the committed fixed-UI assets).");
        }
    }

    /// <summary>Gets the loaded pack, or <see langword="null"/> when none could be loaded.</summary>
    public OverlayGlyphSdfPack? Pack { get; }
}
/// <summary>Builds the render root both GPU presentation shapes present and capture: the SDF engine node as the default
/// render graph's <c>sdf.world</c> producer, the graph's packages (each <c>render.extensions</c> set, and the overlay when
/// the shape draws one), and the <see cref="RenderGraphRuntime"/> that runs them, behind the node the host
/// produces frames from.</summary>
internal static class WorldRenderRoot {
    /// <summary>Builds the render root and records it, and the engine node, on the <see cref="WorldRenderProbe"/>.</summary>
    /// <param name="sp">The composed services.</param>
    /// <param name="overlay">The overlay package the root graph draws, or <see langword="null"/> when it draws
    /// none.</param>
    /// <returns>The render root, tied to the screen binder's teardown.</returns>
    /// <exception cref="InvalidOperationException">The runtime refused the default graph it was given.</exception>
    public static IRenderNode Build(IServiceProvider sp, OverlayPackage? overlay) {
        var hostSettings = sp.GetRequiredService<WorldHostSettings>();
        var width = ((uint)hostSettings.Width);
        var height = ((uint)hostSettings.Height);
        var binder = sp.GetRequiredService<WorldScreenBinder>();
        var frameSource = sp.GetRequiredService<WorldFramePresenter>();
        var device = sp.GetRequiredService<IGpuDeviceContext>();
        var definition = sp.GetRequiredService<WorldDefinition>();
        var graph = sp.GetRequiredService<WorldRootGraph>();
        var engine = SdfWorldRenderBuilder.Build(
            pipelines: sp.GetRequiredService<SdfWorldPipelineCache>(),
            spec: new SdfWorldRenderSpec(
                FrameSource: frameSource,
                Height: height,
                Width: width
            ) {
                Children = sp.GetRequiredService<WorldPipelineRuntime>().Entries.ToDictionary(
                    keySelector: static entry => entry.Key,
                    elementSelector: static entry => ((IRenderNode)entry.Value.Node)
                ),
                DynamicTransformCapacity = frameSource.DynamicTransformCapacity,
                HostsOnDirectX = hostSettings.HostsOnDirectX,
                InstanceCapacity = frameSource.InstanceCapacity,
                ProgramWordCapacity = frameSource.ProgramWordCapacity,
                // The diegetic screens' source + light providers — the test-pattern screen's CPU feed and its room glow;
                // an unbound screen has no provider (the engine's procedural fallback lights it).
                ScreenLights = binder.ScreenLights,
                ScreenSourceFrames = binder.ScreenSources,
                ViewportCapacity = PlayerRoster.MaxSlots,
            }
        );
        var packages = new RenderGraphPackageRecorders();

        // The one external instance is the world, and the runtime owns the engine node from here on.
        packages.RegisterProducer(
            factory: _ => engine,
            package: RenderGraphPackageCatalog.SdfWorld
        );

        foreach (var id in graph.PostPasses.Keys) {
            packages.Register(
                factory: new PostProcessPackage(manifest: ShaderSetCatalog.Shipped.Load(id: id)),
                package: (RenderGraphPackageCatalog.PostProcessPrefix + id)
            );
        }

        if (overlay is not null) {
            packages.Register(
                factory: overlay,
                package: RenderGraphPackageCatalog.Overlay
            );
        }

        if (!RenderGraphRuntime.TryCreate(
            deviceContext: device,
            graphs: graph.Graphs(),
            hostsOnDirectX: hostSettings.HostsOnDirectX,
            packages: packages,
            refusal: out var refusal,
            root: graph.Root,
            runtime: out var runtime,
            set: graph.Instances
        )) {
            engine.Dispose();

            throw new InvalidOperationException(message: $"The default render graph was refused: {refusal.Code}: {refusal.Message}");
        }

        var root = new RenderGraphRuntimeNode(
            footprints: graph.Footprints,
            height: height,
            runtime: runtime,
            width: width
        );
        var probe = sp.GetRequiredService<WorldRenderProbe>();

        probe.Device = device;
        probe.Node = engine;
        probe.Root = root;
        sp.GetRequiredService<WorldPostRenderExtensionPasses>().Attach(
            extensions: definition.Render.Extensions,
            graph: graph,
            root: ((graph.Plan is null)
                ? null
                : runtime.Node(instance: runtime.Instances.IndexOf(name: WorldViewGraphs.MainInstance)))
        );

        // The teardown tie: the host loop disposes this root (device alive) before the presenter and long before the
        // container's reverse-creation-order sweep — ride that safe point for the binder's own GPU holdings (camera
        // feeds, jumbotron view engines), whose container-ordered disposal would otherwise land after device death.
        return new WorldRenderTeardown(
            inner: root,
            binder
        );
    }
}
