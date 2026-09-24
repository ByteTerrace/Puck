using Puck.Launcher;

namespace Puck.World.Client;

public static partial class WorldSessionLevers {
    /// <summary>Folds the live presentation levers into their document homes for a <c>world.save</c> snapshot: the
    /// render levers into <c>render</c>, the master volume into <c>audio</c>, the present target into <c>host</c>, and
    /// the primary seat's forced binding-bar visibility into the first <c>bindingOverlays</c> row. The authority half
    /// (<c>Puck.World.Server.WorldSessionCapture</c>) folds first.
    /// <para>A lever folds only into a section the document authors, and a section whose lever agrees with it comes
    /// back as the same instance, so an absent section stays absent and an authored one keeps exactly the members its
    /// author wrote. An absent section resolves to the engine's inert value, which is not a section a saved document
    /// may claim: an absent <c>audio</c> section, for one, resolves to a zero speaker radius the validator refuses in
    /// an authored one.</para></summary>
    /// <param name="definition">The definition to fold into, normally the authority half's snapshot.</param>
    /// <param name="settings">The live render-lever settings.</param>
    /// <param name="pacing">The live present-rate control.</param>
    /// <param name="audio">The audio director owning the master-volume lever.</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility overrides.</param>
    /// <returns>The folded definition.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldDefinition Fold(WorldDefinition definition, WorldRenderSettings settings, PresentPacingControl pacing, IWorldAudioLever audio, WorldBindingBarVisibility bindingBar) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: settings);
        ArgumentNullException.ThrowIfNull(argument: pacing);
        ArgumentNullException.ThrowIfNull(argument: audio);
        ArgumentNullException.ThrowIfNull(argument: bindingBar);

        return (definition with {
            RenderRaw = FoldRender(
                render: definition.RenderRaw,
                settings: settings
            ),
            AudioRaw = FoldAudio(
                audio: audio,
                authored: definition.AudioRaw
            ),
            HostRaw = FoldHost(
                host: definition.HostRaw,
                pacing: pacing
            ),
            BindingOverlaysRaw = FoldBindingOverlays(
                overlays: definition.BindingOverlaysRaw,
                visibility: bindingBar
            ),
        });
    }

    private static WorldAudioDefaults? FoldAudio(WorldAudioDefaults? authored, IWorldAudioLever audio) => (
        ((authored is not null) && (audio.SessionMasterVolume is { } volume) && (volume != authored.MasterGain))
        ? (authored with { MasterGain = volume })
        : authored
    );
    // The lever is per-seat and the document has exactly one world-scoped bar row, so the primary local seat (slot 0,
    // player 1) is the seat that folds; the other seats' overrides have no document home and stay live-only.
    private static IReadOnlyList<WorldBindingOverlay>? FoldBindingOverlays(IReadOnlyList<WorldBindingOverlay>? overlays, WorldBindingBarVisibility visibility) {
        if (
            (overlays is not { Count: > 0 }) ||
            (overlays[0]?.BindingBar is not { } bar) ||
            (visibility.Override(slot: 0) is not { } forced) ||
            (bar.Enabled == forced)
        ) {
            return overlays;
        }

        var captured = new List<WorldBindingOverlay>(collection: overlays);

        captured[0] = (overlays[0] with { BindingBar = (bar with { Enabled = forced }) });

        return captured;
    }
    private static WorldHostDefaults? FoldHost(WorldHostDefaults? host, PresentPacingControl pacing) => (
        ((host is not null) && (host.TargetHertz != pacing.TargetHertz))
        ? (host with { TargetHertz = pacing.TargetHertz })
        : host
    );
    // The continuous shadow reach and render scale quantize back to their tiered document homes, so a lever that
    // still sits on the authored tier folds to an equal section and the authored instance stands.
    private static WorldRenderDefaults? FoldRender(WorldRenderDefaults? render, WorldRenderSettings settings) {
        if (render is null) {
            return null;
        }

        var captured = (render with {
            Shadows = ShadowTiers.Tier(reach: settings.ShadowReach),
            ShadowCrowdRadius = settings.ShadowCrowdRadius,
            AmbientOcclusion = settings.AmbientOcclusion,
            RenderScale = WorldRenderScaleTiers.Nearest(scale: settings.RenderScale),
            UpscaleSharpness = settings.UpscaleSharpness,
        });

        return ((captured == render)
            ? render
            : captured
        );
    }
}
