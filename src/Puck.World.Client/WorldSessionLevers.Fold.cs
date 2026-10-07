using Puck.Launcher;

namespace Puck.World.Client;

public static partial class WorldSessionLevers {
    /// <summary>Folds the live presentation levers into their document homes for a <c>world.save</c> snapshot: the
    /// render levers into <c>render</c>, view ceilings and floors into <c>views.quality</c>, the master volume into
    /// <c>audio</c>, the present target into <c>host</c>, the
    /// primary seat's forced binding-bar visibility into the first <c>bindingOverlays</c> row, and the primary seat's
    /// moved grid and snapping into <c>editor</c>. The authority half (<c>Puck.World.Server.WorldSessionCapture</c>)
    /// folds first.
    /// <para>A lever folds only into a section the document authors, and a section whose lever agrees with it comes
    /// back as the same instance, so an absent section stays absent and an authored one keeps exactly the members its
    /// author wrote. Some absent sections resolve to values a saved authored section cannot claim:
    /// an absent <c>audio</c> section, for one, resolves to a zero speaker radius the validator refuses in
    /// an authored one. Moved render ceilings, view quality and editor values create their valid section when absent;
    /// moved indirect tiers also create their section, preserving its source and receiver controls;
    /// unchanged defaults leave absent sections absent. Pins never fold.</para></summary>
    /// <param name="definition">The definition to fold into, normally the authority half's snapshot.</param>
    /// <param name="settings">The live render-lever settings.</param>
    /// <param name="pacing">The live present-rate control.</param>
    /// <param name="audio">The audio director owning the master-volume lever.</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility overrides.</param>
    /// <param name="editor">The live per-seat editor state.</param>
    /// <returns>The folded definition.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldDefinition Fold(WorldDefinition definition, WorldRenderSettings settings, PresentPacingControl pacing, IWorldAudioLever audio, WorldBindingBarVisibility bindingBar, WorldEditorSeats editor) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: settings);
        ArgumentNullException.ThrowIfNull(argument: pacing);
        ArgumentNullException.ThrowIfNull(argument: audio);
        ArgumentNullException.ThrowIfNull(argument: bindingBar);
        ArgumentNullException.ThrowIfNull(argument: editor);

        return (definition with {
            RenderRaw = FoldRender(
                render: definition.RenderRaw,
                settings: settings
            ),
            ViewsRaw = settings.FoldQuality(views: definition.ViewsRaw),
            AudioRaw = FoldAudio(
                audio: audio,
                authored: definition.AudioRaw
            ),
            HostRaw = FoldHost(
                host: definition.HostRaw,
                pacing: pacing
            ),
            EditorRaw = editor.Fold(authored: definition.EditorRaw),
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
    // Shadow reach folds to its tier; scalar ceilings survive without quantization.
    private static WorldRenderDefaults? FoldRender(WorldRenderDefaults? render, WorldRenderSettings settings) {
        if ((render is null) && (settings.RenderScale == WorldRenderDefaults.Absent.RenderScale) &&
            (settings.IndirectTier == Puck.SignedDistance.SdfIndirectTier.Medium) && (settings.SkyFieldScale == 1f)) {
            return null;
        }

        render ??= WorldRenderDefaults.Absent;

        var captured = (render with {
            Indirect = (((render.Indirect?.Tier ?? Puck.SignedDistance.SdfIndirectTier.Medium) == settings.IndirectTier)
                ? render.Indirect : (render.Indirect ?? new WorldRenderIndirect()) with { Tier = settings.IndirectTier }),
            Shadows = ShadowTiers.Tier(reach: settings.ShadowReach),
            ShadowLights = settings.ShadowSlots.Slots,
            ShadowFadeSlots = settings.ShadowSlots.FadeSlots,
            ShadowFadeTicks = ((uint)settings.ShadowSlots.FadeTicks),
            ShadowOverflow = settings.ShadowSlots.Overflow,
            ShadowCrowdRadius = settings.ShadowCrowdRadius,
            AmbientOcclusion = settings.AmbientOcclusion,
            RenderScale = settings.RenderScale,
            UpscaleSharpness = settings.UpscaleSharpness,
            Temporal = settings.Temporal,
            ShadowAmortize = settings.ShadowAmortize,
            SkyQuality = settings.SkyQuality,
            SkyFieldScale = settings.SkyFieldScale,
            DynamicResolution = settings.DynamicResolution,
        });

        return ((captured == render)
            ? render
            : captured
        );
    }
}
