using Puck.Demo.DevConsole;
using Puck.Demo.Text;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.Demo.Ui;

/// <summary>
/// The 2D overlay decorators' ONE composition seam: resolves every store/shader/atlas dependency from the service
/// provider and wraps an inner producer chain, or returns it untouched (with a loud skip notice) when a precondition
/// is absent. <c>OverworldRenderNode</c> sits at its exact CA1506 class-coupling ceiling, so it references only THIS
/// type for both wraps — the console overlay's construction moved here from the node (paying for the overlay-panels
/// addition with headroom to spare), exactly the "compose the subsystem elsewhere" escape the analyzer playbook
/// prescribes. Vulkan-only, like the decorators themselves (the render builder skips the whole Decorate chain on a
/// Direct3D 12 host).
/// </summary>
internal static class OverlayComposition {
    /// <summary>Wraps <paramref name="inner"/> with the on-screen developer console overlay (open with the backtick
    /// <c>console</c> verb). Its glyph source is the ONE shared SDF atlas (<see cref="ConsoleGlyphAtlas"/> over the
    /// shared pack). Degrades to the bare inner if the console store, the shader loader, or the shared atlas is
    /// unavailable (e.g. a non-Windows host) — the terminal console still works either way.</summary>
    /// <param name="inner">The producer/decorator chain to wrap.</param>
    /// <param name="services">The application service provider.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <returns>The wrapped chain, or <paramref name="inner"/> when composition is unavailable.</returns>
    public static IRenderNode WrapConsole(IRenderNode inner, IServiceProvider services, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(services);

        if ((services.GetService(serviceType: typeof(ConsoleTextStore)) is not ConsoleTextStore store) ||
            (services.GetService(serviceType: typeof(IShaderModuleLoader)) is not IShaderModuleLoader shaderLoader) ||
            (ConsoleGlyphAtlas.TryCreate() is not { } font)) {
            // Loud degrade, mirroring the binding-bar decorator's skip notice — a silently missing panel reads as a
            // rendering bug, and which precondition failed is the first diagnostic question.
            Console.Error.WriteLine(value: $"[console-overlay] skipped: store={(services.GetService(serviceType: typeof(ConsoleTextStore)) is not null)} shaders={(services.GetService(serviceType: typeof(IShaderModuleLoader)) is not null)} atlas={(ConsoleGlyphAtlas.TryCreate() is not null)}");

            return inner;
        }

        return new ConsoleOverlayNode(
            appServices: services,
            font: font,
            fragmentBytecode: SdfParityProducers.LoadShader(directory: DemoShaders.OverlayDirectory, fileName: "console-overlay.frag.spv", loader: shaderLoader, stage: ShaderStage.Fragment),
            height: height,
            inner: inner,
            services: SdfParityProducers.BuildVulkanServices(serviceProvider: services, width: width, height: height),
            source: store,
            vertexBytecode: SdfParityProducers.LoadShader(directory: DemoShaders.SdfDirectory, fileName: "fullscreen.vert.spv", loader: shaderLoader, stage: ShaderStage.Vertex),
            width: width
        );
    }

    /// <summary>Wraps <paramref name="inner"/> with the overlay-panels decorator (the toast/hub/tracker/plaque
    /// surfaces — see <see cref="OverlayPanelsNode"/>), composing its stores, the per-frame
    /// <see cref="Overworld.OverlayPanelsFeed"/>, the shared glyph pack, and its shaders. Degrades to the bare inner
    /// with a skip notice when a precondition is absent.</summary>
    /// <param name="inner">The producer/decorator chain to wrap.</param>
    /// <param name="services">The application service provider.</param>
    /// <param name="frameSource">The live overworld frame source (the hub/gallery primitives the feed reads).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <returns>The wrapped chain, or <paramref name="inner"/> when composition is unavailable.</returns>
    public static IRenderNode WrapPanels(IRenderNode inner, IServiceProvider services, Overworld.OverworldFrameSource frameSource, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(frameSource);
        ArgumentNullException.ThrowIfNull(services);

        var toastStore = (services.GetService(serviceType: typeof(ToastStore)) as ToastStore);
        var hubStore = (services.GetService(serviceType: typeof(HubPanelStore)) as HubPanelStore);
        var trackerStore = (services.GetService(serviceType: typeof(TrackerPanelStore)) as TrackerPanelStore);
        var galleryStore = (services.GetService(serviceType: typeof(GalleryPanelStore)) as GalleryPanelStore);
        var controlStore = (services.GetService(serviceType: typeof(OverlayPanelsControlStore)) as OverlayPanelsControlStore);
        var shaderLoader = (services.GetService(serviceType: typeof(IShaderModuleLoader)) as IShaderModuleLoader);
        var glyphs = SharedGlyphSdfPack.TryCreate();

        if ((toastStore is null) || (hubStore is null) || (trackerStore is null) || (galleryStore is null) || (controlStore is null) || (shaderLoader is null) || (glyphs is null)) {
            Console.Error.WriteLine(value: $"[overlay-panels] skipped: stores={((toastStore is not null) && (hubStore is not null) && (trackerStore is not null) && (galleryStore is not null) && (controlStore is not null))} shaders={(shaderLoader is not null)} atlas={(glyphs is not null)}");

            return inner;
        }

        var feed = new Overworld.OverlayPanelsFeed(
            frameSource: frameSource,
            gallery: galleryStore,
            hub: hubStore,
            services: services,
            tracker: trackerStore
        );

        return new OverlayPanelsNode(
            fragmentBytecode: SdfParityProducers.LoadShader(directory: DemoShaders.OverlayDirectory, fileName: "ui-panels-overlay.frag.spv", loader: shaderLoader, stage: ShaderStage.Fragment),
            glyphs: glyphs,
            height: height,
            inner: inner,
            services: SdfParityProducers.BuildVulkanServices(serviceProvider: services, width: width, height: height),
            sources: new OverlayPanelsSources(
                Console: (services.GetService(serviceType: typeof(ConsoleTextStore)) as IConsoleTextSource),
                Control: controlStore,
                FeedTick: feed.Tick,
                Gallery: galleryStore,
                Hub: hubStore,
                Pointer: (services.GetService(serviceType: typeof(PointerStore)) as IPointerSource),
                Toast: toastStore,
                Tracker: trackerStore
            ),
            vertexBytecode: SdfParityProducers.LoadShader(directory: DemoShaders.SdfDirectory, fileName: "fullscreen.vert.spv", loader: shaderLoader, stage: ShaderStage.Vertex),
            width: width
        );
    }
}
