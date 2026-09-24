using System.Globalization;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The render levers: engine-wide render options any presentation shape honors — shadows and their crowd radius,
/// ambient occlusion and its quality, the far field, the unchanged-frame cadence gate, the shadow mask and march, render
/// scale, upscale sharpness, and the quality preset — each a live console verb that echoes its current value when
/// called with no argument. Every write is a session lever submitted through the server's grant check and lands in
/// <see cref="WorldRenderSettings"/>, which the frame source reads each captured frame; nothing here needs a window or
/// a presenter, so both the windowed and the offscreen presentation shapes compose it, and an offscreen collector or
/// canary can set the same levers a player can. Headless composes no renderer and refuses these as unknown.
/// </summary>
internal sealed class WorldRenderLeverCommandModule(WorldPopulation population, WorldRenderSettings settings, WorldServer server, IServerLink link) : ICommandModule {
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
            : "off")} render-scale={RenderScaleName(scale: settings.RenderScale)} upscale={UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)}]";
    }
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
    private static bool TryParseRenderScale(ReadOnlySpan<char> text, out float scale) {
        if (WorldRenderScaleTiers.TryParse(
            name: text.ToString(),
            tier: out var tier
        )) {
            scale = WorldRenderScaleTiers.Scale(tier: tier);

            return true;
        }

        var token = text.Trim();
        var percent = (!token.IsEmpty && (token[^1] == '%'));

        if (percent) {
            token = token[..^1];
        }

        if (!CommandArgs.TryParseFloat(
            text: token,
            value: out scale
        )) {
            return false;
        }

        if (percent) {
            scale /= 100f;
        }

        return (
            float.IsFinite(f: scale) &&
            (scale >= 0.125f) &&
            (scale <= 1f)
        );
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
            name: "world.shadow-mask",
            description: "Selects the soft-shadow candidate-mask path live: world.shadow-mask [auto|exact|camera-tile]. auto uses the exact per-tile grid gather (one shadow candidate mask per 8x8 workgroup, bit-identical to the flat march) below 16 simulated stand-ins and the fast camera-tile approximation at the 16/64/128 fleet tiers; exact and camera-tile force either side for visual/performance A/B.",
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
            bindability: CommandBindability.Unbindable,
            name: "world.render-scale",
            description: "Sets internal SDF resolution live (no rebuild): world.render-scale [native|three-quarter|half|quarter|eighth|0.125..1|12.5%..100%]. Every player view renders at that fraction and the compositor reconstructs it to output resolution using world.upscale-sharpness; native is the bit-exact copy path. Numeric values make fine-grained 120 FPS sweeps possible.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.render-scale: {RenderScaleName(scale: settings.RenderScale)} | named: {WorldRenderScaleTiers.ValidNames} | numeric: 12.5%..100%]");
                }

                if (!TryParseRenderScale(
                    text: args[0],
                    scale: out var scale
                )) {
                    return CommandResult.Error(output: $"[world.render-scale: invalid '{args[0]}' — named: {WorldRenderScaleTiers.ValidNames}; numeric: 0.125..1 or 12.5%..100%]");
                }

                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.RenderScale,
                    a: scale,
                    formatEcho: () => {
                        var liveScale = settings.RenderScale;
                        var pixelPercent = ((int)Math.Round(a: ((liveScale * liveScale) * 100f)));

                        return new CommandResult(Output: $"[world.render-scale: {RenderScaleName(scale: liveScale)} — ~{pixelPercent}% of native internal pixels; measure GPU work with world.counters gpu]");
                    }
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.upscale-sharpness",
            description: "Sets reduced-resolution reconstruction continuously, live: world.upscale-sharpness [bilinear|balanced|sharp|0..1|0%..100%]. Names alias 0/50/100%. Zero is the four-tap bilinear fast path; any positive value enables clamped Catmull-Rom and blends toward it; native render scale ignores this setting.",
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
            name: "world.quality",
            description: "Applies a graphics PRESET that bundles the individual levers, live: world.quality low|medium|high — no argument echoes the current settings. low = shadows off, ao off, render-scale half; medium = shadows medium, ao on, render-scale three-quarter; high = shadows high, ao on, render-scale native. A preset just writes the individual settings (world.shadows/.ao/.render-scale still override afterward).",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribeQuality());
                }

                // The preset table is world data (WorldDefinition.Render), read off the LIVE definition so a mutated
                // preset table applies immediately: look the named tier up and write its three levers into the live
                // settings.
                if (server.Definition.Render.Preset(name: args[0].ToString()) is not { } preset) {
                    return CommandResult.Error(output: $"[world.quality: unknown preset '{args[0]}' — low|medium|high]");
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

                // The echo formats INSIDE the LAST lever's completion — all three have applied (or the last was
                // refused) by the time formatEcho runs, since loopback drains each inline before its Submit* returns.
                return SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.RenderScale,
                    a: WorldRenderScaleTiers.Scale(tier: preset.RenderScale),
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
