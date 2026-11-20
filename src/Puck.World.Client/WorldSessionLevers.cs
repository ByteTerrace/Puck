using Puck.Commands;
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
    /// <summary>The ray-query toggle for the indirect volume's background rays (<c>world.rays</c>), folding into
    /// <c>render</c>.</summary>
    public const string RayQueries = "rays";
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
    /// <summary>The levels of detail the raster passes draw (<c>world.detail</c>), a <see cref="SdfMeshDetailLevel"/>.</summary>
    public const string Detail = "detail";
    /// <summary>The exposure pin (<c>world.exposure pin</c>): <see cref="WorldSessionLever.B"/> nonzero pins every view at
    /// the <c>EV100</c> in <see cref="WorldSessionLever.A"/>, zero releases the pin.</summary>
    public const string ExposurePin = "exposure.pin";
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
    /// <summary>The distant shadow map: its reach in metres in lane A (zero for none), its texels a side in lane B, the
    /// light turn a held map tolerates, in degrees, in lane C, and the frames a redraw is spread over in lane D.</summary>
    public const string DistantShadows = "distant-shadows";
    /// <summary>The far shadow cascade's rate: its refreshes a second in lane A (zero for every frame, the unauthored
    /// default) and the light turn a held cascade tolerates, in degrees, in lane B.</summary>
    public const string FarShadows = "far-shadows";
    /// <summary>The maximum shading footprint in A and the classification tile edge in B.</summary>
    public const string ViewsRate = "views-rate";
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
    /// <summary>The authored shading measurement path.</summary>
    public const string ShadePrograms = "shadePrograms";
    /// <summary>The world's default shading program override.</summary>
    public const string ShadeDefault = "shadeDefault";
    /// <summary>Camera bloom enable override, negative restoring authored settings.</summary>
    public const string Bloom = "bloom";
    /// <summary>The per-view optical cost gate, negative restoring the preset.</summary>
    public const string BloomQuality = "bloom-quality";
    /// <summary>The named seat's accessibility scale.</summary>
    public const string BloomScale = "bloom-scale";
    /// <summary>The upscale sharpness (<c>world.upscale-sharpness</c>).</summary>
    public const string UpscaleSharpness = "upscale-sharpness";

    /// <summary>The values <c>world.sky-field-scale</c> takes: the verb refuses any other word with this form, and the
    /// knob's setter refuses any other lever value with the same refusal.</summary>
    public static readonly CommandForms SkyFieldScaleForms = CommandForms.Of(verb: "world.sky-field-scale", "[1|0.5]");

    private static bool Flag(WorldSessionLever lever) => (lever.A != 0.0);

    /// <summary>Creates the one lever that installs a preset's complete shadow policy.</summary>
    /// <param name="preset">The authored quality row.</param>
    /// <returns>The four shadow fields in one accepted settings write.</returns>
    public static WorldSessionLever ShadowPolicy(WorldQualityPreset preset) => new(
        Section: WorldSection.Render, Name: ShadowSlots,
        A: preset.ShadowLights, B: preset.ShadowFadeSlots,
        C: preset.ShadowFadeTicks, D: ((int)preset.ShadowOverflow));
    /// <summary>Creates the one lever that installs a preset's distant shadow map, or removes it for a preset that authors
    /// none.</summary>
    /// <param name="preset">The authored quality row.</param>
    /// <returns>The map's reach, texels and tolerance in one accepted settings write.</returns>
    public static WorldSessionLever DistantShadowPolicy(WorldQualityPreset preset) => new(
        Section: WorldSection.Render, Name: DistantShadows,
        A: (preset.DistantShadows?.Reach ?? 0f), B: (preset.DistantShadows?.Texels ?? 0),
        C: (preset.DistantShadows?.TurnDegrees ?? 0f), D: (preset.DistantShadows?.Frames ?? 1));
    /// <summary>Creates the one lever that installs a preset's far cascade rate, or restores the every-frame default for a
    /// preset that authors none.</summary>
    /// <param name="preset">The authored quality row.</param>
    /// <returns>The rate and tolerance in one accepted settings write.</returns>
    public static WorldSessionLever FarShadowPolicy(WorldQualityPreset preset) => new(
        Section: WorldSection.Render, Name: FarShadows,
        A: (preset.FarShadows?.Hertz ?? 0), B: (preset.FarShadows?.TurnDegrees ?? 0f));
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
        sink.Register(name: RayQueries, setter: lever => settings.RayQueries = Flag(lever: lever));
        sink.Register(
            name: CadenceGate,
            setter: lever => settings.CadenceGate = Flag(lever: lever)
        );
        sink.Register(
            name: Cull,
            setter: lever => settings.Cull = ((SdfMeshCullLevel)((int)lever.A))
        );
        sink.Register(
            name: Detail,
            setter: lever => settings.Detail = ((SdfMeshDetailLevel)((int)lever.A))
        );
        sink.Register(
            name: Rates,
            setter: lever => settings.UpdateRates = ((SdfUpdateRates)((int)lever.A)) & SdfUpdateRates.All
        );
        sink.Register(
            name: Indirect,
            setter: lever => settings.IndirectTier = ((SdfIndirectTier)((int)lever.A))
        );
        sink.Register(
            name: ExposurePin,
            setter: lever => {
                // The value arrives off the wire, so it is range-checked here rather than thrown through delivery.
                if ((lever.B != 0d) && (!double.IsFinite(d: lever.A) || (lever.A < SdfPhotometry.MinimumEv100) || (lever.A > SdfPhotometry.MaximumEv100))) {
                    WorldClientDiagnostics.Report(channel: "world.exposure", text: $"pin {lever.A.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} is outside {SdfPhotometry.MinimumEv100}..{SdfPhotometry.MaximumEv100} EV100 — lever dropped");
                    return;
                }
                settings.ExposurePin = ((lever.B != 0d) ? ((float)lever.A) : null);
            }
        );
        sink.Register(
            name: RenderScale,
            setter: lever => {
                if (!settings.SetResolution((lever.View ?? "*"), ((WorldRenderScaleOperation)((int)lever.B)), ((float)lever.A), out var refusal)) {
                    WorldClientDiagnostics.Report(channel: "world.render-scale", text: refusal!);
                }
            }
        );
        sink.Register(
            name: Temporal,
            setter: lever => settings.Temporal = Flag(lever: lever)
        );
        sink.Register(name: Toon, setter: lever => settings.Toon = Flag(lever: lever));
        sink.Register(name: ShadePrograms, setter: lever => settings.ShadePrograms = Flag(lever: lever));
        sink.Register(name: ShadeDefault, setter: lever => settings.SetShadeDefault(lever.View));
        sink.Register(name: Bloom, setter: lever => settings.BloomOverride = ((lever.A < 0) ? null : Flag(lever: lever)));
        sink.Register(name: BloomScale, setter: lever => settings.SetBloomScale(lever.Seat, ((float)lever.A)));
        sink.Register(name: BloomQuality, setter: lever => {
            if (lever.View is { } view) { settings.SetBloomQuality(view, ((lever.A < 0) ? null : (Puck.Hosting.BloomQuality)((int)lever.A))); } else { settings.BloomQuality = ((Puck.Hosting.BloomQuality)((int)lever.A)); }
        });
        sink.Register(name: SkyFieldScale, setter: lever => {
            if (lever.A is not (1d or .5d)) {
                WorldClientDiagnostics.Report(channel: "world.sky-field-scale", text: $"{lever.A.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} is not a form of {SkyFieldScaleForms.Spelling} — lever dropped");
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
                    WorldClientDiagnostics.Report(channel: "world.binding-bar", text: $"seat {lever.Seat} is outside 0..{(PlayerRoster.MaxSlots - 1)} — lever dropped");

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
        sink.Register(name: DistantShadows, setter: lever => {
            if (lever.A <= 0d) {
                settings.DistantShadows = null;
                return;
            }
            // The values arrive off the wire, so they are judged here as the document validator judges an authored map.
            var map = new WorldDistantShadows(Reach: ((float)lever.A), Texels: ((int)lever.B), TurnDegrees: ((float)lever.C), Frames: ((int)lever.D));

            if (map.Refusal(path: "distant-shadows") is { } refusal) {
                WorldClientDiagnostics.Report(channel: "world.quality", text: $"{refusal} — lever dropped");
                return;
            }
            settings.DistantShadows = map;
        });
        sink.Register(name: FarShadows, setter: lever => {
            if (lever.A <= 0d) {
                settings.FarShadows = null;
                return;
            }
            // The values arrive off the wire, so they are judged here as the document validator judges an authored row.
            var rate = new WorldFarShadows(Hertz: ((int)lever.A), TurnDegrees: ((float)lever.B));

            if (rate.Refusal(path: "far-shadows") is { } refusal) {
                WorldClientDiagnostics.Report(channel: "world.quality", text: $"{refusal} — lever dropped");
                return;
            }
            settings.FarShadows = rate;
        });
        sink.Register(name: ViewsRate, setter: lever => {
            var rate = new SdfViewsRate(MaxRate: (int)lever.A, TileSize: (int)lever.B);
            if (rate.Refusal(path: ViewsRate) is { } refusal) {
                WorldClientDiagnostics.Report(channel: "world.quality", text: $"{refusal} — lever dropped");
                return;
            }
            settings.ViewsRate = rate;
        });
        return sink;
    }
}
