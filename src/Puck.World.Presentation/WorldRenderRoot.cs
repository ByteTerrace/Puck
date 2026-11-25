using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The overlay glyph pack a windowed presentation draws its overlay text with, loaded once. A pack that cannot
/// be loaded leaves the overlay out of the default render graph, reported once, rather than failing the boot.</summary>
public sealed class WorldOverlayGlyphs {
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
public static class WorldRenderRoot {
    /// <summary>Builds the render root and records it, and the world's residency, on the <see cref="WorldRenderProbe"/>.</summary>
    /// <param name="sp">The composed services.</param>
    /// <param name="overlay">The overlay package the root graph draws, or <see langword="null"/> when it draws
    /// none.</param>
    /// <returns>The render root, which releases the graph host's residency registrations, the screen binder and the
    /// world's residency at its teardown while the device is alive.</returns>
    /// <exception cref="InvalidOperationException">The document's instances do not form a set, or the runtime refused
    /// them.</exception>
    public static IRenderRoot Build(IServiceProvider sp, OverlayPackage? overlay) {
        var hostSettings = sp.GetRequiredService<WorldHostSettings>();
        var width = ((uint)hostSettings.Width);
        var height = ((uint)hostSettings.Height);
        var binder = sp.GetRequiredService<WorldScreenBinder>();
        var frameSource = sp.GetRequiredService<WorldFramePresenter>();

        frameSource.ResizeDisplay(height: height, width: width);

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
        sp.GetRequiredService<IWorldViewHost>().ConfigureViews(
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
        // A glass out of sight warms its world by the walk from the viewer's seat, over the navigation of the seat's
        // delivered world.
        host.PortalReach = new WorldPortalReach(senses: sp.GetService<WorldSeatSenses>());

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
                MeshLibrary = binder.MeshLibrary,
                ProgramWordCapacity = frameSource.ProgramWordCapacity,
                // The diegetic screens: the instance each one reads, and the light each casts into the room.
                ScreenSources = binder,
            }
        );
        var packages = new RenderGraphPackageRecorders(regionCopy: sp.GetRequiredService<GpuRegionCopyPass>());

        // The world's views are sdf.world instances. A camera view renders its view of the world's frame, after the
        // presentation's own; a session renders a view of the one residency its destination renders through (this world's,
        // or its endpoint scene's) while its proof says it is handed the whole scene, a scene it shares with sessions proved
        // to be handed the same one, and a residency of its own otherwise; every other instance
        // renders one of the presentation's own views, the first unless its name numbers a later one (WorldViewNames), or,
        // while that view's seat is presented in another world, the view of that world's scene. The package holds the
        // world's residency for its lifetime, so the frame every other residency films is captured whether or not a view
        // renders it.
        binder.ViewHost = residency;
        binder.Presenter = frameSource;
        binder.CrossingCapture = sp.GetService<WorldCrossingCapture>();
        host.Pickers = new SdfWorldPasses(
                host: residency,
                resolve: instance => binder.ResolveInstanceView(
                    name: instance,
                    world: residency
                )
            );
        packages.Register(factory: host.Pickers, package: RenderGraphPackageCatalog.SdfWorld);
        host.Environment = new SdfSkyEnvironmentPasses(views: host.Pickers);
        packages.Register(factory: host.Environment, package: RenderGraphPackageCatalog.SkyEnvironment);
        // The root places each view and pane where the host's composer shows it this frame.
        var place = new PlacePackage(placements: host);

        packages.Register(factory: place, package: RenderGraphPackageCatalog.Place);
        packages.Register(factory: place, package: RenderGraphPackageCatalog.PlaceViews);

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
        // the conversion its descriptor names, into working values relative to the host's paper white; any other
        // producer's instance, and a probe source, renders through an external producer that hands out its image through
        // the binder's capture gate.
        SourceConversionPackage.RegisterAll(
            packages: packages,
            paperWhiteNits: hostSettings.PaperWhiteNits
        );
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
            disposesInBackground: true,
            graphs: graphs,
            hostsOnDirectX: hostSettings.HostsOnDirectX,
            jobs: sp.GetService<Puck.Hosting.Jobs.JobPool>(),
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
        // Only a settled capture pins the presented state to the completed tick; a presented one reads what the
        // display shows, interpolation included.
        frameSource.CapturePending = () => runtime.HoldsPresentation;
        // Lockstep presentation renders every frame free of frame history too, so a still of a tick repeats.
        if (sp.GetService<PresentPacingControl>() is { } pacing) {
            frameSource.HistoryFree = () => pacing.TickLocked;
            frameSource.LightingConvergence = () => pacing.ConvergesLighting;
        }
        // A view the root places shows once its instance has completed an image, so a capture is never served over a
        // stand-in.
        frameSource.ViewRendered = view => runtime.TryLatestImage(
            image: out _,
            instance: WorldRootGraph.ProducerOf(view: view)
        );

        var overlaid = (overlay is not null);
        var timing = sp.GetRequiredService<WorldGpuTiming>();
        // The background allowance follows the GPU time every node of the graph leaves free of the display's period.
        var allowance = new WorldBackgroundAllowance(
            budget: () => SdfResidencySelector.Of(device: device).Work,
            load: new WorldFrameLoadSource(presentTiming: static () => null, probe: sp.GetRequiredService<WorldRenderProbe>(), timing: timing, view: null),
            timing: timing
        );

        frameSource.FrameComposed = () => {
            host.PresentComparison();
            compareCapture.RecordPreparedFrame();
        };

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
            scene: scene,
            synthesized: synthesized
        );
        compareCapture.Attach(target: () => runtime.CaptureTarget(instance: host.ComparisonLiveRoot!),
            completedFrames: () => runtime.NodeOf(instance: host.ComparisonLiveRoot!)?.FrameCounter);

        RenderGraphRuntimeNode? node = null;
        // The overlay node and scene the capture companions were last made for.
        object? companionNode = null;
        object? companionScene = null;
        // Whether the device profile's memory bars were bound for the device this root renders on.
        var profiles = sp.GetService<WorldDeviceProfileState>();
        var profileBound = (profiles is null);
        var display = new WorldDisplayExtent(
            binder: binder,
            height: height,
            presenter: frameSource,
            width: width
        );
        var cursorFeed = (overlaid ? null : sp.GetRequiredService<WorldCursorFeed>());
        var inspector = (overlaid ? null : sp.GetRequiredService<WorldInspector>());
        var root = new RenderGraphRuntimeNode(
            footprints: host.Footprints,
            height: height,
            runtime: runtime,
            width: width
        ) {
            // The windowed overlay ticks these feeds immediately before drawing them. A root without an overlay
            // still follows the same published views and polls explanation fences, including on retained frames.
            FrameProduced = (overlaid ? null : () => {
                cursorFeed!.Tick();
                inspector!.Tick();
            }),
            // The host rewrites its footprint and root lists in place, so the node reads those lists rather than the copy
            // its constructor takes.
            Footprints = host.Footprints,
            Named = host.Named,
            // The graph host's environment registrations retain residencies. Release those registrations
            // before the binder's GPU holdings and the world's creator hold, after the runtime's passes have retired.
            // These services can predate the device context, so their later container disposal is too late for GPU owners.
            // A released residency's device objects wait in the device's retirement, and releasing them retires the
            // builds they still lease into the catalog, so the retirement is drained before the catalog's builds, which
            // are waited out last, while the device lives.
            Holdings = [compareCapture, host, binder, residency, new DisposeOnce(action: () => {
                device.TryWaitIdle();
                device.Services.Retirement.Drain();
            }), pipelines.RetiredBuilds],
            Prepare = (in FrameContext context) => {
                // The profile the world authors for this adapter applies once the device is up and has named its
                // adapter; the device opens lazily, so it may have no identity when the root is composed. Its memory
                // bars apply here, on the frame thread, before the first frame's residencies stage anything. Its
                // quality table's levers are writes to the authority, which the frame may not make, so the loop makes
                // them in its next boundary window, through the door world.device-profile takes: the device runs the
                // path its table enables from the frame after that window; a lever command after it overrides it as
                // usual.
                if (!profileBound && (device.Identity is { } identity)) {
                    profileBound = true;

                    var render = client.Definition.Render;
                    var window = sp.GetService<Puck.World.Protocol.WorldLinkBoundary>();

                    profiles?.ApplyAtBoot(
                        adapter: identity.AdapterName,
                        authority: work => {
                            if (window is null) { work(); } else { window.Post(work: work); }
                        },
                        presets: sp.GetService<IWorldQualityPresets>(),
                        profile: WorldDeviceProfiles.Select(adapterName: identity.AdapterName, render: render),
                        render: render,
                        report: static applied => Console.Error.WriteLine(value: applied.Output)
                    );
                }
                display.Follow(
                    context: in context,
                    root: node!
                );
                compareCapture.Poll();
                frameSource.PrepareGraph(context: in context);
                // The companions are made again only for another overlay node or scene, never for each frame.
                if (
                    overlaid &&
                    (host.Synthesized is { } synthesizedRoot) &&
                    (runtime.NodeOf(instance: WorldRootGraph.OverlayInstance) is { } captureNode) &&
                    (!ReferenceEquals(objA: captureNode, objB: companionNode) || !Equals(objA: synthesizedRoot.Scene, objB: companionScene))
                ) {
                    var shown = synthesizedRoot.Scene;

                    captureNode.CaptureCompanionFactory = () => sp.GetRequiredService<WorldDiagnosticOverlay>().CaptureCompanion(runtime: runtime, scene: shown);
                    captureNode.CaptureCompanionReady = () => sp.GetRequiredService<WorldDiagnosticOverlay>().CaptureReady(runtime: runtime, scene: shown);
                    companionNode = captureNode;
                    companionScene = shown;
                }
                allowance.Advance(context: in context);
                timing.Tick();
            },
            Roots = host.Roots,
        };

        node = root;
        var probe = sp.GetRequiredService<WorldRenderProbe>();

        probe.Device = device;
        probe.Residency = residency;
        probe.Views = host.Pickers;
        probe.MeshIrradiance.Retarget(target: residency.MeshIrradiance.Counters);
        probe.Root = root;
        probe.Settings = sp.GetService<WorldRenderSettings>();
        var iterations = sp.GetRequiredService<WorldIterationClock>();

        var viewports = sp.GetService<WorldSeatViewports>();

        iterations.Attach(bakes: binder, probe: probe, presented: () => ((viewports is null)
            ? null
            : probe.Views?.FindPicker(instance: (viewports.Seat(slot: 0).RenderInstance ?? WorldViewGraphs.WorldInstance))?.View?.Residency));
        // Dynamic resolution reads the views' GPU frame time, the presenter's present timing, resolved on the first frame
        // it reads it, and the views' counted march steps.
        frameSource.FrameLoad = new WorldFrameLoadSource(
            presentTiming: () => (sp.GetService<ISurfacePresenter>() as IPresentTimingFeedback),
            probe: probe,
            timing: timing
        );
        frameSource.FrameLoadForView = name => new WorldFrameLoadSource(
            presentTiming: () => (sp.GetService<ISurfacePresenter>() as IPresentTimingFeedback),
            probe: probe,
            timing: timing,
            view: name
        );
        sp.GetRequiredService<WorldPostPasses>().Attach(
            graph: () => host.Synthesized,
            root: () => runtime.NodeOf(instance: WorldViewGraphs.MainInstance)
        );

        return new WorldPresentationRoot(root, client, () => frameSource.PinsPresentation, iterations, device, pipelines.RetiredBuilds, allowance);
    }

    // All graph consumers see the clock of the snapshot the client actually received, including after replay
    // restoration or a paused boot. Host pump elapsed time is only a scheduling coordinate. Each produced frame advances
    // the edit loops the World times, just before the host presents it.
    private sealed class WorldPresentationRoot(RenderGraphRuntimeNode root, WorldClient client, Func<bool> pinned, WorldIterationClock iterations, IGpuDeviceContext device, RetiredBuilds retired, WorldBackgroundAllowance allowance) : IRenderRoot, ICaptureRequestTarget {
        public string? PendingCapturePath => root.PendingCapturePath;

        public void RequestCapture(FrameCaptureRequest request) => root.RequestCapture(request: request);
        public void Dispose() => root.Dispose();
        // The runtime's loss releases every residency it holds; a released residency's objects, waiting in the device's
        // retirement, go next and retire the builds they still lease into the catalog, whose builds are waited out last,
        // before the device is recreated.
        public void OnDeviceLost() {
            root.OnDeviceLost();
            device.Services.Retirement.Drain();
            retired.Drain();
        }
        public RootFrame ProduceFrame(in FrameContext context) {
            var stepTicks = client.StepTicks;
            var fraction = (pinned() ? 1f : client.InterpolationAlpha);
            var delivered = context with {
                ElapsedTicks = client.EngineTick,
                StepTicks = stepTicks,
                Tick = ((long)client.Tick),
                AccumulatorTicks = ((ulong)(fraction * stepTicks)),
            };

            var produced = root.ProduceFrame(context: in delivered);

            allowance.Passed();
            iterations.ObserveFrame();
            return produced;
        }
    }
}
