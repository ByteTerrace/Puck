using Puck.Launcher;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The session-lever knob vocabulary and its composition-time registration: one token per <c>world.&lt;knob&gt;</c>
/// verb, each bound to the live presentation service that knob writes.
/// </summary>
/// <remarks>The tokens are the verb names without their <c>world.</c> prefix, so a reader who knows the verb knows the
/// wire name. A verb submits <see cref="WorldSessionLever"/> carrying one of these; nothing else may.</remarks>
public static partial class WorldSessionLevers {
    /// <summary>Sky solo row, -1 to restore all rows.</summary>
    public const string SkySolo = "sky-layer.solo";
    /// <summary>Sky mute row in A and on/off in B.</summary>
    public const string SkyMute = "sky-layer.mute";
    /// <summary>The ambient-occlusion toggle (<c>world.ao</c>), folding into <c>render</c>.</summary>
    public const string AmbientOcclusion = "ao";
    /// <summary>One seat's on-screen binding-bar visibility (<c>world.binding-bar</c>), folding into
    /// <c>bindings</c>. Seat-scoped: <see cref="WorldSessionLever.Seat"/> names the 0-based local seat.</summary>
    public const string BindingBar = "binding-bar";
    /// <summary>The <see cref="WorldSessionLever.A"/> value <see cref="BindingBar"/> carries to clear a seat's
    /// override and return it to authored behavior; a non-negative value forces the bar off (0) or on (1).</summary>
    public const double BindingBarAuto = -1.0;
    /// <summary>The unchanged-frame cadence gate (<c>world.cadence</c>).</summary>
    public const string CadenceGate = "cadence";
    /// <summary>The raster passes' cull of mesh draws they cannot show (<c>world.cull</c>).</summary>
    public const string Cull = "cull";
    /// <summary>The indirect-light tier the irradiance volume traces at (<c>world.indirect</c>).</summary>
    public const string Indirect = "indirect";
    /// <summary>The audio mix master gain (<c>world.volume</c>), folding into <c>audio</c>.</summary>
    public const string MasterVolume = "volume";
    /// <summary>Which work updates less often when far or small on screen, a <see cref="SdfUpdateRates"/> in
    /// <see cref="WorldSessionLever.A"/> (<c>world.rates</c>).</summary>
    public const string Rates = "rates";
    /// <summary>The render scale (<c>world.render-scale</c>).</summary>
    public const string RenderScale = "render-scale";
    /// <summary>The shadow tier, a <see cref="ShadowTier"/> in <see cref="WorldSessionLever.A"/> (<c>world.shadows</c>).</summary>
    public const string Shadows = "shadows";
    /// <summary>The complete shadow policy: stable slots, fade slots, fade ticks and overflow in lanes A through D.</summary>
    public const string ShadowSlots = "shadow-slots";
    /// <summary>The sky's quality tier ordinal (<c>world.sky-quality</c>), a <see cref="WorldSkyTier"/>.</summary>
    public const string SkyQuality = "sky-quality";
    /// <summary>The independent sky field grid fraction (<c>world.sky-field-scale</c>), one or one half.</summary>
    public const string SkyFieldScale = "sky-field-scale";
    /// <summary>The target present rate in Hz, 0 meaning automatic display pacing (<c>world.target</c>), folding into
    /// <c>host</c>.</summary>
    public const string TargetHertz = "target";
    /// <summary>Temporal reconstruction of the world's own views (<c>world.temporal</c>).</summary>
    public const string Temporal = "temporal";
    /// <summary>The presentation-only cel-shading choice (<c>world.toon</c>).</summary>
    public const string Toon = "toon";
    /// <summary>The upscale sharpness (<c>world.upscale-sharpness</c>).</summary>
    public const string UpscaleSharpness = "upscale-sharpness";

    private static bool Flag(WorldSessionLever lever) => (lever.A != 0.0);

    /// <summary>Creates the one lever that installs a preset's complete shadow policy.</summary>
    /// <param name="preset">The authored quality row.</param>
    /// <returns>The four shadow fields in one accepted settings write.</returns>
    public static WorldSessionLever ShadowPolicy(WorldQualityPreset preset) => new(
        Section: WorldSection.Render, Name: ShadowSlots,
        A: preset.ShadowLights, B: preset.ShadowFadeSlots,
        C: preset.ShadowFadeTicks, D: ((int)preset.ShadowOverflow));
    /// <summary>Composes the applier every shipped knob is registered on.</summary>
    /// <param name="settings">The live render-lever settings the frame source reads.</param>
    /// <param name="pacing">The live present-rate control the window pump reads.</param>
    /// <param name="audio">The audio director owning the master-volume knob.</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility overrides.</param>
    /// <returns>The composed applier.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static WorldSessionLeverSink Compose(WorldRenderSettings settings, PresentPacingControl pacing, IWorldAudioLever audio, WorldBindingBarVisibility bindingBar) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pacing);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(bindingBar);

        var sink = new WorldSessionLeverSink();

        sink.Register(name: SkySolo, setter: lever => settings.SkyLayers.SetSolo(index: ((int)lever.A)));
        sink.Register(name: SkyMute, setter: lever => settings.SkyLayers.SetMuted(index: ((int)lever.A), muted: (lever.B != 0d)));

        sink.Register(
            name: MasterVolume,
            setter: lever => audio.SetMasterVolume(value: ((float)lever.A))
        );
        sink.Register(
            name: Shadows,
            setter: lever => settings.Shadows = ((ShadowTier)((int)lever.A))
        );
        sink.Register(
            name: AmbientOcclusion,
            setter: lever => settings.AmbientOcclusion = Flag(lever: lever)
        );
        sink.Register(
            name: CadenceGate,
            setter: lever => settings.CadenceGate = Flag(lever: lever)
        );
        sink.Register(
            name: Cull,
            setter: lever => settings.Cull = ((SdfMeshCullLevel)((int)lever.A))
        );
        sink.Register(
            name: Rates,
            setter: lever => settings.UpdateRates = (((SdfUpdateRates)((int)lever.A)) & SdfUpdateRates.All)
        );
        sink.Register(
            name: Indirect,
            setter: lever => settings.IndirectTier = ((SdfIndirectTier)((int)lever.A))
        );
        sink.Register(
            name: RenderScale,
            setter: lever => {
                if (!settings.SetResolution((lever.View ?? "*"), ((WorldRenderScaleOperation)((int)lever.B)), ((float)lever.A), out var refusal)) {
                    Console.Error.WriteLine(value: $"[{refusal}]");
                }
            }
        );
        sink.Register(
            name: Temporal,
            setter: lever => settings.Temporal = Flag(lever: lever)
        );
        sink.Register(name: Toon, setter: lever => settings.Toon = Flag(lever: lever));
        sink.Register(name: SkyFieldScale, setter: lever => {
            if (lever.A is not (1d or .5d)) {
                Console.Error.WriteLine(value: "[world.sky-field-scale: expected 1|0.5 — lever dropped]");
                return;
            }
            settings.SkyFieldScale = ((float)lever.A);
        });
        sink.Register(
            name: SkyQuality,
            setter: lever => settings.SkyQuality = (((int)lever.A) switch {
                0 => WorldSkyTier.Low,
                1 => WorldSkyTier.Medium,
                _ => WorldSkyTier.High,
            })
        );
        sink.Register(
            name: UpscaleSharpness,
            setter: lever => settings.UpscaleSharpness = ((float)lever.A)
        );
        sink.Register(
            name: TargetHertz,
            setter: lever => pacing.SetTargetHertz(targetHertz: lever.A)
        );
        sink.Register(
            name: BindingBar,
            setter: lever => {
                // The seat arrives off the wire, so it is range-checked here rather than thrown through the
                // delivery path a remote-accepted lever reaches this sink on.
                if (
                    (lever.Seat < 0) ||
                    (lever.Seat >= PlayerRoster.MaxSlots)
                ) {
                    Console.Error.WriteLine(value: $"[world.binding-bar: seat {lever.Seat} is outside 0..{(PlayerRoster.MaxSlots - 1)} — lever dropped]");

                    return;
                }

                bindingBar.SetOverride(
                    slot: lever.Seat,
                    visible: ((lever.A < 0.0)
                    ? null
                    : ((bool?)Flag(lever: lever)))
                );
            }
        );

        sink.Register(name: ShadowSlots, setter: lever => settings.ShadowSlots = new WorldShadowSettings(
            Slots: ((int)lever.A), FadeSlots: ((int)lever.B), FadeTicks: ((uint)lever.C), Overflow: ((WorldShadowOverflow)((int)lever.D))));
        return sink;
    }
}
