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
/// <summary>Builds the render root both GPU presentation shapes present and capture: the SDF engine node as the
/// <c>sdf.world</c> producer, the graph's packages (<c>place</c>, every post-process package a <c>views.post</c> row may
/// name, and the overlay when the shape draws one), and the <see cref="RenderGraphRuntime"/> that runs the document's
/// instances — its <c>views.graphs</c> rows beside the default graph composition synthesizes, or the rows alone under an
/// authored <c>views.root</c> — behind the node the host produces frames from. The <see cref="WorldViewGraphHost"/>
/// drives the runtime from then on, frame by frame.</summary>
internal static class WorldRenderRoot {
    /// <summary>Builds the render root and records it, and the engine node, on the <see cref="WorldRenderProbe"/>.</summary>
    /// <param name="sp">The composed services.</param>
    /// <param name="overlay">The overlay package the root graph draws, or <see langword="null"/> when it draws
    /// none.</param>
    /// <returns>The render root, tied to the screen binder's teardown.</returns>
    /// <exception cref="InvalidOperationException">The document's instances do not form a set, or the runtime refused
    /// them.</exception>
    public static IRenderNode Build(IServiceProvider sp, OverlayPackage? overlay) {
        var hostSettings = sp.GetRequiredService<WorldHostSettings>();
        var width = ((uint)hostSettings.Width);
        var height = ((uint)hostSettings.Height);
        var binder = sp.GetRequiredService<WorldScreenBinder>();
        var frameSource = sp.GetRequiredService<WorldFramePresenter>();
        var device = sp.GetRequiredService<IGpuDeviceContext>();
        var definition = sp.GetRequiredService<WorldDefinition>();
        var graph = sp.GetRequiredService<WorldRootGraph>();
        var host = sp.GetRequiredService<WorldViewGraphHost>();
        // The composition's one pipeline cache, which the engine node and every view's engine lease their pipeline sets
        // from; each records through the services of the device context it renders on.
        var pipelines = sp.GetRequiredService<SdfWorldPipelineCache>();

        // Configure the views now the frame source has probed the render envelope: each camera a screen shows and each
        // session screen registers a view the render graph renders through an engine sized to these worst-case
        // capacities, using the selected host's bytecode, at its declared extent over the display's.
        binder.ConfigureViews(
            displayHeight: hostSettings.Height,
            displayWidth: hostSettings.Width,
            dynamicTransformCapacity: frameSource.DynamicTransformCapacity,
            host: frameSource,
            hostsOnDirectX: hostSettings.HostsOnDirectX,
            instanceCapacity: frameSource.InstanceCapacity,
            pipelines: pipelines,
            programWordCapacity: frameSource.ProgramWordCapacity
        );

        // A walk into a view's world continues through the screens standing in it, and into a camera view through the
        // camera it films from.
        host.Screens = binder.Mappings;
        host.ViewCameras = binder;

        var synthesized = ((definition.Views.Root is null)
            ? graph
            : null);

        if (!WorldViewGraphHost.TryCompose(
            graphs: out var graphs,
            passes: static _ => 1,
            reason: out var composeReason,
            rendered: binder.Mappings.Views,
            root: out var rootName,
            set: out var set,
            sources: binder.Mappings.Sources.Instances,
            synthesized: synthesized,
            views: definition.Views
        )) {
            throw new InvalidOperationException(message: $"The document's render graph instances were refused: {composeReason}");
        }

        var engine = SdfWorldRenderBuilder.Build(
            pipelines: pipelines,
            spec: new SdfWorldRenderSpec(
                FrameSource: frameSource,
                Height: height,
                Width: width
            ) {
                DynamicTransformCapacity = frameSource.DynamicTransformCapacity,
                HostsOnDirectX = hostSettings.HostsOnDirectX,
                InstanceCapacity = frameSource.InstanceCapacity,
                ProgramWordCapacity = frameSource.ProgramWordCapacity,
                // The diegetic screens: the instance each one reads, and the light each casts into the room.
                ScreenSources = binder,
                ViewportCapacity = WorldRootGraph.ViewsOf(views: definition.Views),
            }
        );
        var packages = new RenderGraphPackageRecorders(regionCopy: sp.GetRequiredService<GpuRegionCopyPass>());

        // The world's external instances are its views: a camera view or a session the binder registered renders through
        // an engine node of its own, filming the frame the world's node renders; otherwise the first is the engine node,
        // which renders every split-screen view, and each later one the node's producer for that view. The runtime owns
        // every producer from here on.
        binder.ViewHost = engine;
        packages.RegisterProducer(
            factory: context => (binder.TryViewProducer(
                context: context,
                producer: out var view
            )
                ? view
                : ((WorldViewNames.ViewOf(instance: context.Instance) is { } seat)
                    ? engine.ViewProducer(view: seat)
                    : engine)),
            package: RenderGraphPackageCatalog.SdfWorld
        );
        frameSource.ViewRendered = engine.HasViewOutput;
        // The root places each pane where the host's composer shows it this frame.
        packages.Register(
            factory: new PlacePackage(placements: host),
            package: RenderGraphPackageCatalog.Place
        );

        // Every post-process package the catalog offers, not only the ones the booted views.post rows run: a live views.post
        // edit may name any of them, and a factory reads its stages only when a pass builds.
        foreach (var package in RenderGraphPackageCatalog.Engine.Packages.Where(predicate: static package => package.IsPostProcess)) {
            packages.Register(
                factory: new PostProcessPackage(package: package),
                package: package.Id
            );
        }

        if (overlay is not null) {
            packages.Register(
                factory: overlay,
                package: RenderGraphPackageCatalog.Overlay
            );
        }

        // An uploaded producer's source instance converts the region its feed writes through the conversion its
        // descriptor names; any other producer's instance, and a machine or probe source, renders through an external
        // producer that hands out its image through the binder's capture gate.
        SourceConversionPackage.RegisterAll(packages: packages);
        binder.Producers.RegisterPackages(
            adapt: binder.Adapt,
            packages: packages
        );
        packages.RegisterProducer(
            factory: binder.MachineSource,
            package: RenderGraphInstance.SourcePackage(producer: WorldImageProducerSettings.MachineId)
        );
        packages.RegisterProducer(
            factory: binder.ProbeSource,
            package: RenderGraphInstance.SourcePackage(producer: WorldImageProducerSettings.ProbeId)
        );

        if (!RenderGraphRuntime.TryCreate(
            deviceContext: device,
            graphs: graphs,
            hostsOnDirectX: hostSettings.HostsOnDirectX,
            packages: packages,
            pipelines: sp.GetRequiredService<GpuPassPipelineCache>(),
            refusal: out var refusal,
            root: rootName,
            runtime: out var runtime,
            set: set
        )) {
            engine.Dispose();

            throw new InvalidOperationException(message: $"The document's render graph was refused: {refusal.Code}: {refusal.Message}");
        }

        binder.Runtime = runtime;

        var overlaid = (overlay is not null);

        // The host composes the root again whenever the document's panes, views or views.post move, from the post passes
        // the document names then, so a live views.post edit reaches the running root.
        host.Attach(
            compose: (panes, views, post) => WorldRootGraph.Compose(
                overlay: overlaid,
                packages: RenderGraphPackageCatalog.Engine,
                post: post,
                panes: panes,
                views: views
            ),
            runtime: runtime,
            synthesized: synthesized
        );

        var root = new RenderGraphRuntimeNode(
            footprints: host.Footprints,
            height: height,
            runtime: runtime,
            width: width
        ) {
            // The host rewrites its footprint and root lists in place, so the node reads those lists rather than the copy
            // its constructor takes.
            Footprints = host.Footprints,
            Prepare = frameSource.PrepareGraph,
            Roots = host.Roots,
        };
        var probe = sp.GetRequiredService<WorldRenderProbe>();

        probe.Device = device;
        probe.Node = engine;
        probe.Root = root;
        sp.GetRequiredService<WorldPostPasses>().Attach(
            graph: () => host.Synthesized,
            root: () => runtime.NodeOf(instance: WorldViewGraphs.MainInstance)
        );

        // The teardown tie: the host loop disposes this root (device alive) before the presenter and long before the
        // container's reverse-creation-order sweep — ride that safe point for the binder's own GPU holdings (camera
        // feeds, capture fills), whose container-ordered disposal would otherwise land after device death.
        return new WorldRenderTeardown(
            inner: root,
            binder
        );
    }
}
