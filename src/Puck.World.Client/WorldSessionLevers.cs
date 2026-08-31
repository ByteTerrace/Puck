using Puck.Launcher;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The session-lever knob vocabulary and its composition-time registration: one token per <c>world.&lt;knob&gt;</c>
/// verb, each bound to the live presentation service that knob writes.
/// </summary>
/// <remarks>The tokens are the verb names without their <c>world.</c> prefix, so a reader who knows the verb knows the
/// wire name. A verb submits <see cref="WorldSessionLever"/> carrying one of these; nothing else may.</remarks>
public static class WorldSessionLevers {
    /// <summary>The <see cref="WorldSessionLever.A"/> value <see cref="BindingBar"/> carries to clear a seat's
    /// override and return it to authored behavior; a non-negative value forces the bar off (0) or on (1).</summary>
    public const double BindingBarAuto = -1.0;

    /// <summary>The ambient-occlusion toggle (<c>world.ao</c>), folding into <c>render</c>.</summary>
    public const string AmbientOcclusion = "ao";
    /// <summary>The ambient-occlusion quality tier ordinal (<c>world.ao-quality</c>).</summary>
    public const string AmbientOcclusionQuality = "ao-quality";
    /// <summary>One seat's on-screen binding-bar visibility (<c>world.binding-bar</c>), folding into
    /// <c>bindings</c>. Seat-scoped: <see cref="WorldSessionLever.Seat"/> names the 0-based local seat.</summary>
    public const string BindingBar = "binding-bar";
    /// <summary>The per-tile far-bound cull (<c>world.far-field bound</c>).</summary>
    public const string FarBound = "far-field.bound";
    /// <summary>Whether a seat's engaged instrument is treated as the session's reference clock
    /// (<c>world.instrument-clock</c>), folding into <c>audio</c>. Presentation echo only — see
    /// <see cref="IWorldInstrumentClockLever"/>'s own remarks for the simulation-side gate this is NOT.
    /// Seat-scoped: <see cref="WorldSessionLever.Seat"/> names the 0-based local seat.</summary>
    public const string InstrumentClock = "instrument-clock";
    /// <summary>The audio mix master gain (<c>world.volume</c>), folding into <c>audio</c>.</summary>
    public const string MasterVolume = "volume";
    /// <summary>The render scale (<c>world.render-scale</c>).</summary>
    public const string RenderScale = "render-scale";
    /// <summary>The shadow accumulation toggle (<c>world.shadow.accumulate</c>).</summary>
    public const string ShadowAccumulation = "shadow.accumulate";
    /// <summary>The soft-shadow light-side early exit (<c>world.far-field shadow</c>).</summary>
    public const string ShadowFarExit = "far-field.shadow";
    /// <summary>The shadow march tier ordinal (<c>world.shadow-march</c>).</summary>
    public const string ShadowMarch = "shadow-march";
    /// <summary>The shadow mask tier ordinal (<c>world.shadow-mask</c>).</summary>
    public const string ShadowMask = "shadow-mask";
    /// <summary>Soft-shadow reach in <see cref="WorldSessionLever.A"/> and crowd radius in
    /// <see cref="WorldSessionLever.B"/> (<c>world.shadows</c>).</summary>
    public const string Shadows = "shadows";
    /// <summary>The target present rate in Hz, 0 meaning automatic display pacing (<c>world.target</c>), folding into
    /// <c>host</c>.</summary>
    public const string TargetHertz = "target";
    /// <summary>The upscale sharpness (<c>world.upscale-sharpness</c>).</summary>
    public const string UpscaleSharpness = "upscale-sharpness";

    private static bool Flag(WorldSessionLever lever) => (lever.A != 0.0);

    /// <summary>Composes the applier every shipped knob is registered on.</summary>
    /// <param name="settings">The live render-lever settings the frame source reads.</param>
    /// <param name="pacing">The live present-rate control the window pump reads.</param>
    /// <param name="audio">The audio director owning the master-volume knob.</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility overrides.</param>
    /// <param name="instrumentClock">The audio director owning the instrument-clock echo.</param>
    /// <returns>The composed applier.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static WorldSessionLeverSink Compose(WorldRenderSettings settings, PresentPacingControl pacing, IWorldAudioLever audio, WorldBindingBarVisibility bindingBar, IWorldInstrumentClockLever instrumentClock) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pacing);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(bindingBar);
        ArgumentNullException.ThrowIfNull(instrumentClock);

        var sink = new WorldSessionLeverSink();

        sink.Register(
            name: MasterVolume,
            setter: lever => audio.SetMasterVolume(value: ((float)lever.A))
        );
        sink.Register(
            name: InstrumentClock,
            setter: lever => {
                if (
                    (lever.Seat < 0) ||
                    (lever.Seat >= PlayerRoster.MaxSlots)
                ) {
                    Console.Error.WriteLine(value: $"[world.instrument-clock: seat {lever.Seat} is outside 0..{(PlayerRoster.MaxSlots - 1)} — lever dropped]");

                    return;
                }

                instrumentClock.SetInstrumentClockEngaged(
                    engaged: Flag(lever: lever),
                    seat: lever.Seat
                );
            }
        );
        sink.Register(
            name: Shadows,
            setter: lever => {
                settings.ShadowReach = ((float)lever.A);
                settings.ShadowCrowdRadius = ((float)lever.B);
            }
        );
        sink.Register(
            name: AmbientOcclusion,
            setter: lever => settings.AmbientOcclusion = Flag(lever: lever)
        );
        sink.Register(
            name: AmbientOcclusionQuality,
            setter: lever => settings.AmbientOcclusionQuality = ((AmbientOcclusionMode)((int)lever.A))
        );
        sink.Register(
            name: FarBound,
            setter: lever => settings.FarBound = Flag(lever: lever)
        );
        sink.Register(
            name: ShadowFarExit,
            setter: lever => settings.ShadowFarExit = Flag(lever: lever)
        );
        sink.Register(
            name: ShadowAccumulation,
            setter: lever => settings.ShadowAccumulation = Flag(lever: lever)
        );
        sink.Register(
            name: ShadowMask,
            setter: lever => settings.ShadowMask = ((ShadowMaskMode)((int)lever.A))
        );
        sink.Register(
            name: ShadowMarch,
            setter: lever => settings.ShadowMarch = ((ShadowMarchMode)((int)lever.A))
        );
        sink.Register(
            name: RenderScale,
            setter: lever => settings.RenderScale = ((float)lever.A)
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
                    : ((bool?)Flag(lever: lever))
                )
                );
            }
        );

        return sink;
    }
}
