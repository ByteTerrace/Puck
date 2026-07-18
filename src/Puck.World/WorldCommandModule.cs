using System.Globalization;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Launcher;
using Puck.Scene;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using static Puck.Commands.CommandArgs;

namespace Puck.World;

/// <summary>
/// The world's own console surface — its performance readouts (<c>world.fps</c>, <c>world.gpu</c>), the participant
/// tables (<c>world.players</c>, <c>world.devices</c>), and the graphics options (shadows, ambient occlusion, render
/// scale, an FPS target, a quality preset) — all live console verbs, each echoing the current value when called with no
/// argument. Metrics are armed and read over the pipe (<c>world.timing</c> / <c>world.gpu</c>), not through an
/// environment variable. Every setting rides <see cref="WorldRenderSettings"/> or a live control
/// (<see cref="PresentPacingControl"/>, <see cref="GpuTimingControl"/>), read by the frame source each captured frame.
/// </summary>
internal sealed class WorldCommandModule(FrameRateMonitor frameRate, PresentPacingControl pacing, PlayerRoster roster, WorldPopulation population, WorldRenderSettings settings, WorldRenderProbe renderProbe, WorldServer server, WorldScreenBinder screens, WorldEngagement engagement, IServerLink link) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.shadows",
            description: "Sets continuous ENGINE-WIDE soft-shadow reach and CROWD RADIUS, live (no rebuild): world.shadows [off|low|medium|high|0..1|0%..100%] [crowd-radius]. Names alias 0/25/50/100%; numeric input is continuous. The optional 0..100 world-unit crowd radius bounds WHO casts; farther avatars still render but leave the shadow march.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: ShadowEcho(settings: settings));
                }

                if (!TryParseShadowReach(text: args[0], reach: out var reach)) {
                    return new CommandResult(Output: $"[world.shadows: invalid reach '{args[0]}' — off|low|medium|high, 0..1, or 0%..100%]");
                }

                if (args.Length >= 2) {
                    if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var radius) || (radius < 0f) || (radius > 100f)) {
                        return new CommandResult(Output: $"[world.shadows: bad crowd-radius '{args[1]}' — a number 0..100]");
                    }

                    settings.ShadowCrowdRadius = radius;
                }

                settings.ShadowReach = reach;

                return new CommandResult(Output: ShadowEcho(settings: settings));
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.ao",
            description: "Toggles ambient occlusion engine-wide, live (no rebuild): world.ao [on|off] — no argument echoes the current state. AO darkens creases and contact seams; turning it off skips the per-lit-pixel occlusion march (a small GPU saving, see world.gpu).",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: $"[world.ao: {(settings.AmbientOcclusion ? "on" : "off")}]");
                }

                bool? on = args[0].ToUpperInvariant() switch {
                    "ON" => true,
                    "OFF" => false,
                    _ => null,
                };

                if (on is not { } resolved) {
                    return new CommandResult(Output: $"[world.ao: unknown state '{args[0]}' — on|off]");
                }

                settings.AmbientOcclusion = resolved;

                return new CommandResult(Output: $"[world.ao: {(resolved ? "on" : "off")}]");
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.far-field",
            description: "Toggles the far-field termination optimizations live (no rebuild) — the isolators for the owner's paired A/B: world.far-field [on|off|status] moves BOTH lanes together; world.far-field bound [on|off] is the F1 beam-published per-tile far bound (output-identical, skips empty-sky march steps); world.far-field shadow [on|off] is the F2 soft-shadow light-side early exit (a march-path change). No argument (or 'status') echoes both. Both ship ON; 'off' is the paired-run baseline.",
            handler: (_, args) => {
                if ((args.Length == 0) || string.Equals(a: args[0], b: "status", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return new CommandResult(Output: FarFieldEcho(settings: settings));
                }

                var head = args[0].ToUpperInvariant();

                // Lane-scoped form: world.far-field bound|shadow on|off.
                if ((head == "BOUND") || (head == "SHADOW")) {
                    if ((args.Length < 2) || (ParseOnOff(token: args[1]) is not { } laneState)) {
                        return new CommandResult(Output: $"[world.far-field: expected '{args[0].ToLowerInvariant()} on|off']");
                    }

                    if (head == "BOUND") {
                        settings.FarBound = laneState;
                    }
                    else {
                        settings.ShadowFarExit = laneState;
                    }

                    return new CommandResult(Output: FarFieldEcho(settings: settings));
                }

                // Bare form: world.far-field on|off drives BOTH lanes.
                if (ParseOnOff(token: args[0]) is not { } bothState) {
                    return new CommandResult(Output: $"[world.far-field: unknown '{string.Join(separator: ' ', value: args)}' — on|off|status, or bound|shadow on|off]");
                }

                settings.FarBound = bothState;
                settings.ShadowFarExit = bothState;

                return new CommandResult(Output: FarFieldEcho(settings: settings));
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.shadow-mask",
            description: "Selects the soft-shadow candidate-mask path live: world.shadow-mask [auto|exact|camera-tile]. auto uses exact per-pixel grid gathers below 16 simulated stand-ins and the fast camera-tile approximation at the 16/64/128 fleet tiers; exact and camera-tile force either side for visual/performance A/B.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: DescribeShadowMask());
                }

                ShadowMaskMode? mode = args[0].ToUpperInvariant() switch {
                    "AUTO" => ShadowMaskMode.Auto,
                    "EXACT" or "GATHER" => ShadowMaskMode.ExactGather,
                    "CAMERA" or "CAMERA-TILE" or "TILE" => ShadowMaskMode.CameraTile,
                    _ => null,
                };

                if (mode is not { } resolved) {
                    return new CommandResult(Output: $"[world.shadow-mask: unknown mode '{args[0]}' — auto|exact|camera-tile]");
                }

                settings.ShadowMask = resolved;

                return new CommandResult(Output: DescribeShadowMask());
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.ao-quality",
            description: "Selects the ambient-occlusion sampler live: world.ao-quality [auto|exact|fast]. auto keeps the three-rung quality ladder below 16 simulated stand-ins and uses the calibrated one-sample contact path at the 16/64/128 fleet tiers; exact and fast force either side for visual/performance A/B.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: DescribeAmbientOcclusionQuality());
                }

                AmbientOcclusionMode? mode = args[0].ToUpperInvariant() switch {
                    "AUTO" => AmbientOcclusionMode.Auto,
                    "EXACT" or "QUALITY" => AmbientOcclusionMode.Exact,
                    "FAST" or "FLEET" => AmbientOcclusionMode.Fast,
                    _ => null,
                };

                if (mode is not { } resolved) {
                    return new CommandResult(Output: $"[world.ao-quality: unknown mode '{args[0]}' — auto|exact|fast]");
                }

                settings.AmbientOcclusionQuality = resolved;

                return new CommandResult(Output: DescribeAmbientOcclusionQuality());
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.shadow-march",
            description: "Selects the soft-shadow marcher live: world.shadow-march [auto|exact|fast]. auto keeps the exact 48-step, 12-unit path below 16 simulated stand-ins and uses the bounded-cost 16-step, 6-unit near-field path at the 16/64/128 fleet tiers; exact and fast force either side for visual/performance A/B.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: DescribeShadowMarch());
                }

                ShadowMarchMode? mode = args[0].ToUpperInvariant() switch {
                    "AUTO" => ShadowMarchMode.Auto,
                    "EXACT" or "QUALITY" => ShadowMarchMode.Exact,
                    "FAST" or "FLEET" => ShadowMarchMode.Fast,
                    _ => null,
                };

                if (mode is not { } resolved) {
                    return new CommandResult(Output: $"[world.shadow-march: unknown mode '{args[0]}' — auto|exact|fast]");
                }

                settings.ShadowMarch = resolved;

                return new CommandResult(Output: DescribeShadowMarch());
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.view-refresh",
            description: "Sets the diegetic views' deterministic offscreen refresh cadence: world.view-refresh [1..8]. 1 renders every produced frame; 4 (the default) renders every fourth frame and preserves the previous images between refreshes. No argument echoes the current divisor and how many camera views are registered in the offscreen pool (a removed View screen releases its camera's render, dropping that count).",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: $"[world.view-refresh: every {screens.ViewRefreshDivisor} produced frame(s); {screens.ActiveCameraViewCount} camera view(s) registered]");
                }

                if (!TryParseInt(text: args[0], value: out var divisor) || (divisor < 1) || (divisor > 8)) {
                    return new CommandResult(Output: $"[world.view-refresh: expected an integer divisor from 1 through 8, got '{args[0]}']");
                }

                screens.SetViewRefreshDivisor(divisor: divisor);

                return new CommandResult(Output: $"[world.view-refresh: every {divisor} produced frame(s)]");
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.debug-view",
            description: "Selects the live SDF diagnostic output for every World camera: world.debug-view [off|depth|normals|raydir|material-id|iteration-count|termination|slice|mask|overshoot]. Depth is the primary-march-only performance probe; off restores final shading.",
            handler: (_, args) => {
                if (renderProbe.Node is not { } node) {
                    return new CommandResult(Output: "[world.debug-view: renderer not built yet]");
                }

                if (args.Length == 0) {
                    return new CommandResult(Output: $"[world.debug-view: {DebugViewModes.Name(mode: node.DebugMode)}]");
                }

                if ((args.Length != 1) || !DebugViewModes.TryParse(name: args[0], mode: out var mode)) {
                    return new CommandResult(Output: $"[world.debug-view: unknown mode '{string.Join(separator: " ", value: args)}' — {string.Join(separator: '|', value: DebugViewModes.Names)}]");
                }

                node.DebugMode = mode;

                return new CommandResult(Output: $"[world.debug-view: {DebugViewModes.Name(mode: mode)}]");
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.render-scale",
            description: "Sets internal SDF resolution live (no rebuild): world.render-scale [native|three-quarter|half|quarter|eighth|0.125..1|12.5%..100%]. Every player view renders at that fraction and the compositor reconstructs it to output resolution using world.upscale-sharpness; native is the bit-exact copy path. Numeric values make fine-grained 120 FPS sweeps possible.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: $"[world.render-scale: {RenderScaleName(scale: settings.RenderScale)} | named: {WorldRenderScaleTiers.ValidNames} | numeric: 12.5%..100%]");
                }

                if (!TryParseRenderScale(text: args[0], scale: out var scale)) {
                    return new CommandResult(Output: $"[world.render-scale: invalid '{args[0]}' — named: {WorldRenderScaleTiers.ValidNames}; numeric: 0.125..1 or 12.5%..100%]");
                }

                settings.RenderScale = scale;

                var pixelPercent = (int)Math.Round(a: ((scale * scale) * 100f));

                return new CommandResult(Output: $"[world.render-scale: {RenderScaleName(scale: scale)} — ~{pixelPercent}% of native internal pixels; measure GPU cost with world.gpu]");
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.upscale-sharpness",
            description: "Sets reduced-resolution reconstruction continuously, live: world.upscale-sharpness [bilinear|balanced|sharp|0..1|0%..100%]. Names alias 0/50/100%. Zero is the four-tap bilinear fast path; any positive value enables clamped Catmull-Rom and blends toward it; native render scale ignores this setting.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: $"[world.upscale-sharpness: {UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)}]");
                }

                if (!TryParseUpscaleSharpness(text: args[0], sharpness: out var sharpness)) {
                    return new CommandResult(Output: $"[world.upscale-sharpness: invalid '{args[0]}' — bilinear|balanced|sharp, 0..1, or 0%..100%]");
                }

                settings.UpscaleSharpness = sharpness;

                return new CommandResult(Output: $"[world.upscale-sharpness: {UpscaleSharpnessName(sharpness: sharpness)}]");
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.target",
            description: "Sets the continuous presentation target live: world.target [<hz>|display]. <hz> is any positive finite number, capped by the effective display ceiling. 'display' (or 'vrr') uses verified VRR bounds when advertised and otherwise the active signal timing. Presentation only; present-mode switching remains a boot option.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: $"[world.target: {DescribeTarget(target: pacing.TargetHertz)}]");
                }

                var token = args[0].ToUpperInvariant();

                if ((token == "DISPLAY") || (token == "VRR")) {
                    pacing.SetTargetHertz(targetHertz: 0.0);

                    return new CommandResult(Output: $"[world.target: {DescribeTarget(target: 0.0)}]");
                }

                if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) || !double.IsFinite(hz) || (hz <= 0.0)) {
                    return new CommandResult(Output: "[world.target: expected a positive finite Hz value, or 'display'/'vrr' for automatic display pacing]");
                }

                pacing.SetTargetHertz(targetHertz: hz);

                return new CommandResult(Output: $"[world.target: {DescribeTarget(target: hz)}]");
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.quality",
            description: "Applies a graphics PRESET that bundles the individual levers, live: world.quality low|medium|high — no argument echoes the current settings. low = shadows off, ao off, render-scale half; medium = shadows medium, ao on, render-scale three-quarter; high = shadows high, ao on, render-scale native. A preset just writes the individual settings (world.shadows/.ao/.render-scale still override afterward).",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: DescribeQuality());
                }

                // The preset table is world data (WorldDefinition.Render), read off the LIVE definition so a mutated
                // preset table applies immediately: look the named tier up and write its three levers into the live
                // settings.
                if (server.Definition.Render.Preset(name: args[0]) is not { } preset) {
                    return new CommandResult(Output: $"[world.quality: unknown preset '{args[0]}' — low|medium|high]");
                }

                settings.ShadowReach = ShadowTiers.Scale(tier: preset.Shadows);
                settings.AmbientOcclusion = preset.AmbientOcclusion;
                settings.RenderScale = WorldRenderScaleTiers.Scale(tier: preset.RenderScale);

                return new CommandResult(Output: DescribeQuality());
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.timing",
            description: "Arms per-pass GPU timing engine-wide, live (no restart, no magic env var): world.timing [on|off] — no argument echoes the armed state. On lights BOTH the GPU per-pass digest (readable with world.gpu) and the launcher's CPU frame-timing hub; performance metrics are a first-class citizen here.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: $"[world.timing: {(GpuTimingControl.Shared.Armed ? "on" : "off")}]");
                }

                bool? on = args[0].ToUpperInvariant() switch {
                    "ON" => true,
                    "OFF" => false,
                    _ => null,
                };

                if (on is not { } resolved) {
                    return new CommandResult(Output: $"[world.timing: unknown state '{args[0]}' — on|off]");
                }

                GpuTimingControl.Shared.SetArmed(armed: resolved);

                return new CommandResult(Output: $"[world.timing: {(resolved ? "on" : "off")}]");
            }
        );
        yield return CommandDefinition.Verb(
            name: "world.gpu",
            description: "Echoes the previous frame's per-pass GPU milliseconds — the whole-frame total plus each render pass (mask/beam/cull-args/views/composite) — read live off the renderer. Arm it first with world.timing on; the metrics are first-class, no env var needed.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: DescribeGpu())
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.population",
            description: "Sets how many SIMULATED network stand-ins the world renders behind the four local seats, and how they behave between scripted tapes — the network-population simulator: world.population [count] [idle|wander] (tokens are order-independent; no argument echoes both). A bare integer 0..124 sets the active stand-in COUNT; a bare 'idle'/'wander' sets the BEHAVIOR. It answers 'what do N real people cost to render' by drawing that many avatars TODAY, before any wire exists — the up-to-128-player scale (16/64/128 tiers) proven as a render cost. Behavior 'wander' (default) has the stand-ins gently drift as a living crowd; 'idle' makes them hold still between player.run tape segments so a SCRIPTED CORPUS is the sole driver of entries 5..128 — the remote-server stand-in posture the 128-player stdin proof pipes. Arm world.timing on and read world.gpu to measure.",
            handler: (context, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: DescribePopulation());
                }

                int? count = null;
                IntentSource? behavior = null;

                // Order-independent tokens: each is either a bare integer count or a bare idle/wander keyword. A repeat
                // of either lane, or an unrecognized token, is rejected whole so a typo never half-applies.
                foreach (var token in args) {
                    switch (token.ToUpperInvariant()) {
                        case "IDLE":
                            if (behavior is not null) {
                                return new CommandResult(Output: $"[world.population: behavior given twice — one of idle|wander]");
                            }

                            behavior = IntentSource.Idle;

                            break;
                        case "WANDER":
                            if (behavior is not null) {
                                return new CommandResult(Output: $"[world.population: behavior given twice — one of idle|wander]");
                            }

                            behavior = IntentSource.Wander;

                            break;
                        default:
                            if (!TryParseInt(text: token, value: out var parsed) || (parsed < 0) || (parsed > WorldPopulation.MaxSimulated)) {
                                return new CommandResult(Output: $"[world.population: unknown token '{token}' — a count 0..{WorldPopulation.MaxSimulated} and/or idle|wander]");
                            }

                            if (count is not null) {
                                return new CommandResult(Output: $"[world.population: count given twice — one integer 0..{WorldPopulation.MaxSimulated}]");
                            }

                            count = parsed;

                            break;
                    }
                }

                // The census and peer source are session requests to the authoritative server (synchronous
                // request/reply), so the echo below reads the applied state. An explicit idle/wander token sets the
                // peer-source DEFAULT and sweeps ALL peers (4..127) to it — last-writer-wins, so a per-entity
                // player.control does not survive the global flip; a count alone leaves existing peers' sources be.
                if (count is { } resolvedCount) {
                    _ = link.SubmitSession(request: new SessionRequest.SetPopulation(Principal: WorldPrincipal.Console, Count: resolvedCount));
                }

                if (behavior is { } resolvedBehavior) {
                    _ = link.SubmitSession(request: new SessionRequest.SetPeerSource(Principal: WorldPrincipal.Console, Source: resolvedBehavior));
                }

                return new CommandResult(Output: DescribePopulation());
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.Verb(
            name: "world.players",
            description: "Lists the roster's four slots — joined/empty, each joined slot's profile, state (active/PENDING), owned devices (or origin), and pose (p<N> name state(devices) pos=(x, z) yaw=d°) — plus the population line (local seats + simulated stand-ins). Every player is a networked player; a local pad or the keyboard is just one at zero latency.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: DescribePlayers())
        );
        yield return CommandDefinition.Verb(
            name: "world.devices",
            description: "Lists every input device seen this session by its stable token (kbd, pad1, pad2, …) in first-seen order and the player it currently drives (p<N> or unassigned). The reassignment verbs — player.assign / player.cycle / player.claim — move a device between players.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: roster.DescribeDevices())
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.screens",
            description: "Lists every declared diegetic screen, one segment each — index, source kind (test-pattern|none|machine|camera|view|capture; a machine reads machine:<engine>), bound/unbound (a nonzero live provider handle this frame), and its engage policy (engageable|fixed). No argument; the pipe-assertable state proving the test-pattern screen is bound and the unbound screen falls back to the engine's procedural no-signal card (never black). A query — its listing always echoes, even under wire.ack quiet.",
            handler: ScreensHandler,
            echoesData: true
        );
        yield return CommandDefinition.Verb(
            name: "world.fps",
            description: "Echoes the measured frame rate over the recent window — avg, the slowest single frame (the floor check), the sample count — and the pacer's current target. The world's reference desktop contract is 120 FPS under VRR.",
            valueKind: CommandValueKind.Digital,
            handler: _ => {
                var (averageFps, worstFps, frameCount) = frameRate.Summarize();

                if (frameCount == 0) {
                    return new CommandResult(Output: "[world.fps: no frames sampled yet]");
                }

                var target = pacing.TargetHertz;
                var pacer = ((target > 0.0)
                    ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{target:0.###} Hz")
                    : "automatic (verified VRR range or active signal timing)");

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.fps: avg={averageFps:0.0} worst={worstFps:0.0} over {frameCount} frames | pacer: {pacer}]"
                ));
            }
        );
    }

    // The world.screens listing: one segment per declared screen — index, source kind, live bound/unbound state (a
    // nonzero provider handle this frame), and engage policy. A query (EchoesData): its listing always surfaces, so a
    // piped proof can assert the test-pattern screen is bound and the None screen stays unbound (procedural fallback).
    private CommandResult ScreensHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return new CommandResult(Output: "[world.screens: no arguments — lists every declared screen]") {
                IsError = true,
            };
        }

        // The LIVE definition's rows (never the boot snapshot), so a screen mutation's new source narrates honestly.
        var declaredScreens = server.Definition.Screens;

        if (declaredScreens.Count == 0) {
            return new CommandResult(Output: "[world.screens: none declared]");
        }

        var builder = new StringBuilder(value: "[world.screens:");

        for (var index = 0; (index < declaredScreens.Count); index++) {
            var screen = declaredScreens[index];
            var bound = (screens.CurrentHandle(index: screen.Index) != 0);
            // The engaged marker (only when players are engaged) — reflects the route state, kept bracket-agnostic so the
            // proof regexes are undisturbed.
            var engaged = engagement.PlayersOn(screenIndex: screen.Index);
            var engagedText = ((engaged.Count > 0) ? $" engaged:{string.Join(separator: "+", values: engaged.Select(selector: static n => $"p{n}"))}" : "");

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{((index == 0) ? " " : " | ")}{screen.Index} {ScreenSourceKind(source: screen.Source)} {(bound ? "bound" : "unbound")} {(screen.Route.Engageable ? "engageable" : "fixed")}{engagedText}"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }

    // The source-kind keyword for a screen's declared source — the stable token a piped proof asserts against.
    private static string ScreenSourceKind(WorldScreenSource source) {
        return source switch {
            WorldScreenSource.TestPattern => "test-pattern",
            WorldScreenSource.Machine machine => $"machine:{machine.Engine}",
            WorldScreenSource.Camera => "camera",
            WorldScreenSource.View => "view",
            WorldScreenSource.Capture => "capture",
            _ => "none",
        };
    }
    // The world.shadows echo: continuous reach plus crowd radius; named-notch values render through their facade.
    private static string ShadowEcho(WorldRenderSettings settings) {
        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.shadows: {ShadowTiers.Name(reach: settings.ShadowReach)} | crowd {settings.ShadowCrowdRadius:0.##}]");
    }

    // The world.far-field echo: both isolator lanes (F1 bound, F2 shadow exit) and their on/off state.
    private static string FarFieldEcho(WorldRenderSettings settings) {
        return $"[world.far-field: bound {(settings.FarBound ? "on" : "off")}, shadow {(settings.ShadowFarExit ? "on" : "off")}]";
    }

    // Shared on/off token parse for the boolean isolator verbs (null = unrecognized).
    private static bool? ParseOnOff(string token) {
        return token.ToUpperInvariant() switch {
            "ON" => true,
            "OFF" => false,
            _ => null,
        };
    }

    // The FPS-target readout: a set rate paces to that Hz; 0 is automatic display pacing.
    private static string DescribeTarget(double target) {
        return ((target > 0.0)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{target:0.###} Hz — the display-aware pacer targets this rate")
            : "display (automatic — verified VRR capabilities or active signal timing)");
    }

    // The world.quality echo: the current individual settings the preset (or a later override) left in place.
    private string DescribeQuality() {
        return $"[world.quality: shadows={ShadowTiers.Name(reach: settings.ShadowReach)} ao={(settings.AmbientOcclusion ? "on" : "off")} render-scale={RenderScaleName(scale: settings.RenderScale)} upscale={UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)}]";
    }

    private static bool TryParseShadowReach(string text, out float reach) {
        reach = text.ToUpperInvariant() switch {
            "OFF" => 0f,
            "LOW" => 0.25f,
            "MEDIUM" => 0.5f,
            "HIGH" or "ON" => 1f,
            _ => float.NaN,
        };

        if (!float.IsNaN(f: reach)) {
            return true;
        }

        var token = text.Trim();
        var percent = token.EndsWith(value: '%');

        if (percent) {
            token = token[..^1];
        }

        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out reach)) {
            return false;
        }

        if (percent) {
            reach /= 100f;
        }

        return float.IsFinite(f: reach) && (reach >= 0f) && (reach <= 1f);
    }

    private static bool TryParseRenderScale(string text, out float scale) {
        if (WorldRenderScaleTiers.TryParse(name: text, tier: out var tier)) {
            scale = WorldRenderScaleTiers.Scale(tier: tier);

            return true;
        }

        var token = text.Trim();
        var percent = token.EndsWith(value: '%');

        if (percent) {
            token = token[..^1];
        }

        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out scale)) {
            return false;
        }

        if (percent) {
            scale /= 100f;
        }

        return float.IsFinite(f: scale) && (scale >= 0.125f) && (scale <= 1f);
    }

    private static bool TryParseUpscaleSharpness(string text, out float sharpness) {
        sharpness = text.ToUpperInvariant() switch {
            "BILINEAR" or "OFF" => 0f,
            "BALANCED" => 0.5f,
            "SHARP" => 1f,
            _ => float.NaN,
        };

        if (!float.IsNaN(f: sharpness)) {
            return true;
        }

        var token = text.Trim();
        var percent = token.EndsWith(value: '%');

        if (percent) {
            token = token[..^1];
        }

        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out sharpness)) {
            return false;
        }

        if (percent) {
            sharpness /= 100f;
        }

        return float.IsFinite(f: sharpness) && (sharpness >= 0f) && (sharpness <= 1f);
    }

    private static string RenderScaleName(float scale) {
        foreach (var tier in Enum.GetValues<WorldRenderScaleTier>()) {
            if (MathF.Abs(x: (scale - WorldRenderScaleTiers.Scale(tier: tier))) <= (0.5f / 255f)) {
                return WorldRenderScaleTiers.Name(tier: tier);
            }
        }

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(scale * 100f):0.#}%");
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

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(sharpness * 100f):0.#}%");
    }

    private string DescribeShadowMask() {
        var configured = settings.ShadowMask switch {
            ShadowMaskMode.ExactGather => "exact",
            ShadowMaskMode.CameraTile => "camera-tile",
            _ => "auto",
        };
        var resolved = (settings.ShadowMask switch {
            ShadowMaskMode.ExactGather => false,
            ShadowMaskMode.CameraTile => true,
            _ => (population.SimulatedCount >= 16),
        }) ? "camera-tile" : "exact";

        return $"[world.shadow-mask: {configured} → {resolved} | simulated={population.SimulatedCount}]";
    }

    private string DescribeShadowMarch() {
        var configured = settings.ShadowMarch switch {
            ShadowMarchMode.Exact => "exact",
            ShadowMarchMode.Fast => "fast",
            _ => "auto",
        };
        var resolved = (settings.ShadowMarch switch {
            ShadowMarchMode.Exact => false,
            ShadowMarchMode.Fast => true,
            _ => (population.SimulatedCount >= 16),
        }) ? "fast" : "exact";

        return $"[world.shadow-march: {configured} → {resolved} | simulated={population.SimulatedCount}]";
    }

    private string DescribeAmbientOcclusionQuality() {
        var configured = settings.AmbientOcclusionQuality switch {
            AmbientOcclusionMode.Exact => "exact",
            AmbientOcclusionMode.Fast => "fast",
            _ => "auto",
        };
        var resolved = (settings.AmbientOcclusionQuality switch {
            AmbientOcclusionMode.Exact => false,
            AmbientOcclusionMode.Fast => true,
            _ => (population.SimulatedCount >= 16),
        }) ? "fast" : "exact";

        return $"[world.ao-quality: {configured} → {resolved} | simulated={population.SimulatedCount}]";
    }

    // The world.players readout: the roster's four slots plus the population line spliced in as a trailing segment.
    // roster.Describe() ends with ']', so drop it (the [..^1] slice) and re-close after the population segment.
    private string DescribePlayers() {
        var players = roster.Describe();
        var local = roster.Count;
        var simulated = population.SimulatedCount;

        return $"{players[..^1]} | population: {local} local + {simulated} network = {(local + simulated)}/{WorldPopulation.MaxPopulation}]";
    }

    // The world.population readout: the active simulated count, the between-tapes behavior, and the total avatar load on
    // the renderer. LOOPBACK-ONLY: the population reads here (and in the Describe*/auto-tier echoes above) are
    // in-process; a socket transport replaces them with a link query the server composes.
    private string DescribePopulation() {
        var local = roster.Count;
        var simulated = population.SimulatedCount;
        var behavior = (population.DefaultPeerSource switch {
            IntentSource.Idle => "idle",
            IntentSource.Wander => "wander",
            _ => "live",
        });
        var workload = WorldAvatarCatalog.ActiveWorkload(isActive: population.IsActive);
        // The per-kit census derives its names and counts from the definition rows, in row order.
        var counts = population.ActiveKitCounts();
        var kits = string.Join(separator: " ", values: server.Definition.Kits.Select(selector: (kit, row) => $"{kit.Name}={counts[row]}"));

        return $"[world.population: {simulated} network-human stand-ins active (0..{WorldPopulation.MaxSimulated}), behavior {behavior} | {local} local + {simulated} = {(local + simulated)}/{WorldPopulation.MaxPopulation} avatars rendered | archetypes {kits} | unique deterministic rigs {WorldAvatarCatalog.MinInstructionCount}..{WorldAvatarCatalog.MaxInstructionCount} instructions/avatar; active {workload.Leaves} leaf instances, {workload.Instructions} authored VM instructions]";
    }

    // The world.gpu readout: the previous frame's per-pass GPU ms, read live off the render probe's engine node.
    private string DescribeGpu() {
        if (renderProbe.Node is not { } node) {
            return "[world.gpu: renderer not built yet]";
        }

        Span<double> passMilliseconds = stackalloc double[SdfEngineNode.PassTimingCount];

        if (!node.TryReadPassTimings(passMilliseconds: passMilliseconds, passCount: out var passCount, frame: out var frame)) {
            return "[world.gpu: timing off — world.timing on]";
        }

        var builder = new StringBuilder(value: "[world.gpu:");
        var labels = SdfEngineNode.PassTimingLabels;

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" frame {frame:0.00}ms");

        for (var index = 0; (index < passCount); index++) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" | {labels[index]} {passMilliseconds[index]:0.00}");
        }

        return builder.Append(value: ']').ToString();
    }
}
