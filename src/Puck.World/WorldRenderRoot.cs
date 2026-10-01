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
/// <summary>Builds the render root both GPU presentation shapes present and capture: the world's SDF residency and the
/// <c>sdf.world</c> passes every view renders through, the graph's packages (<c>place</c>, every post-process package a
/// <c>views.post</c> row may name, and the overlay when the shape draws one), and the <see cref="RenderGraphRuntime"/>
/// that runs the document's
/// instances — its <c>views.graphs</c> rows beside the default graph composition synthesizes, or the rows alone under an
/// authored <c>views.root</c> — behind the node the host produces frames from. The <see cref="WorldViewGraphHost"/>
/// drives the runtime from then on, frame by frame.</summary>
internal static class WorldRenderRoot {
    /// <summary>Builds the render root and records it, and the world's residency, on the <see cref="WorldRenderProbe"/>.</summary>
    /// <param name="sp">The composed services.</param>
    /// <param name="overlay">The overlay package the root graph draws, or <see langword="null"/> when it draws
    /// none.</param>
    /// <returns>The render root, which releases the screen binder at its teardown.</returns>
    /// <exception cref="InvalidOperationException">The document's instances do not form a set, or the runtime refused
    /// them.</exception>
    public static IRenderRoot Build(IServiceProvider sp, OverlayPackage? overlay) {
        var hostSettings = sp.GetRequiredService<WorldHostSettings>();
        var width = ((uint)hostSettings.Width);
        var height = ((uint)hostSettings.Height);
        var binder = sp.GetRequiredService<WorldScreenBinder>();
        var frameSource = sp.GetRequiredService<WorldFramePresenter>();

        frameSource.ResizeDisplay(height: height, width: width);

        var bakes = sp.GetService<WorldBakeSchedule>();
        var client = sp.GetRequiredService<WorldClient>();
        var device = sp.GetRequiredService<IGpuDeviceContext>();
        var definition = sp.GetRequiredService<WorldDefinition>();
        var graph = sp.GetRequiredService<WorldRootGraph>();
        var host = sp.GetRequiredService<WorldViewGraphHost>();
        var comparison = sp.GetRequiredService<WorldFrameComparison>();
        var compareCapture = sp.GetRequiredService<WorldCompareCapture>();

        host.Comparison = comparison;
        host.ComparisonViewports = sp.GetRequiredService<WorldSeatViewports>();
        // The composition's pipeline catalog, whose pass-pipeline cache the world's residency and every view's lease their
        // pipelines from; each records through the services of the device context it renders on.
        var pipelines = sp.GetRequiredService<SdfWorldPipelineCatalog>();

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
            programWordCapacity: frameSource.ProgramWordCapacity,
            viewports: sp.GetRequiredService<WorldSeatViewports>()
        );

        // A walk into a view's world continues through the screens standing in it, and into a camera view through the
        // camera it films from.
        host.Screens = binder.Mappings;
        host.ViewScenes = binder;

        var synthesized = ((definition.Views.Root is null)
            ? graph
            : null);

        if (!WorldViewGraphHost.TryCompose(
            comparison: comparison,
            graphs: out var graphs,
            passes: static _ => 1,
            reason: out var composeReason,
            rendered: binder.Mappings.Views,
            root: out var rootName,
            scene: out var scene,
            set: out var set,
            sources: binder.Mappings.Sources.Instances,
            synthesized: synthesized,
            views: definition.Views
        )) {
            throw new InvalidOperationException(message: $"The document's render graph instances were refused: {composeReason}");
        }

        var residency = SdfWorldRenderBuilder.Build(
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
            }
        );
        var packages = new RenderGraphPackageRecorders(regionCopy: sp.GetRequiredService<GpuRegionCopyPass>());

        // The world's views are sdf.world instances. A camera view renders its view of the world's frame, after the
        // presentation's own; a session the binder registered renders a residency of its own; every other instance renders
        // one of the presentation's own views, the first unless its name numbers a later one (WorldViewNames), or, while that
        // view's seat is presented in another world, the view of that world's scene. The package holds the world's residency for its lifetime, so
        // the frame every other residency films is captured whether or not a view renders it.
        binder.ViewHost = residency;
        binder.Presenter = frameSource;
        binder.CrossingCapture = sp.GetService<WorldCrossingCapture>();
        host.Pickers = new SdfWorldPasses(
                host: residency,
                resolve: instance => (binder.TryResolveView(
                    name: instance,
                    view: out var view
                )
                    ? view
                    : (binder.TryResolveRoutedView(
                        name: instance,
                        view: out var routed
                    )
                        ? routed
                        : new SdfWorldView(
                            Residency: residency,
                            View: binder.HostView(view: (WorldViewNames.ViewOf(instance: instance) ?? 0))
                        )))
            );
        packages.Register(factory: host.Pickers, package: RenderGraphPackageCatalog.SdfWorld);
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

        // An uploaded producer's source instance and a machine source convert the region their upload writes through
        // the conversion its descriptor names; any other producer's instance, and a probe source, renders through an
        // external producer that hands out its image through the binder's capture gate.
        SourceConversionPackage.RegisterAll(packages: packages);
        packages.RegisterSource(package: WorldFrameComparison.SourcePackage, factory: context => {
            var slot = WorldComparisonGraph.SeatOf(source: context.Instance);
            var snapshot = ((slot >= 0) ? comparison.Seat(slot: slot) : null);

            if ((snapshot is null) || (context.Settings?["hold"].GetUInt64() != snapshot.Sequence)) {
                throw new InvalidOperationException(message: $"Comparison source '{context.Instance}' has no matching held frame.");
            }
            return WorldFrameComparison.Open(snapshot: snapshot);
        });
        binder.Producers.RegisterPackages(
            adapt: binder.Adapt,
            packages: packages
        );
        packages.RegisterSource(
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
            residency.Dispose();

            throw new InvalidOperationException(message: $"The document's render graph was refused: {refusal.Code}: {refusal.Message}");
        }

        binder.Runtime = runtime;
        frameSource.CapturePending = () => (runtime.PendingCapturePath is not null);
        // A view the root places shows once its instance has completed an image, so a capture is never served over a
        // stand-in.
        frameSource.ViewRendered = view => runtime.TryLatestImage(
            image: out _,
            instance: WorldRootGraph.ProducerOf(view: view)
        );

        var overlaid = (overlay is not null);
        var timing = sp.GetRequiredService<WorldGpuTiming>();

        frameSource.FrameComposed = () => {
            host.PresentComparison();
            compareCapture.RecordPreparedFrame();
        };

        // The host composes the root again whenever the document's panes, views, views.post or render.tonemap move, from
        // the post passes and tonemap the document names then, so a live views.post or render.tonemap edit reaches the
        // running root.
        // A debug view shows its own colors, so the root runs no tonemap while one is on.
        host.ShowsDebugView = () => (residency.DebugMode != 0);
        host.Attach(
            compose: (panes, views, post, tonemap) => WorldRootGraph.Compose(
                overlay: overlaid,
                packages: RenderGraphPackageCatalog.Engine,
                post: post,
                panes: panes,
                tonemap: tonemap,
                views: views
            ),
            runtime: runtime,
            scene: scene,
            synthesized: synthesized
        );
        compareCapture.Attach(target: () => runtime.CaptureTarget(instance: host.ComparisonLiveRoot!),
            completedFrames: () => runtime.NodeOf(instance: host.ComparisonLiveRoot!)?.FrameCounter);

        var root = new RenderGraphRuntimeNode(
            footprints: host.Footprints,
            height: height,
            runtime: runtime,
            width: width
        ) {
            // The host rewrites its footprint and root lists in place, so the node reads those lists rather than the copy
            // its constructor takes.
            Footprints = host.Footprints,
            // The binder's GPU holdings (camera feeds, capture fills, the views' residencies) and the world's residency are
            // created before the device context, so the container would dispose them after it; the root's teardown releases
            // them, after the runtime's passes gave back their holds, while the device is alive.
            Holdings = [compareCapture, binder, residency],
            Prepare = (in FrameContext context) => {
                compareCapture.Poll();
                bakes?.Pump(definition: client.Definition);
                frameSource.PrepareGraph(context: in context);
                timing.Tick();
            },
            Roots = host.Roots,
        };
        var probe = sp.GetRequiredService<WorldRenderProbe>();

        probe.Bakes = bakes;
        probe.Device = device;
        probe.Residency = residency;
        probe.Root = root;
        probe.Settings = sp.GetService<WorldRenderSettings>();
        sp.GetRequiredService<WorldPostPasses>().Attach(
            graph: () => host.Synthesized,
            root: () => runtime.NodeOf(instance: WorldViewGraphs.MainInstance)
        );

        return root;
    }
}
