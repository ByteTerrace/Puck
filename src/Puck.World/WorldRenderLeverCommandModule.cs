using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The render levers: engine-wide render options any presentation shape honors — shadows, ambient occlusion, the
/// unchanged-frame cadence gate, indirect light, render scale, temporal reconstruction, upscale sharpness, the sky's
/// quality, and the quality preset — each a live console verb that echoes its current value when
/// called with no argument. Every write is a session lever submitted through the server's grant check and lands in
/// <see cref="WorldRenderSettings"/>, which the frame source reads each captured frame, except the SDF debug view
/// view, which affects only live presentation settings.
/// <para>Every verb here is Bindable: a write names no path or document target, and runs under the pressing seat's
/// principal, so the server drops a lever whose section the seat holds no Mutate grant over
/// (<c>WorldServer.ApplySessionLever</c>) exactly as it does for a typed line.</para>
/// Nothing here needs a window or a
/// presenter, so both the windowed and the offscreen presentation shapes compose it, and an offscreen collector or
/// canary can set the same levers a player can. Headless composes no renderer and refuses these as unknown.
/// </summary>
/// <param name="settings">The presentation settings receiving admitted render levers.</param>
/// <param name="server">The authority whose live definition and grants the commands inspect.</param>
/// <param name="link">The existing submission path for authoritative session levers.</param>
/// <param name="renderProbe">The presentation owner's live renderer.</param>
public sealed partial class WorldRenderLeverCommandModule(WorldRenderSettings settings, WorldServer server, IServerLink link, WorldRenderProbe renderProbe) : ICommandModule {
    private static readonly CommandForms WorldSkyLayerForms = CommandForms.Of(verb: "world.sky-layer", "[solo <index>|solo off|mute <index> on|off]");
    private static readonly CommandForms WorldShadowsForms = CommandForms.Of(verb: "world.shadows", "[off|low|medium|high]");
    private static readonly CommandForms WorldAoForms = CommandForms.Of(verb: "world.ao", "[on|off]");
    private static readonly CommandForms WorldCadenceForms = CommandForms.Of(verb: "world.cadence", "[on|off|status]");
    private static readonly CommandForms WorldCullForms = CommandForms.Of(verb: "world.cull", "[occlusion|camera|frustum|off|status]");
    private static readonly CommandForms WorldDetailForms = CommandForms.Of(verb: "world.detail", "[auto|0|1|2|status]");
    private static readonly CommandForms WorldRatesForms = CommandForms.Of(verb: "world.rates", "[status|on|off|toggle]", "<bodies|views|screens|shadow-far> on|off");
    private static readonly CommandForms WorldRenderScaleForms = CommandForms.Of(verb: "world.render-scale", "[<view>]", "[<view>] <tier|fraction|percent>", "[<view>] floor <tier>", "[<view>] pin <scale>", "[<view>] auto [on|off]");
    private static readonly CommandForms WorldUpscaleSharpnessForms = CommandForms.Of(verb: "world.upscale-sharpness", "[bilinear|balanced|sharp|0..1|0%..100%]");
    private static readonly CommandForms WorldTemporalForms = CommandForms.Of(verb: "world.temporal", "[on|off]");
    private static readonly CommandForms WorldSkyQualityForms = CommandForms.Of(verb: "world.sky-quality", "[low|medium|high]");
    private static readonly CommandForms WorldQualityForms = CommandForms.Of(verb: "world.quality", $"[{string.Join(separator: "|", values: QualityTiers.Names)}|next]");
    private static readonly CommandForms WorldToonForms = CommandForms.Of(verb: "world.toon", "[on|off|toggle]");
    private static readonly CommandForms WorldShadeForms = CommandForms.Of(verb: "world.shade", "[programs|layers]", "default <program|document>");
    private static readonly CommandForms WorldIndirectForms = CommandForms.Of(verb: "world.indirect", "[off|medium|high]");
    private static readonly CommandForms WorldExposureForms = CommandForms.Of(verb: "world.exposure", "[pin [<ev100>|off]]");
    private static readonly CommandForms WorldResidencyForms = CommandForms.Of(verb: "world.residency", "[radius]");
    private static readonly CommandForms WorldProbeForms = CommandForms.Of(verb: "world.probe", "<x> <y> <z> [<nx> <ny> <nz>]");
    private static readonly CommandForms WorldSkyFieldScaleForms = WorldSessionLevers.SkyFieldScaleForms;
    // The world.quality echo: the current individual settings the preset (or a later override) left in place.
    private string DescribeQuality() {
        return $"[world.quality: shadows={ShadowTierName(tier: settings.Shadows)} ao={(settings.AmbientOcclusion
            ? "on"
            : "off")} temporal={(settings.Temporal
            ? "on"
            : "off")} dynamic-resolution={(settings.DynamicResolution
            ? "on"
            : "off")} render-scale={RenderScaleName(scale: settings.RenderScale)} upscale={UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)} sky={SkyQualityName(tier: settings.SkyQuality)} sky-field-scale={settings.SkyFieldScale.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)} indirect={settings.IndirectTier.ToString().ToLowerInvariant()}]";
    }
    // A sky tier's spelling, as the document and world.sky-quality spell it.
    private static string SkyQualityName(WorldSkyTier tier) => tier switch {
        WorldSkyTier.Low => "low",
        WorldSkyTier.Medium => "medium",
        _ => "high",
    };
    // The world.sky-quality echo.
    private static string SkyQualityEcho(WorldRenderSettings settings) =>
        $"[world.sky-quality: {SkyQualityName(tier: settings.SkyQuality)}]";
    // The world.rates lever a token names, or null for none.
    private static SdfUpdateRates? RatesLever(ReadOnlySpan<char> token) => token switch {
        "bodies" => SdfUpdateRates.Bodies,
        "views" => SdfUpdateRates.Views,
        "screens" => SdfUpdateRates.Screens,
        "shadow-far" => SdfUpdateRates.ShadowFar,
        _ => null,
    };
    // The world.rates echo: each lever, what each counted so far, then the presentation rate the levers hold by, each
    // tier's longest allowed interval, and each lever's longest achieved interval at each tier over the recent window, in
    // milliseconds ('-' where nothing was rated at that tier).
    private static string RatesEcho(WorldRenderSettings settings) {
        static string On(SdfUpdateRates rates, SdfUpdateRates lever) => (((rates & lever) != 0) ? "on" : "off");

        var rates = settings.UpdateRates;
        var counts = settings.RateCounts;
        var intervals = settings.RateIntervals;
        var rate = intervals.Rate;
        var echo = new System.Text.StringBuilder();

        _ = echo.Append(provider: CultureInfo.InvariantCulture,
            handler: $"[world.rates: bodies={On(lever: SdfUpdateRates.Bodies, rates: rates)} views={On(lever: SdfUpdateRates.Views, rates: rates)} screens={On(lever: SdfUpdateRates.Screens, rates: rates)} shadow-far={On(lever: SdfUpdateRates.ShadowFar, rates: rates)} bodies.posed={counts.Read(kind: SdfUpdateRateCounts.BodiesPosed)} bodies.held={counts.Read(kind: SdfUpdateRateCounts.BodiesHeld)} placements.posed={counts.Read(kind: SdfUpdateRateCounts.PlacementsPosed)} placements.held={counts.Read(kind: SdfUpdateRateCounts.PlacementsHeld)} views.rated={counts.Read(kind: SdfUpdateRateCounts.ViewsRated)} views.held={counts.Read(kind: SdfUpdateRateCounts.ViewsHeld)} screens.rated={counts.Read(kind: SdfUpdateRateCounts.ScreensRated)} shadow-far.rendered={counts.Read(kind: SdfUpdateRateCounts.ShadowFarRendered)} shadow-far.split={counts.Read(kind: SdfUpdateRateCounts.ShadowFarSplit)} shadow-far.held={counts.Read(kind: SdfUpdateRateCounts.ShadowFarHeld)}");
        _ = echo.Append(provider: CultureInfo.InvariantCulture,
            handler: $" display={rate.DisplayHertz}hz grid={((rate.GridHertz > 0d) ? (rate.GridHertz.ToString(format: "0.###", provider: CultureInfo.InvariantCulture) + "hz") : "none")} period-ms:");
        foreach (var tier in SdfUpdateRateIntervals.Tiers) {
            _ = echo.Append(provider: CultureInfo.InvariantCulture, handler: $" {tier}hz={(rate.PeriodSeconds(hertz: tier) * 1000d):0.0}");
        }
        _ = echo.Append(value: " longest-ms:");
        foreach (var lever in SdfUpdateRateIntervals.Levers) {
            foreach (var tier in SdfUpdateRateIntervals.Tiers) {
                // Only bodies take the tiers below 15 Hz, and only the far cascade's own tier records.
                if (
                    ((lever == SdfUpdateRates.ShadowFar) && (tier != SdfUpdateRateTiers.ShadowFarHertz)) ||
                    ((lever != SdfUpdateRates.Bodies) && (tier < 15))
                ) {
                    continue;
                }

                var longest = intervals.Longest(hertz: tier, lever: lever);

                _ = echo.Append(provider: CultureInfo.InvariantCulture, handler: $" {LeverName(lever: lever)}.{tier}hz={((longest is { } seconds) ? (seconds * 1000d).ToString(format: "0.0", provider: CultureInfo.InvariantCulture) : "-")}");
            }
        }

        foreach (var lever in ((ReadOnlySpan<SdfUpdateRates>)[SdfUpdateRates.Bodies, SdfUpdateRates.Views, SdfUpdateRates.Screens])) {
            _ = echo.Append(provider: CultureInfo.InvariantCulture, handler: $" {LeverName(lever: lever)}:");

            var readings = settings.RateReadings.Latest(lever: lever);

            if (readings.Count == 0) {
                _ = echo.Append(value: " -");
            }
            foreach (var reading in readings) {
                _ = ((reading.Hertz == SdfUpdateRateTiers.HeldHertz)
                    ? echo.Append(provider: CultureInfo.InvariantCulture, handler: $" {reading.Name}={ReadingSize(lever: lever, size: reading.Size)}@held")
                    : echo.Append(provider: CultureInfo.InvariantCulture, handler: $" {reading.Name}={ReadingSize(lever: lever, size: reading.Size)}@{reading.Hertz}hz"));
                // A creation every view's sight hid names the latency of the evidence it is rated by, in frames.
                if (reading.SightFrames >= 0) {
                    _ = echo.Append(provider: CultureInfo.InvariantCulture, handler: $"(occluded,latency={reading.SightFrames}f)");
                }
            }
        }

        return echo.Append(value: ']').ToString();
    }
    // A reading's size on screen in its lever's unit: a body's pixels per metre, a view's display pixels high, a screen's
    // height on the display over its image's.
    private static string ReadingSize(SdfUpdateRates lever, double size) => (double.IsPositiveInfinity(d: size)
        ? "near"
        : (lever switch {
            SdfUpdateRates.Bodies => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{size:0.0}px/m"),
            SdfUpdateRates.Views => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{size:0}px"),
            _ => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{size:0.00}x"),
        }));
    // A world.rates lever's spelling, as RatesLever reads it.
    private static string LeverName(SdfUpdateRates lever) => lever switch {
        SdfUpdateRates.Bodies => "bodies",
        SdfUpdateRates.Views => "views",
        SdfUpdateRates.Screens => "screens",
        _ => "shadow-far",
    };
    // The world.cadence echo.
    private static string CadenceEcho(WorldRenderSettings settings) {
        return $"[world.cadence: {(settings.CadenceGate
            ? "on"
            : "off")}]";
    }
    private string RenderScaleEcho(string view) {
        view = ((view == "*") ? WorldViewGraphs.WorldInstance : view);
        var state = settings.Resolution(view: view);
        var controller = state.Controller;
        var ceiling = settings.Ceiling(view: view);
        var enabled = settings.Enabled(view: view);
        var mode = (!enabled ? "off" : ((state.Pin > 0f) ? $"pin {RenderScaleName(scale: state.Pin)}" : "auto"));
        var grid = ((enabled && (controller.Grid > 0d)) ? controller.Grid : WorldDynamicResolution.GridOf(ceiling: ceiling, scale: ceiling));
        var signal = (enabled ? controller.Signal : WorldDynamicResolutionSignal.Off);

        return string.Create(CultureInfo.InvariantCulture,
            $"[world.render-scale: view={view} {mode} ceiling={RenderScaleName(scale: ceiling)} floor={RenderScaleName(scale: settings.Floor(view: view))} grid={RenderScaleName(scale: ((float)grid))} over={controller.OverGrid} budget={controller.StepBudget} signal={signal.ToString().ToLowerInvariant()}]");
    }
    // The world.temporal echo.
    private static string TemporalEcho(WorldRenderSettings settings) =>
        $"[world.temporal: {(settings.Temporal ? "on" : "off")}]";
    // The world.detail echo's name of a level: the token that selects it.
    private static string DetailName(SdfMeshDetailLevel level) => level switch {
        SdfMeshDetailLevel.Level0 => "0",
        SdfMeshDetailLevel.Level1 => "1",
        SdfMeshDetailLevel.Level2 => "2",
        _ => "auto",
    };
    // Shared on/off token parse for the boolean isolator verbs (null = unrecognized).
    private static bool? ParseOnOff(ReadOnlySpan<char> token) {
        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "on"
        )) {
            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "off"
        )) {
            return false;
        }

        return null;
    }
    private static string RenderScaleName(float scale) {
        foreach (var tier in Enum.GetValues<WorldRenderScaleTier>()) {
            if (MathF.Abs(x: (scale - WorldRenderScaleTiers.Scale(tier: tier))) <= (0.5f / 255f)) {
                return WorldRenderScaleTiers.Name(tier: tier);
            }
        }

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{(scale * 100f):0.#}%"
        );
    }
    // The world.shadows echo.
    private static string ShadowEcho(WorldRenderSettings settings) =>
        $"[world.shadows: {ShadowTierName(tier: settings.Shadows)}]";
    // A shadow tier's spelling, as the document and world.shadows spell it.
    private static string ShadowTierName(ShadowTier tier) => tier.ToString().ToLowerInvariant();

    // Submits one live presentation-knob write through the server's grant check (WorldServer.ApplySessionLever) instead
    // of writing the injected service here. Defaults to the Render section because most of these knobs fold into it;
    // world.target passes Host explicitly.
    internal static void SubmitLever(IServerLink link, Principal principal, string name, double a, double b = 0.0, WorldSection section = WorldSection.Render) {
        link.SubmitSessionLever(
            lever: new WorldSessionLever(
                A: a,
                B: b,
                Name: name,
                Section: section
            ),
            principal: principal
        );
    }
    // Overload wired to the completion model: SubmitSessionLever is fire-and-forget (it carries no completion of its
    // own — the accept/reject outcome is already reported loud on stderr and through WorldServer.EchoTap), but the
    // console echo must still read the settings/pacing service ONLY after the lever has actually applied (or been
    // refused). Over loopback DeliverSessionLever runs synchronously inside SubmitSessionLever, so formatEcho is
    // invoked immediately after the submit call returns — never before it, and never from a stale prior read.
    internal static CommandResult SubmitLever(IServerLink link, Principal principal, string name, double a, Func<CommandResult> formatEcho, double b = 0.0, WorldSection section = WorldSection.Render) {
        SubmitLever(
            a: a,
            b: b,
            link: link,
            name: name,
            principal: principal,
            section: section
        );

        return formatEcho();
    }

    private static bool TryParseUpscaleSharpness(ReadOnlySpan<char> text, out float sharpness) {
        if (
            text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "bilinear"
        ) ||
            text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "off"
        )
        ) {
            sharpness = 0f;
        } else if (text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "balanced"
        )) {
            sharpness = 0.5f;
        } else if (text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "sharp"
        )) {
            sharpness = 1f;
        } else {
            sharpness = float.NaN;
        }

        if (!float.IsNaN(f: sharpness)) {
            return true;
        }

        var token = text.Trim();
        var percent = (!token.IsEmpty && (token[^1] == '%'));

        if (percent) {
            token = token[..^1];
        }

        if (!CommandArgs.TryParseFloat(
            text: token,
            value: out sharpness
        )) {
            return false;
        }

        if (percent) {
            sharpness /= 100f;
        }

        return (
            float.IsFinite(f: sharpness) &&
            (sharpness >= 0f) &&
            (sharpness <= 1f)
        );
    }
    private static string UpscaleSharpnessName(float sharpness) {
        if (MathF.Abs(x: sharpness) <= 0.0001f) {
            return "bilinear";
        }

        if (MathF.Abs(x: (sharpness - 0.5f)) <= 0.0001f) {
            return "balanced";
        }

        if (MathF.Abs(x: (sharpness - 1f)) <= 0.0001f) {
            return "sharp";
        }

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{(sharpness * 100f):0.#}%"
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return BloomCommand();
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.sky-layer",
            description: $"Auditions the sky rows printed by world.lighting: {WorldSkyLayerForms.Spelling}. Session-only; mute takes precedence over solo.",
            forms: WorldSkyLayerForms,
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.sky-layer: solo={((settings.SkyLayers.Solo < 0) ? "off" : settings.SkyLayers.Solo.ToString(provider: CultureInfo.InvariantCulture))} | {string.Join(separator: " | ", values: Enumerable.Range(0, (server.ReadView(context).Definition.Render.Sky?.Layers?.Count ?? 0)).Select(selector: index => $"sky[{index}] muted={settings.SkyLayers.Muted(index: index)} included={settings.SkyLayers.Includes(index: index)}"))}]");
                if (args.Count == 0) { return Echo(); }
                var solo = args.Is(index: 0, value: "solo");
                var index = -1;
                var off = (solo && (args.Count == 2) && args.Is(index: 1, value: "off"));
                var mute = ((args.Count == 3) ? ParseOnOff(token: args[2]) : null);

                if ((!solo && !args.Is(index: 0, value: "mute")) || (args.Count != (solo ? 2 : 3)) || (!solo && (mute is null)) ||
                    (!off && (!int.TryParse(args[1], CultureInfo.InvariantCulture, out index) || (index < 0) || (index >= (server.ReadView(context).Definition.Render.Sky?.Layers?.Count ?? 0))))) {
                    return WorldSkyLayerForms.Refuse();
                }
                return SubmitLever(link, context.Principal, (solo ? WorldSessionLevers.SkySolo : WorldSessionLevers.SkyMute), index, Echo, ((mute == true) ? 1d : 0d));
            });
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.shadows",
            forms: WorldShadowsForms,
            description: $"Selects the shadow tier ENGINE-WIDE, live: {WorldShadowsForms.Spelling} — no argument echoes the current tier. High renders every shadow cascade at 2048 texels a side, medium at 1024 and low at 512, each at a quarter of the memory and fill of the tier above; a change of tier rebuilds each view's graph once. Off, every light goes unshadowed and no cascade renders or is held.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: ShadowEcho(settings: settings));
                }

                var tier = ((args.Count != 1) ? ((ShadowTier?)null) : args[0] switch {
                    "off" => ShadowTier.Off,
                    "low" => ShadowTier.Low,
                    "medium" => ShadowTier.Medium,
                    "high" => ShadowTier.High,
                    _ => ((ShadowTier?)null),
                });

                if (tier is not { } selected) {
                    return WorldShadowsForms.Refuse(args: in args, got: args[0].ToString());
                }

                // The echo formats INSIDE SubmitLever's completion — after the lever has applied (or been refused),
                // never a live read taken separately and possibly before that.
                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Shadows,
                    a: ((int)selected),
                    formatEcho: () => new CommandResult(Output: ShadowEcho(settings: settings))
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.ao",
            forms: WorldAoForms,
            description: $"Toggles ambient occlusion engine-wide, live (no rebuild): {WorldAoForms.Spelling} — no argument echoes the current state. AO darkens creases and contact seams; turning it off skips the surface pass's screen-space occlusion taps (a small GPU saving, see world.counters gpu).",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.ao: {(settings.AmbientOcclusion
                        ? "on"
                        : "off")} | gradient-scaled]");
                }

                var on = ParseOnOff(token: args[0]);

                if (on is not { } resolved) {
                    return WorldAoForms.Refuse(args: in args, got: args[0].ToString());
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.AmbientOcclusion,
                    a: (resolved
                    ? 1.0
                    : 0.0),
                    formatEcho: () => new CommandResult(Output: $"[world.ao: {(settings.AmbientOcclusion
                    ? "on"
                    : "off")}]")
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.toon",
            description: $"Cel shading, live: {WorldToonForms.Spelling}. No argument echoes the mode. Switches each world's default style, render.toon (the standard's styles.puck authors celStyle); a world that authors none has no style to band with, and a material with a style of its own keeps it. Screens, portals and sky are not posterised. Presentation only, under the render grant.",
            forms: WorldToonForms,
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.toon: {(settings.Toon ? "on" : "off")}{(settings.ToonStyled ? string.Empty : " (render.toon authors no complete style, so this world's default stays continuous)")}]");
                if (args.Count == 0) { return Echo(); }
                if (args.Count != 1) { return WorldToonForms.Refuse(); }
                var on = (args.Is(index: 0, value: "toggle") ? !settings.Toon : ParseOnOff(token: args[0]));

                if (on is not { } resolved) { return WorldToonForms.Refuse(); }
                return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.Toon,
                    a: (resolved ? 1d : 0d), formatEcho: Echo);
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable, name: "world.shade", forms: WorldShadeForms,
            description: $"Selects authored shading programs or the material layers in one build for measurement: {WorldShadeForms.Spelling}.",
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.shade: {(settings.ShadePrograms ? "programs" : "layers")}]");
                if (args.Count == 0) { return Echo(); }
                if (args.Count == 2 && args.Is(0, "default")) {
                    var name = args[1].ToString();
                    var program = settings.NamedShade(name);
                    if (name != "document" && program is null) { return CommandResult.Error(output: $"[world.shade: no admitted program named '{name}' in this scene]"); }
                    var payload = program is null ? null : System.Text.Json.JsonSerializer.Serialize(program.Definition, WorldJsonContext.Default.SdfShadeDefinition);
                    link.SubmitSessionLever(new WorldSessionLever(WorldSection.Render, WorldSessionLevers.ShadeDefault, 0, View: payload), context.Principal);
                    return new(Output: $"[world.shade default: {name}]");
                }
                if (args.Count != 1 || (!args.Is(0, "programs") && !args.Is(0, "layers"))) { return WorldShadeForms.Refuse(); }
                return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.ShadePrograms,
                    a: args.Is(0, "programs") ? 1d : 0d, formatEcho: Echo);
            });
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.cadence",
            forms: WorldCadenceForms,
            description: $"Toggles the unchanged-frame cadence gate live (no rebuild): {WorldCadenceForms.Spelling}. On, a frame whose render inputs match the previous one re-composites the retained image (pixel-identical); off renders every frame, so world.counters gpu measures a still scene. Ships ON.",
            handler: (context, args) => {
                if (
                    (args.Count == 0) ||
                    args.Is(
                    index: 0,
                    value: "status"
                )
                ) {
                    return new CommandResult(Output: CadenceEcho(settings: settings));
                }

                if (ParseOnOff(token: args[0]) is not { } state) {
                    return WorldCadenceForms.Refuse(args: in args, got: args.Tail(start: 0).ToString());
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.CadenceGate,
                    a: (state
                    ? 1.0
                    : 0.0),
                    formatEcho: () => new CommandResult(Output: CadenceEcho(settings: settings))
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.cull",
            forms: WorldCullForms,
            description: $"Selects how much of the mesh draws the raster passes skip, live (no rebuild): {WorldCullForms.Spelling} (on is occlusion). occlusion, the default, draws each view's mesh pass in two phases, what the previous frame found visible and then what the GPU finds newly visible against the first phase's depth, on top of the box cull. The far cascade additionally rejects casters outside current receiver coverage and occludes entry depth in light space; exit and glass lists remain independent of entry visibility. Receiver-conditioned far layers refresh together. camera keeps camera occlusion but uses box-only cascades, isolating caster culling. frustum keeps the box cull alone: each view's mesh and glass passes draw only the draws whose box may lie in the view's frustum before the far distance, and each shadow cascade only those in its light's box; off draws every draw everywhere. world.counters gpu measures what each level saves (draws, vertices, fragments); the image is the same at every level.",
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.cull: {settings.Cull.ToString().ToLowerInvariant()}]");

                if ((args.Count == 0) || args.Is(index: 0, value: "status")) {
                    return Echo();
                }

                var level = ((args.Count != 1) ? ((SdfMeshCullLevel?)null) : args[0] switch {
                    "on" or "occlusion" => SdfMeshCullLevel.Occlusion,
                    "camera" => SdfMeshCullLevel.Camera,
                    "frustum" => SdfMeshCullLevel.Frustum,
                    "off" => SdfMeshCullLevel.Off,
                    _ => ((SdfMeshCullLevel?)null),
                });

                if (level is not { } selected) {
                    return WorldCullForms.Refuse(args: in args, got: args.Tail(start: 0).ToString());
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Cull,
                    a: ((double)selected),
                    formatEcho: Echo
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.detail",
            forms: WorldDetailForms,
            description: $"Selects which levels of detail of the baked meshes the raster passes draw, live (no rebuild): {WorldDetailForms.Spelling}. auto, the default, has each view's mesh pass draw each mesh at the coarsest level whose recorded error projects to at most one display pixel over the nearest point of its box, going back to a finer one only once the level it drew shows more than 1.1 pixels, and each shadow cascade at the coarsest level within one of its texels; the glass pass, and a mesh whose draw no host box bounds, draw the mesh's own triangles. 0, 1 or 2 draws that level everywhere (a mesh's coarsest where it has fewer), so a capture shows what a level changes. world.counters gpu measures what each saves (vertices).",
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.detail: {DetailName(level: settings.Detail)}]");

                if ((args.Count == 0) || args.Is(index: 0, value: "status")) {
                    return Echo();
                }

                var level = ((args.Count != 1) ? ((SdfMeshDetailLevel?)null) : args[0] switch {
                    "auto" => SdfMeshDetailLevel.Auto,
                    "0" => SdfMeshDetailLevel.Level0,
                    "1" => SdfMeshDetailLevel.Level1,
                    "2" => SdfMeshDetailLevel.Level2,
                    _ => ((SdfMeshDetailLevel?)null),
                });

                if (level is not { } selected) {
                    return WorldDetailForms.Refuse(args: in args, got: args.Tail(start: 0).ToString());
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Detail,
                    a: ((double)selected),
                    formatEcho: Echo
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.rates",
            forms: WorldRatesForms,
            description: $"Selects which presentation work updates less often when it is far or small on screen, live (no rebuild): {WorldRatesForms.Spelling} (toggle turns every lever off when all are on, else on), the levers bodies (a body's pose at least 30 times a second at or below 30 pixels per metre and 15 at or below 15 or while only its shadow, an undisplayed view or a reader of its parts may show it, held while nothing may, its root followed every frame), views (a nested portal or session view at least 30 times a second at or below 120 display pixels high and 15 at or below 60), screens (a screen's source upload at least 30 times a second at or below its native height on the display and 15 at or below half of it) and shadow-far (the far shadow cascade refreshed at least 30 times a second, its fit held between). Each tier is a floor held by presentation time, so slow or uneven frames never lower it; on a fixed signal its period is whole refreshes, under lockstep whole ticks. A rated update lands early rather than on a frame that runs a simulation step when a frame without one serves it. Every lever ships on. Presentation only: the simulation ticks every body and every machine runs at its own rate. The echo prints the display and grid the levers hold by, each tier's period (period-ms) and each lever's longest interval between updates at each tier over the last one to two seconds (longest-ms, '-' for none), which stays within a millisecond of the period unless a frame arrives later than the one before it. world.counters sdf.rates counts what each held; world.counters gpu shows the far cascade standing between refreshes.",
            handler: (context, args) => {
                CommandResult Echo() => new(Output: RatesEcho(settings: settings));

                if ((args.Count == 0) || args.Is(index: 0, value: "status")) {
                    return Echo();
                }

                var rates = settings.UpdateRates;
                SdfUpdateRates? selected = null;

                if ((args.Count == 1) && args.Is(index: 0, value: "toggle")) {
                    selected = ((rates == SdfUpdateRates.All) ? SdfUpdateRates.None : SdfUpdateRates.All);
                } else if ((args.Count == 1) && (ParseOnOff(token: args[0]) is { } all)) {
                    selected = (all ? SdfUpdateRates.All : SdfUpdateRates.None);
                } else if ((args.Count == 2) && (RatesLever(token: args[0]) is { } lever) && (ParseOnOff(token: args[1]) is { } on)) {
                    selected = (on ? rates | lever : rates & ~lever);
                }

                if (selected is not { } chosen) {
                    return WorldRatesForms.Refuse(args: in args, got: args.Tail(start: 0).ToString());
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Rates,
                    a: ((double)chosen),
                    formatEcho: Echo
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.indirect",
            description: $"Selects the indirect-light tier the baked-mesh path's irradiance volume traces at: {WorldIndirectForms.Spelling}. No argument reads the selected tier.",
            forms: WorldIndirectForms,
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.indirect: {settings.IndirectTier.ToString().ToLowerInvariant()}]");
                if (args.Count == 0) { return Echo(); }
                var tier = ((args.Count != 1) ? ((SdfIndirectTier?)null) : args[0] switch {
                    "off" => SdfIndirectTier.Off,
                    "medium" => SdfIndirectTier.Medium,
                    "high" => SdfIndirectTier.High,
                    _ => ((SdfIndirectTier?)null),
                });

                if (tier is not { } selected) {
                    return WorldIndirectForms.Refuse();
                }
                return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.Indirect,
                    a: ((int)selected), formatEcho: Echo);
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.exposure",
            description: $"Reads or pins the camera exposure: {WorldExposureForms.Spelling}. No argument echoes the world's render.exposure (mode, ev100, bounds, compensation, adaptation, local adaptation), the session's pin and, for each view of the world's latest frame, the EV100 it renders at, its multiplier per nit and the luminance its meter read about the camera (none while the irradiance volume has no probe there, or the exposure is manual). 'pin <ev100>' holds every view the session presents, nested worlds included, manual at that EV100 with no local adaptation, as render.exposure.hold does; 'pin' alone holds the first view's current EV100; 'pin off' releases it. Presentation only: the pin never reaches the document or simulation state.",
            forms: WorldExposureForms,
            handler: (context, args) => {
                var exposure = WorldRenderExposure.Resolve(exposure: server.ReadView(context).Definition.Render.Exposure);
                var authored = string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, $"mode={exposure.Mode.ToString().ToLowerInvariant()} ev100={exposure.Ev100:0.##} minimum={exposure.Minimum:0.##} maximum={exposure.Maximum:0.##} compensation={exposure.Compensation:0.##} adaptation={exposure.Adaptation:0.##}s local={exposure.Local:0.##}");
                string Pin() => ((settings.ExposurePin is { } pinned) ? pinned.ToString(format: "0.##", provider: CultureInfo.InvariantCulture) : "off");
                // The lever lands through the server, after this answer: echo what was asked.
                static Func<CommandResult> PinEcho(string pin) => () => new(Output: $"[world.exposure: pin={pin}]");

                if (args.Count > 0) {
                    if ((args[0] is not "pin") || (args.Count > 2)) {
                        return WorldExposureForms.Refuse();
                    }
                    if ((args.Count == 2) && (args[1] is "off")) {
                        return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.ExposurePin, a: 0d, b: 0d, formatEcho: PinEcho(pin: "off"));
                    }

                    float ev100;

                    if (args.Count == 2) {
                        if (!args.TryFloats(count: 1, start: 1, values: out var values) || !float.IsFinite(f: values[0]) || (values[0] < SdfPhotometry.MinimumEv100) || (values[0] > SdfPhotometry.MaximumEv100)) {
                            return WorldExposureForms.Refuse();
                        }
                        ev100 = values[0];
                    } else if ((renderProbe.Residency?.Exposures.Ev100Of(view: 0) is { } current) && float.IsFinite(f: current)) {
                        ev100 = current;
                    } else {
                        return CommandResult.Error(output: "[world.exposure: no view has rendered an exposure to pin yet; name the EV100]");
                    }
                    return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.ExposurePin, a: ev100, b: 1d, formatEcho: PinEcho(pin: ev100.ToString(format: "0.##", provider: CultureInfo.InvariantCulture)));
                }
                if (renderProbe.Residency is not { } node) {
                    return new CommandResult(Output: $"[world.exposure: {authored} pin={Pin()}; renderer not built yet]");
                }

                return new CommandResult(Output: $"[world.exposure: {authored} pin={Pin()}; {string.Join(separator: "; ", values: node.Exposures.Describe())}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.residency",
            description: $"Reads every residency held by the presentation's world package, including routed and session views: {WorldResidencyForms.Spelling} (metres, default 8). Each report names its diagnostic residency-id, independent of its display name. Echoes the latest bake's groups and when they landed (finished) and became resident (their words in the mesh region), counted from the bake's start, both for every group and for the groups whose planned box lies within the radius of the primary seat's camera (the occupied area), with when half and nine tenths of those landed and which landed last; its store loads and their span; the time its plan and restart took; the resident meshes' region bytes by level of detail; the store's reads, bytes and read and decode thread-seconds; the mesh region's held, used, written and owed bytes and the frames and seconds it owed words; the latest program's instances, words, builds and its emission and build milliseconds; and the irradiance volume's probe cache bytes. 'pending' marks a time not reached yet. Presentation only; reads change nothing.",
            forms: WorldResidencyForms,
            handler: (context, args) => {
                var radius = 8f;

                if (args.Count == 1) {
                    if (!args.TryFloats(count: 1, start: 0, values: out var values) || !float.IsFinite(f: values[0]) || (values[0] < 0f)) {
                        return WorldResidencyForms.Refuse();
                    }
                    radius = values[0];
                } else if (args.Count > 1) {
                    return WorldResidencyForms.Refuse();
                }
                if (renderProbe.Residency is not { } node) {
                    return new CommandResult(Output: "[world.residency: renderer not built yet]");
                }

                var reports = renderProbe.Views?.DescribeResidencies(radius, node, renderProbe.Composition)
                    ?? node.DescribeResidency(composition: renderProbe.Composition, radius: radius);
                return new CommandResult(Output: $"[world.residency: {string.Join(separator: Environment.NewLine, values: reports)}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.probe",
            description: $"Reads the settled irradiance probes about a point: {WorldProbeForms.Spelling}. A changing trace or pending publication is marked unsettled with the published GPU and live CPU windows. Otherwise echoes spacing, window, answering corners and total weight for a receiver facing the normal (up by default), then each corner's cell, relocated position, offset, back-face share, validity, sweeps, measured cubes' mean sky and luminance, and weight terms: trilinear share, positive normal cosine, octahedral depth visibility and final weight. reference-clear is the exact static-mesh segment check for diagnosing sampling; it never changes the gather.",
            forms: WorldProbeForms,
            handler: (context, args) => {
                if (((args.Count != 3) && (args.Count != 6)) || !args.TryFloats(count: args.Count, start: 0, values: out var values) || values.Any(predicate: static value => !float.IsFinite(f: value))) {
                    return WorldProbeForms.Refuse();
                }

                var normal = ((args.Count == 6) ? new System.Numerics.Vector3(x: values[3], y: values[4], z: values[5]) : System.Numerics.Vector3.UnitY);

                if (normal.LengthSquared() <= 0f) {
                    return CommandResult.Error(output: "[world.probe: the normal must not be zero]");
                }
                if (renderProbe.Residency is not { } node) {
                    return new CommandResult(Output: "[world.probe: renderer not built yet]");
                }

                return new CommandResult(Output: string.Join(separator: Environment.NewLine,
                    values: node.MeshIrradiance.DescribeProbes(normal: System.Numerics.Vector3.Normalize(value: normal), point: new System.Numerics.Vector3(x: values[0], y: values[1], z: values[2]))));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.render-scale",
            forms: WorldRenderScaleForms,
            description: $"Controls view resolution: {WorldRenderScaleForms.Spelling}. No argument echoes ceiling, saved quality floor, grid and signal. A pin is session-only and must lie between the floor and ceiling; auto releases it and resumes the policy from its grid. Sweeps inside the same ceiling allocate nothing. Defaults are off; the floor is Quarter unless a view's quality or tier authors another.",
            handler: (context, args) => {
                settings.ReadQuality(definition: server.ReadView(context).Definition);
                if (!WorldRenderScaleCommand.TryParse(args: in args, command: out var command, refusal: out var refusal)) {
                    return ((refusal is null) ? WorldRenderScaleForms.Refuse(args: in args, got: args.Tail(start: 0).ToString()) : CommandResult.Error(output: $"[{refusal}]"));
                }
                if (!settings.HasView(view: command.View)) {
                    return CommandResult.Error(output: $"[world.render-scale: unknown view '{command.View}']");
                }
                if (command.Operation == WorldRenderScaleOperation.Echo) {
                    return new CommandResult(Output: RenderScaleEcho(view: command.View));
                }
                if ((command.Operation == WorldRenderScaleOperation.Pin) && !settings.CanPin(command.View, command.Scale, out refusal)) {
                    return CommandResult.Error(output: $"[world.render-scale: {refusal}]");
                }
                var section = (((command.View == "*") && (command.Operation != WorldRenderScaleOperation.Floor))
                    ? WorldSection.Render : WorldSection.Views);

                link.SubmitSessionLever(lever: new WorldSessionLever(section, WorldSessionLevers.RenderScale,
                    command.Scale, ((double)command.Operation), View: ((command.View == "*") ? null : command.View)), principal: context.Principal);
                return new CommandResult(Output: RenderScaleEcho(view: command.View));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.upscale-sharpness",
            forms: WorldUpscaleSharpnessForms,
            description: $"Sets reconstruction sharpness continuously, live: {WorldUpscaleSharpnessForms.Spelling}. Names alias 0/50/100%. A reduced view's spatial resolve is the four-tap bilinear fast path at zero, and any positive value blends toward clamped Catmull-Rom; a temporally resolved view (world.temporal) gets a contrast-adaptive sharpen of that strength where place shows it at its own extent. A native view that does not reconstruct ignores this setting.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.upscale-sharpness: {UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)}]");
                }

                if (!TryParseUpscaleSharpness(
                    text: args[0],
                    sharpness: out var sharpness
                )) {
                    return WorldUpscaleSharpnessForms.Refuse(args: in args, got: args[0].ToString());
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.UpscaleSharpness,
                    a: sharpness,
                    formatEcho: () => new CommandResult(Output: $"[world.upscale-sharpness: {UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)}]")
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.temporal",
            forms: WorldTemporalForms,
            description: $"Turns temporal reconstruction of the world's own views on or off, live: {WorldTemporalForms.Spelling} — no argument echoes the current state. On, each view jitters its samples over an eight-sample sequence and resolves them over its history into its output, at native or reduced render scale, and a still view stands once it has converged; off, a reduced view resolves spatially and a native view writes its output directly. A change rebuilds each view's graph beside the installed one. The world's own views are the seat views, the operator pose and every layout slot that names a camera; the camera views screens film and session views never reconstruct.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: TemporalEcho(settings: settings));
                }

                if (ParseOnOff(token: args[0]) is not { } on) {
                    return WorldTemporalForms.Refuse(args: in args, got: args[0].ToString());
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Temporal,
                    a: (on ? 1.0 : 0.0),
                    formatEcho: () => new CommandResult(Output: TemporalEcho(settings: settings))
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.sky-quality",
            forms: WorldSkyQualityForms,
            description: $"Sets the sky's quality tier, live: {WorldSkyQualityForms.Spelling} — no argument echoes the current tier. A sky layer whose tier lies above it writes no entry and counts no work; below high each kind draws its reduced form (clouds take one thickness tap and three octaves at low, shaded flat, and three octaves at medium; stars stop twinkling at low; an aurora and a noise field take fewer octaves). The world's quality presets set it through their sky row.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: SkyQualityEcho(settings: settings));
                }

                WorldSkyTier? tier = args[0].ToString() switch {
                    "low" => WorldSkyTier.Low,
                    "medium" => WorldSkyTier.Medium,
                    "high" => WorldSkyTier.High,
                    _ => null,
                };

                if (tier is not { } chosen) {
                    return WorldSkyQualityForms.Refuse(args: in args, got: args[0].ToString());
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.SkyQuality,
                    a: ((double)chosen),
                    formatEcho: () => new CommandResult(Output: SkyQualityEcho(settings: settings))
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.sky-field-scale",
            description: $"Sets the sky field grid independently of the scene grid: {WorldSkyFieldScaleForms.Spelling}. One half evaluates a ceil-half grid and reconstructs it during composition; retained field allocation capacity stays unchanged. No argument echoes the current fraction; quality presets and save retain it.",
            forms: WorldSkyFieldScaleForms,
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.sky-field-scale: {settings.SkyFieldScale.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}]");
                if (args.Count == 0) { return Echo(); }
                float? scale = ((args.Count == 1) ? args[0].ToString() switch {
                    "1" => 1f,
                    "0.5" => .5f,
                    _ => null,
                } : null);

                if (scale is not { } chosen) { return WorldSkyFieldScaleForms.Refuse(); }
                return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.SkyFieldScale,
                    a: chosen, formatEcho: Echo);
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.quality",
            forms: WorldQualityForms,
            description: $"Applies one of the world's authored graphics PRESETs (render.low, render.medium, render.high), each bundling the shadow, ambient-occlusion, temporal-reconstruction, dynamic-resolution, render-scale, sky-quality, sky-field-scale and indirect-tier levers, live: {WorldQualityForms.Spelling} — no argument echoes the current settings; next steps low to medium to high and back to low from the preset the shadow tier matches (off is low, high is high, any tier between is medium), so one key cycles them. A preset the world does not author is refused by name. A preset just writes the individual settings (world.shadows/.ao/.temporal/.render-scale still override afterward).",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribeQuality());
                }

                // The preset table is world data (WorldDefinition.Render), read off the LIVE definition so a mutated
                // preset table applies immediately: look the named tier up and write its levers into the live
                // settings.
                // `next` steps the cheapest preset to the fullest and back, from the preset the shadow tier matches
                // (off is low, high is high, the tiers between are medium), so one key cycles them.
                var requested = (args.Is(index: 0, value: "next")
                    ? QualityTiers.Name(tier: (settings.Shadows switch {
                        ShadowTier.Off => QualityTier.Medium,
                        ShadowTier.High => QualityTier.Low,
                        _ => QualityTier.High,
                    }))
                    : args[0].ToString());

                if (QualityTiers.Parse(name: requested) is not { } tier) {
                    return WorldQualityForms.Refuse(args: in args, got: args[0].ToString());
                }

                if (server.ReadView(context).Definition.Render.Preset(tier: tier) is not { } preset) {
                    return CommandResult.Error(output: $"[world.quality: this world authors no {QualityTiers.Name(tier: tier)} preset]");
                }

                SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Shadows,
                    a: ((int)preset.Shadows)
                );
                SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.AmbientOcclusion,
                    a: (preset.AmbientOcclusion
                    ? 1.0
                    : 0.0)
                );
                SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Temporal,
                    a: (preset.Temporal
                    ? 1.0
                    : 0.0)
                );
                SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.RenderScale,
                    b: ((double)(preset.DynamicResolution ? WorldRenderScaleOperation.Auto : WorldRenderScaleOperation.Off)),
                    a: 0.0
                );

                link.SubmitSessionLever(lever: WorldSessionLevers.ShadowPolicy(preset: preset), principal: context.Principal);
                SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.SkyQuality,
                    a: ((double)preset.Sky)
                );
                SubmitLever(link, context.Principal, WorldSessionLevers.SkyFieldScale, preset.SkyFieldScale);
                SubmitLever(link, context.Principal, WorldSessionLevers.BloomQuality, ((double)preset.Bloom));
                SubmitLever(link, context.Principal, WorldSessionLevers.Indirect, ((double)(preset.Indirect ?? (tier switch {
                    QualityTier.Low => SdfIndirectTier.Off,
                    QualityTier.Medium => SdfIndirectTier.Medium,
                    _ => SdfIndirectTier.High,
                }))));
                SubmitLever(link, context.Principal, WorldSessionLevers.RenderScale,
                    WorldRenderScaleTiers.Scale(tier: preset.RenderScaleFloor), b: ((double)WorldRenderScaleOperation.Floor),
                    section: WorldSection.Views);
                // The echo formats inside the last lever's completion — every setting has applied (or the last was
                // refused) by the time formatEcho runs, since loopback drains each inline before its Submit* returns.
                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.RenderScale,
                    a: preset.RenderScale,
                    b: ((double)WorldRenderScaleOperation.Ceiling),
                    formatEcho: () => new CommandResult(Output: DescribeQuality())
                );
            }
        );
    }
}
