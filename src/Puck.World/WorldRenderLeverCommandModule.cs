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
/// The render levers: engine-wide render options any presentation shape honors — shadows and their crowd radius,
/// ambient occlusion and its quality, the far field, the unchanged-frame cadence gate, the shadow mask and march, render
/// scale, temporal reconstruction, upscale sharpness, and the quality preset — each a live console verb that echoes its current value when
/// called with no argument. Every write is a session lever submitted through the server's grant check and lands in
/// <see cref="WorldRenderSettings"/>, which the frame source reads each captured frame, except the SDF debug view
/// and operator-owned indirect freeze/reset, which control presentation objects through <see cref="WorldRenderProbe"/>.
/// Nothing here needs a window or a
/// presenter, so both the windowed and the offscreen presentation shapes compose it, and an offscreen collector or
/// canary can set the same levers a player can. Headless composes no renderer and refuses these as unknown.
/// </summary>
internal sealed class WorldRenderLeverCommandModule(WorldPopulation population, WorldRenderSettings settings, WorldServer server, IServerLink link, WorldRenderProbe renderProbe) : ICommandModule {
    /// <summary>Owns the automatic population threshold and readout shape shared by adaptive render-quality levers.</summary>
    private string DescribeAdaptiveQuality(string verb, int mode, string exact = "exact", string fast = "fast") {
        var (configured, isFast) = mode switch {
            1 => (exact, ((bool?)false)),
            2 => (fast, ((bool?)true)),
            _ => ("auto", ((bool?)null)),
        };
        var resolved = ((isFast ?? (population.SimulatedCount >= 16))
            ? fast
            : exact
        );

        return $"[{verb}: {configured} → {resolved} | simulated={population.SimulatedCount}]";
    }
    private string DescribeAmbientOcclusionQuality() =>
        DescribeAdaptiveQuality(
            mode: ((int)settings.AmbientOcclusionQuality),
            verb: "world.ao-quality"
        );
    // The world.quality echo: the current individual settings the preset (or a later override) left in place.
    private string DescribeQuality() {
        return $"[world.quality: shadows={ShadowTiers.Name(reach: settings.ShadowReach)} ao={(settings.AmbientOcclusion
            ? "on"
            : "off")} temporal={(settings.Temporal
            ? "on"
            : "off")} shadow-amortize={(settings.ShadowAmortize
            ? "on"
            : "off")} dynamic-resolution={(settings.DynamicResolution
            ? "on"
            : "off")} render-scale={RenderScaleName(scale: settings.RenderScale)} upscale={UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)} sky={SkyQualityName(tier: settings.SkyQuality)}]";
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
    private string DescribeShadowMarch() =>
        DescribeAdaptiveQuality(
            mode: ((int)settings.ShadowMarch),
            verb: "world.shadow-march"
        );
    private string DescribeShadowMask() =>
        DescribeAdaptiveQuality(
            fast: "camera-tile",
            mode: ((int)settings.ShadowMask),
            verb: "world.shadow-mask"
        );
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
    // The world.bakes echo.
    private static string BakesEcho(WorldRenderSettings settings) =>
        $"[world.bakes: {settings.Bakes switch {
            true => "on",
            false => "off",
            null => "default: a world's bakes draw when it ships them",
        }}]";
    // The world.far-field echo.
    private static string FarFieldEcho(WorldRenderSettings settings) {
        return $"[world.far-field: bound {(settings.FarBound
            ? "on"
            : "off")}]";
    }
    private static bool MatchesAny(in WireArgs args, string[] spellings) {
        foreach (var spelling in spellings) {
            if (args.Is(
                index: 0,
                value: spelling
            )) {
                return true;
            }
        }

        return false;
    }
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
    private static string ShadowEcho(WorldRenderSettings settings) {
        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.shadows: {ShadowTiers.Name(reach: settings.ShadowReach)} | crowd {settings.ShadowCrowdRadius:0.##} | gradient-scaled]"
        );
    }

    // The world.shadows echo: continuous reach plus crowd radius; named-notch values render through their facade.
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

    // The one argument grammar every adaptive render-quality lever (world.ao-quality, world.shadow-march,
    // world.shadow-mask) parses: auto, the exact side, or the fast side — answered as the lever's OWN mode member,
    // so the value the session lever carries is anchored to the enum declaration, never to a shared ordinal.
    private static bool TryParseAdaptiveMode<TMode>(in WireArgs args, string[] exact, string[] fast, TMode autoMode, TMode exactMode, TMode fastMode, out TMode mode) where TMode : struct, Enum {
        if (args.Is(
            index: 0,
            value: "auto"
        )) {
            mode = autoMode;

            return true;
        }

        if (MatchesAny(
            args: in args,
            spellings: exact
        )) {
            mode = exactMode;

            return true;
        }

        if (MatchesAny(
            args: in args,
            spellings: fast
        )) {
            mode = fastMode;

            return true;
        }

        mode = default;

        return false;
    }
    private static bool TryParseShadowReach(ReadOnlySpan<char> text, out float reach) {
        if (text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "off"
        )) {
            reach = 0f;
        } else if (text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "low"
        )) {
            reach = 0.25f;
        } else if (text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "medium"
        )) {
            reach = 0.5f;
        } else if (
            text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "high"
        ) ||
            text.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "on"
        )
        ) {
            reach = 1f;
        } else {
            reach = float.NaN;
        }

        if (!float.IsNaN(f: reach)) {
            return true;
        }

        var token = text.Trim();
        var percent = (!token.IsEmpty && (token[^1] == '%'));

        if (percent) {
            token = token[..^1];
        }

        if (!CommandArgs.TryParseFloat(
            text: token,
            value: out reach
        )) {
            return false;
        }

        if (percent) {
            reach /= 100f;
        }

        return (
            float.IsFinite(f: reach) &&
            (reach >= 0f) &&
            (reach <= 1f)
        );
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
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.sky-layer",
            description: "Auditions the sky rows printed by world.lighting: world.sky-layer [solo <index>|solo off|mute <index> on|off]. Session-only; mute takes precedence over solo.",
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.sky-layer: solo={((settings.SkyLayers.Solo < 0) ? "off" : settings.SkyLayers.Solo.ToString(provider: CultureInfo.InvariantCulture))} | {string.Join(separator: " | ", values: Enumerable.Range(0, (server.Definition.Render.Sky?.Layers?.Count ?? 0)).Select(selector: index => $"sky[{index}] muted={settings.SkyLayers.Muted(index: index)} included={settings.SkyLayers.Includes(index: index)}"))}]");
                if (args.Count == 0) { return Echo(); }
                var solo = args.Is(index: 0, value: "solo");
                var index = -1;
                var off = (solo && (args.Count == 2) && args.Is(index: 1, value: "off"));
                var mute = ((args.Count == 3) ? ParseOnOff(token: args[2]) : null);

                if ((!solo && !args.Is(index: 0, value: "mute")) || (args.Count != (solo ? 2 : 3)) || (!solo && (mute is null)) ||
                    (!off && (!int.TryParse(args[1], CultureInfo.InvariantCulture, out index) || (index < 0) || (index >= (server.Definition.Render.Sky?.Layers?.Count ?? 0))))) {
                    return CommandResult.Usage(form: "solo <index>|solo off|mute <index> on|off", verb: "world.sky-layer");
                }
                return SubmitLever(link, context.Principal, (solo ? WorldSessionLevers.SkySolo : WorldSessionLevers.SkyMute), index, Echo, ((mute == true) ? 1d : 0d));
            });
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shadows",
            description: "Sets continuous ENGINE-WIDE soft-shadow reach and CROWD RADIUS, live (no rebuild): world.shadows [off|low|medium|high|0..1|0%..100%] [crowd-radius]. Names alias 0/25/50/100%; numeric input is continuous. The optional 0..100 world-unit crowd radius bounds WHO casts; farther avatars still render but leave the shadow march.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: ShadowEcho(settings: settings));
                }

                if (!TryParseShadowReach(
                    text: args[0],
                    reach: out var reach
                )) {
                    return CommandResult.Error(output: $"[world.shadows: invalid reach '{args[0]}' — off|low|medium|high, 0..1, or 0%..100%]");
                }

                var crowdRadius = settings.ShadowCrowdRadius;

                if (args.Count >= 2) {
                    if (
                        !args.TryFloat(
                        index: 1,
                        value: out var radius
                    ) ||
                        (radius < 0f) ||
                        (radius > 100f)
                    ) {
                        return CommandResult.Error(output: $"[world.shadows: bad crowd-radius '{args[1]}' — a number 0..100]");
                    }

                    crowdRadius = radius;
                }

                // The echo formats INSIDE SubmitLever's completion — after the lever has applied (or been refused),
                // never a live read taken separately and possibly before that.
                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Shadows,
                    a: reach,
                    b: crowdRadius,
                    formatEcho: () => new CommandResult(Output: ShadowEcho(settings: settings))
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.ao",
            description: "Toggles ambient occlusion engine-wide, live (no rebuild): world.ao [on|off] — no argument echoes the current state. AO darkens creases and contact seams; turning it off skips the per-lit-pixel occlusion march (a small GPU saving, see world.counters gpu).",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.ao: {(settings.AmbientOcclusion
                        ? "on"
                        : "off")} | gradient-scaled]");
                }

                var on = ParseOnOff(token: args[0]);

                if (on is not { } resolved) {
                    return CommandResult.Error(output: $"[world.ao: unknown state '{args[0]}' — on|off]");
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
            bindability: CommandBindability.Unbindable,
            name: "world.bakes",
            description: "Draws each prototype's ready bake in place of its field, or its field again: world.bakes [on|off|status]. Presentation only (the field still answers contact, casts shadows and occludes); a prototype whose bake is not ready yet draws its field and switches when it is (world.counters counts the switch as sdf.bakes.drawn). Ships off.",
            handler: (context, args) => {
                if (
                    (args.Count == 0) ||
                    args.Is(
                    index: 0,
                    value: "status"
                )
                ) {
                    return new CommandResult(Output: BakesEcho(settings: settings));
                }

                if (ParseOnOff(token: args[0]) is not { } state) {
                    return CommandResult.Error(output: $"[world.bakes: unknown '{args.Tail(start: 0)}' — on|off|status]");
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Bakes,
                    a: (state
                    ? 1.0
                    : 0.0),
                    formatEcho: () => new CommandResult(Output: BakesEcho(settings: settings))
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.far-field",
            description: "Toggles the beam-published per-tile far bound live (no rebuild): world.far-field [on|off|status]. Output-identical when on (it skips empty-sky march steps); off is the paired-run baseline. Ships ON.",
            handler: (context, args) => {
                if (
                    (args.Count == 0) ||
                    args.Is(
                    index: 0,
                    value: "status"
                )
                ) {
                    return new CommandResult(Output: FarFieldEcho(settings: settings));
                }

                if (ParseOnOff(token: args[0]) is not { } state) {
                    return CommandResult.Error(output: $"[world.far-field: unknown '{args.Tail(start: 0)}' — on|off|status]");
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.FarBound,
                    a: (state
                    ? 1.0
                    : 0.0),
                    formatEcho: () => new CommandResult(Output: FarFieldEcho(settings: settings))
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.cadence",
            description: "Toggles the unchanged-frame cadence gate live (no rebuild): world.cadence [on|off|status]. On, a frame whose render inputs match the previous one re-composites the retained image (pixel-identical); off renders every frame, so world.counters gpu measures a still scene. Ships ON.",
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
                    return CommandResult.Error(output: $"[world.cadence: unknown '{args.Tail(start: 0)}' — on|off|status]");
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
            bindability: CommandBindability.Unbindable,
            name: "world.indirect",
            description: "Selects the residency's indirect cache: world.indirect [off|medium|high]. No argument reads the selected tier; world.lighting reads its live host inventory and world.budget its allocation.",
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
                    return CommandResult.Error(output: "[world.indirect: expected off|medium|high]");
                }
                return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.Indirect,
                    a: ((int)selected), formatEcho: Echo);
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.indirect-freeze",
            description: "Pauses new update admission in every active residency: world.indirect-freeze [on|off]. Reads keep the retained cache; an already admitted frame may finish. Operator presentation control, not authoritative state.",
            handler: (_, args) => {
                if (args.Count > 1) { return CommandResult.Usage(form: "on|off", verb: "world.indirect-freeze"); }
                var freeze = ((args.Count == 1) ? ParseOnOff(args[0]) : null);
                if ((args.Count == 1) && (freeze is null)) { return CommandResult.Usage(form: "on|off", verb: "world.indirect-freeze"); }
                if (renderProbe.IndirectResidencies.Count == 0) {
                    return CommandResult.Error(output: "[world.indirect-freeze: no active indirect residency — select medium or high and wait for the renderer]");
                }
                if (freeze is { } selected) {
                    foreach (var residency in renderProbe.IndirectResidencies) { residency.IndirectFrozen = selected; }
                }
                return new CommandResult(Output: $"[world.indirect-freeze: {WorldIndirectDiagnosticText.Describe(renderProbe)}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.indirect-reset",
            description: "Queues every active residency's presentation cache reset at its next renderable frame. Frozen caches withdraw their old publication and admit no replacement work. Takes no argument; changes no authoritative state.",
            handler: (_, args) => {
                if (CommandResult.RequireNoArguments(args, "world.indirect-reset") is { } refusal) { return refusal; }
                if (renderProbe.IndirectResidencies.Count == 0) {
                    return CommandResult.Error(output: "[world.indirect-reset: no active indirect residency — select medium or high and wait for the renderer]");
                }
                foreach (var residency in renderProbe.IndirectResidencies) { residency.RequestIndirectReset(); }
                return new CommandResult(Output: $"[world.indirect-reset: {WorldIndirectDiagnosticText.Describe(renderProbe)}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.debug-view",
            description: $"Selects the live SDF diagnostic output for every World camera: world.debug-view [{string.Join(
                separator: '|',
                value: DebugViewModes.Names
            )}]. Depth is the primary-march-only performance probe; off restores final shading.",
            handler: (_, args) => {
                if (renderProbe.Residency is not { } node) {
                    return CommandResult.Error(output: "[world.debug-view: renderer not built yet]");
                }

                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.debug-view: {DebugViewModes.Name(mode: node.DebugMode)}]");
                }

                if (
                    (args.Count != 1) ||
                    !DebugViewModes.TryParse(
                    name: args[0].ToString(),
                    mode: out var mode
                )
                ) {
                    return CommandResult.Error(output: $"[world.debug-view: unknown mode '{args.Tail(start: 0)}' — {string.Join(
                        separator: '|',
                        value: DebugViewModes.Names
                    )}]");
                }

                node.DebugMode = mode;

                return new CommandResult(Output: $"[world.debug-view: {DebugViewModes.Name(mode: mode)}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shadow-mask",
            description: "Selects the soft-shadow candidate-mask path live: world.shadow-mask [auto|exact|camera-tile]. auto uses the exact per-tile grid gather (one shadow candidate mask per 8x8 workgroup, equal to the flat march to the bit by construction, though no automated check compares them) below 16 simulated stand-ins and the fast camera-tile approximation at the 16/64/128 fleet tiers; exact and camera-tile force either side for visual/performance A/B.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribeShadowMask());
                }

                if (!TryParseAdaptiveMode(
                    args: in args,
                    autoMode: ShadowMaskMode.Auto,
                    exact: ExactOrGather,
                    exactMode: ShadowMaskMode.ExactGather,
                    fast: CameraTileAliases,
                    fastMode: ShadowMaskMode.CameraTile,
                    mode: out var mode
                )) {
                    return CommandResult.Error(output: $"[world.shadow-mask: unknown mode '{args[0]}' — auto|exact|camera-tile]");
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.ShadowMask,
                    a: ((double)mode),
                    formatEcho: () => new CommandResult(Output: DescribeShadowMask())
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.ao-quality",
            description: "Selects the ambient-occlusion sampler live: world.ao-quality [auto|exact|fast]. auto keeps the three-rung quality ladder below 16 simulated stand-ins and uses the calibrated one-sample contact path at the 16/64/128 fleet tiers; exact and fast force either side for visual/performance A/B.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribeAmbientOcclusionQuality());
                }

                if (!TryParseAdaptiveMode(
                    args: in args,
                    autoMode: AmbientOcclusionMode.Auto,
                    exact: ExactOrQuality,
                    exactMode: AmbientOcclusionMode.Exact,
                    fast: FastOrFleet,
                    fastMode: AmbientOcclusionMode.Fast,
                    mode: out var mode
                )) {
                    return CommandResult.Error(output: $"[world.ao-quality: unknown mode '{args[0]}' — auto|exact|fast]");
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.AmbientOcclusionQuality,
                    a: ((double)mode),
                    formatEcho: () => new CommandResult(Output: DescribeAmbientOcclusionQuality())
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shadow-march",
            description: "Selects the soft-shadow marcher live: world.shadow-march [auto|exact|fast]. auto keeps the exact 64-step, 9-unit path below 16 simulated stand-ins and uses the bounded-cost 12-step, 5-unit near-field path at the 16/64/128 fleet tiers; exact and fast force either side for visual/performance A/B.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribeShadowMarch());
                }

                if (!TryParseAdaptiveMode(
                    args: in args,
                    autoMode: ShadowMarchMode.Auto,
                    exact: ExactOrQuality,
                    exactMode: ShadowMarchMode.Exact,
                    fast: FastOrFleet,
                    fastMode: ShadowMarchMode.Fast,
                    mode: out var mode
                )) {
                    return CommandResult.Error(output: $"[world.shadow-march: unknown mode '{args[0]}' — auto|exact|fast]");
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.ShadowMarch,
                    a: ((double)mode),
                    formatEcho: () => new CommandResult(Output: DescribeShadowMarch())
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.render-scale",
            description: "Controls view resolution: world.render-scale [view] [<tier|fraction|percent>|floor <tier>|pin <scale>|auto [on|off]]. No argument echoes ceiling, saved quality floor, grid and signal. A pin is session-only and must lie between the floor and ceiling; auto releases it and resumes the policy from its grid. Sweeps inside the same ceiling allocate nothing. Defaults are off; the floor is Quarter unless a view's quality or tier authors another.",
            handler: (context, args) => {
                settings.ReadQuality(definition: server.Definition);
                if (!WorldRenderScaleCommand.TryParse(args: in args, command: out var command, refusal: out var refusal)) {
                    return CommandResult.Error(output: $"[{refusal}]");
                }
                if (!settings.HasView(view: command.View)) {
                    return CommandResult.Error(output: $"[world.render-scale: unknown view '{command.View}']");
                }
                if (command.Operation == WorldRenderScaleOperation.Echo) {
                    return new CommandResult(Output: RenderScaleEcho(view: command.View));
                }
                if ((command.Operation == WorldRenderScaleOperation.Pin) && !settings.CanPin(command.View, command.Scale, out refusal)) {
                    return CommandResult.Error(output: $"[{refusal}]");
                }
                var section = (((command.View == "*") && (command.Operation != WorldRenderScaleOperation.Floor))
                    ? WorldSection.Render : WorldSection.Views);

                link.SubmitSessionLever(lever: new WorldSessionLever(section, WorldSessionLevers.RenderScale,
                    command.Scale, ((double)command.Operation), View: ((command.View == "*") ? null : command.View)), principal: context.Principal);
                return new CommandResult(Output: RenderScaleEcho(view: command.View));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.upscale-sharpness",
            description: "Sets reconstruction sharpness continuously, live: world.upscale-sharpness [bilinear|balanced|sharp|0..1|0%..100%]. Names alias 0/50/100%. A reduced view's spatial resolve is the four-tap bilinear fast path at zero, and any positive value blends toward clamped Catmull-Rom; a temporally resolved view (world.temporal) gets a contrast-adaptive sharpen of that strength where place shows it at its own extent. A native view that does not reconstruct ignores this setting.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.upscale-sharpness: {UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)}]");
                }

                if (!TryParseUpscaleSharpness(
                    text: args[0],
                    sharpness: out var sharpness
                )) {
                    return CommandResult.Error(output: $"[world.upscale-sharpness: invalid '{args[0]}' — bilinear|balanced|sharp, 0..1, or 0%..100%]");
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
            bindability: CommandBindability.Unbindable,
            name: "world.temporal",
            description: "Turns temporal reconstruction of the world's own views on or off, live: world.temporal [on|off] — no argument echoes the current state. On, each view jitters its samples over an eight-sample sequence and resolves them over its history into its output, at native or reduced render scale, and a still view stands once it has converged; off, a reduced view resolves spatially and a native view writes its output directly. A change rebuilds each view's graph beside the installed one. Camera and session views never reconstruct.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: TemporalEcho(settings: settings));
                }

                if (ParseOnOff(token: args[0]) is not { } on) {
                    return CommandResult.Error(output: $"[world.temporal: unknown state '{args[0]}' — on|off]");
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
            bindability: CommandBindability.Unbindable,
            name: "world.shadow-amortize",
            description: "Reuses secondary shadow history with temporal reconstruction: world.shadow-amortize [on|off]. Slot zero and fading slots march fully. Other slots march one quarter-grid selected by the jitter index, rejecting history on light ownership, light motion, occluder motion, or receiver identity and depth changes.",
            handler: (context, args) => {
                CommandResult Echo() => new(Output: $"[world.shadow-amortize: {(settings.ShadowAmortize ? "on" : "off")}]");
                if (args.Count == 0) { return Echo(); }
                if (ParseOnOff(token: args[0]) is not { } on) {
                    return CommandResult.Error(output: $"[world.shadow-amortize: unknown state '{args[0]}' — on|off]");
                }
                return SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.ShadowAmortize,
                    a: (on ? 1.0 : 0.0), formatEcho: Echo);
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.sky-quality",
            description: "Sets the sky's quality tier, live: world.sky-quality [low|medium|high] — no argument echoes the current tier. A sky layer whose tier lies above it writes no entry and counts no work; below high each kind draws its reduced form (clouds take one thickness tap and three octaves at low, shaded flat, and three octaves at medium; stars stop twinkling at low; an aurora and a noise field take fewer octaves). The world's quality presets set it through their sky row.",
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
                    return CommandResult.Error(output: $"[world.sky-quality: unknown tier '{args[0]}' — low|medium|high]");
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
            bindability: CommandBindability.Unbindable,
            name: "world.quality",
            description: "Applies one of the world's authored graphics PRESETs (render.low, render.medium, render.high), each bundling the shadow, ambient-occlusion, temporal-reconstruction, dynamic-resolution, render-scale and sky-quality levers, live: world.quality low|medium|high — no argument echoes the current settings. A preset the world does not author is refused by name. A preset just writes the individual settings (world.shadows/.ao/.temporal/.render-scale still override afterward).",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribeQuality());
                }

                // The preset table is world data (WorldDefinition.Render), read off the LIVE definition so a mutated
                // preset table applies immediately: look the named tier up and write its levers into the live
                // settings.
                if (QualityTiers.Parse(name: args[0].ToString()) is not { } tier) {
                    return CommandResult.Error(output: $"[world.quality: unknown preset '{args[0]}' — {string.Join(separator: "|", values: QualityTiers.Names)}]");
                }

                if (server.Definition.Render.Preset(tier: tier) is not { } preset) {
                    return CommandResult.Error(output: $"[world.quality: this world authors no {QualityTiers.Name(tier: tier)} preset]");
                }

                SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.Shadows,
                    a: ShadowTiers.Scale(tier: preset.Shadows),
                    b: settings.ShadowCrowdRadius
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
                SubmitLever(link: link, principal: context.Principal, name: WorldSessionLevers.ShadowAmortize,
                    a: (preset.ShadowAmortize ? 1.0 : 0.0));
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

    private static readonly string[] CameraTileAliases = ["camera", "camera-tile", "tile"];
    private static readonly string[] ExactOrGather = ["exact", "gather"];
    // The spellings each adaptive lever's exact and fast sides answer to, beside the shared "auto".
    private static readonly string[] ExactOrQuality = ["exact", "quality"];
    private static readonly string[] FastOrFleet = ["fast", "fleet"];
}
