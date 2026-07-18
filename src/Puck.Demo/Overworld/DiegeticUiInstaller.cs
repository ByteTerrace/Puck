using Puck.Demo.BindingBar;
using Puck.Demo.DevConsole;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// The demo-side coupling absorber for <see cref="OverworldRenderNode"/>'s <see cref="SdfWorldRenderSpec"/> assembly
/// — that node and <see cref="OverworldFrameSource"/> both sit at their exact CA1506 coupling ceilings, so composition
/// that would otherwise need to name several Demo types lives HERE instead, reached through delegate/static seams
/// whose arities are already in the coupling set (the documented escape hatch: compose elsewhere, wire through a
/// pure seam, add zero new types on either ceilinged class).
/// <para>
/// <see cref="Install"/> is THE DIEGETIC UI (Tier 2) half — the demo-side counterpart of
/// <see cref="SdfWorldRenderSpec.DecorateFrameSource"/>: composes the <see cref="DiegeticUiDirector"/> and wires it
/// into an <see cref="OverworldFrameSource"/>. The director decorates the frame source with the shared glyph atlas
/// (the <c>ISdfFrameSource.GlyphAtlas</c> seam the engine uploads once) and lays the camera-rig action bar; the frame
/// source's own worst-case probe already reserved the bar's envelope. Only the overworld path (an
/// <see cref="OverworldFrameSource"/> + a live binding-bar store) gets it — the document-driven render path
/// (<c>GraphBuilder</c>) has no binding bar to mirror and passes no <see cref="SdfWorldRenderSpec.DecorateFrameSource"/>
/// at all.
/// </para>
/// <para>
/// <see cref="ResolveTimingToggles"/> is unrelated in subject (it forwards the host config's RayQuery/Timing
/// toggles) but shares the same reason for living here: it is the one place that names <c>HostSettings</c> so the
/// ceilinged render node never has to.
/// </para>
/// </summary>
internal static class DiegeticUiInstaller {
    /// <summary>Resolves the host config's ray-query / host.timing toggles (registered as a singleton by
    /// <c>HostSettings.Apply</c>), for threading into <see cref="SdfWorldRenderSpec.RayQuery"/>/
    /// <see cref="SdfWorldRenderSpec.Timing"/>. Both null when no host config is registered — <c>SdfEngineNode</c>
    /// then falls back to the PUCK_RAY_QUERY environment read for ray-query, and leaves GPU timing disarmed until it is
    /// armed live (the gpu.timing switch / the world.timing verb).</summary>
    /// <param name="services">The application service provider.</param>
    /// <returns>The resolved toggles.</returns>
    public static (bool? RayQuery, bool? Timing) ResolveTimingToggles(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(services);

        var hostSettings = (services.GetService(serviceType: typeof(HostSettings)) as HostSettings);

        return (hostSettings?.RayQuery, hostSettings?.Timing);
    }

    /// <summary>Installs the diegetic UI onto <paramref name="frameSource"/> when it is an
    /// <see cref="OverworldFrameSource"/> with a resolvable <see cref="BindingBarStore"/>; returns
    /// <paramref name="frameSource"/> unchanged otherwise (matching the pre-seam guard behavior).</summary>
    /// <param name="services">The application service provider (resolves the binding-bar store and the console text store).</param>
    /// <param name="frameSource">The frame source to decorate.</param>
    /// <returns>The director wrapping <paramref name="frameSource"/>, or <paramref name="frameSource"/> itself when the cast/store resolution fails.</returns>
    public static ISdfFrameSource Install(IServiceProvider services, ISdfFrameSource frameSource) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameSource);

        if ((frameSource is not OverworldFrameSource overworldFrameSource)
            || (services.GetService(serviceType: typeof(BindingBarStore)) is not BindingBarStore bindingBarStore)) {
            return frameSource;
        }

        var director = new DiegeticUiDirector(inner: overworldFrameSource, barSource: bindingBarStore);

        overworldFrameSource.InstallDiegeticUi(
            bindSlotBase: director.BindSlotBase,
            emit: director.Emit,
            mount: director.Mount,
            signature: () => director.ContentSignature
        );
        // The embossed terminal nameplate reads the terminal's live world frame (it moves when a world loads).
        director.SetTerminalNameplate(
            centre: overworldFrameSource.TerminalNameplateCentre,
            right: overworldFrameSource.TerminalNameplateRight,
            up: overworldFrameSource.TerminalNameplateUp
        );
        // The town's engraved/embossed façade signage lays out against the SAME atlas (one atlas + one TextLayout):
        // bind the director's font onto the world renderer so a placed creation's text runs sample the exact atlas
        // the engine uploads through ISdfFrameSource.GlyphAtlas.
        overworldFrameSource.SetWorldGlyphAtlas(font: director.Font);

        // The diegetic terminal's CELL-GRID text mode: bind its screen slot to a glyph decal composed over the same
        // console store the overlay/CPU-bitmap feed taps, baked against the shared atlas the director owns.
        director.SetTerminalDecal(
            slot: OverworldFrameSource.TerminalScreenSlot,
            console: (services.GetService(serviceType: typeof(ConsoleTextStore)) as IConsoleTextSource)
        );

        return director;
    }
}
