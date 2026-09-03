using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The world's own PRESENTATION console surface — its performance readouts (<c>world.fps</c>, <c>world.gpu</c>), the
/// declared-row tables (<c>world.screens</c>, <c>world.cameras</c>), and the graphics options (shadows, ambient
/// occlusion, render scale, an FPS target, a quality preset) — all live console verbs, each echoing the current value
/// when called with no argument. Registered ONLY when presentation is composed (<c>AddWorldPresentation</c>); over
/// headless stdin every one of these refuses as unknown. The participant/census verbs (<c>world.players</c>,
/// <c>world.devices</c>, <c>world.population</c>) and authoritative diagnostics (<c>world.navigation</c>,
/// <c>world.budget</c>) moved to <see cref="WorldPopulationCommandModule"/> — server-safe, registered in core either
/// way. Metrics are armed and read over the pipe (<c>world.timing</c> / <c>world.gpu</c>),
/// not through an environment variable. Every setting rides <see cref="WorldRenderSettings"/> or a live control
/// (<see cref="PresentPacingControl"/>, <see cref="GpuTimingControl"/>), read by the frame source each captured frame.
/// </summary>
internal sealed class WorldCommandModule(FrameRateMonitor frameRate, PresentPacingControl pacing, WorldPopulation population, WorldRenderSettings settings, WorldRenderProbe renderProbe, WorldServer server, WorldScreenBinder screens, IServerLink link, WorldOverlayFacts facts, PlayerRoster roster) : ICommandModule {
    // A ranked camera's listing: every candidate's anchor kind in rank order, then the candidate currently winning
    // for each joined seat (a seat-relative list can win differently per seat).
    private string CameraAnchorCandidates(WorldCamera camera) {
        var candidates = camera.Anchors!;
        var builder = new StringBuilder(value: "anchors=[");

        for (var index = 0; (index < candidates.Count); index++) {
            _ = builder.Append(value: ((index == 0) ? "" : ",")).Append(value: CameraAnchorKind(anchor: candidates[index].Anchor));
        }

        _ = builder.Append(value: "] winner=");

        var any = false;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!roster.IsJoined(slot: slot)) {
                continue;
            }

            _ = WorldSeatAnchors.SelectAnchor(
                camera: camera,
                candidateIndex: out var winner,
                evaluator: facts,
                slot: slot
            );
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{(any ? "," : "")}seat{PlayerRoster.DisplayNumber(slot: slot)}:{((winner >= 0) ? winner.ToString(provider: CultureInfo.InvariantCulture) : "none")}"
            );
            any = true;
        }

        return (any ? builder.ToString() : builder.Append(value: "none").ToString());
    }
    // The anchor keyword for a camera's declared ride — kind plus the target it names, the stable token a piped proof
    // asserts against. An unanchored camera's own offset IS its world position, so it reads 'none'.
    private static string CameraAnchorKind(WorldAnchor? anchor) {
        return anchor switch {
            WorldAnchor.Entity entity => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"anchor=entity:{entity.Index}"
        ),
            WorldAnchor.EntityPart part => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"anchor=entityPart:{part.Index}/{part.PartId}"
        ),
            WorldAnchor.Placement placement => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"anchor=placement:{placement.PlacementId}{((placement.ShapeId is { } shape)
            ? $"/{shape}"
            : "")}"
        ),
            WorldAnchor.Group group => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"anchor=group:{((group.Indices is { } indices)
            ? indices.Count.ToString(provider: CultureInfo.InvariantCulture)
            : "all")}"
        ),
            WorldAnchor.Seat seat => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"anchor=seat:{((seat.Number is { } number)
            ? number.ToString(provider: CultureInfo.InvariantCulture)
            : "enclosing")}{((seat.PartId is { } seatPart)
            ? $"/{seatPart}"
            : "")}"
        ),
            WorldAnchor.RecentSpeaker speaker => $"anchor=recentSpeaker{((speaker.PartId is { } speakerPart)
            ? $"/{speakerPart}"
            : "")}",
            _ => "anchor=none",
        };
    }
    // A camera rig is an authored op-list program: the listing names it and its ops in evaluation order, which is the
    // whole framing — there is no separate motion/aim kind left to report.
    private static string CameraRigKind(WorldCameraProgram rig) {
        var operations = rig.Operations;
        var opcodes = new string[operations.Count];

        for (var index = 0; (index < operations.Count); index++) {
            opcodes[index] = operations[index].Opcode;
        }

        return $"program={rig.Name} ops={string.Join(
            separator: ',',
            values: opcodes
        )}";
    }
    // The world.cameras listing: one segment per declared camera — name, the anchor it rides, the rig it frames with,
    // and its offscreen render dimensions. Reads the LIVE definition (never the boot snapshot), so a camera mutation's
    // new row narrates honestly. A query (not AcknowledgementOnly): its listing always surfaces.
    private CommandResult CamerasHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return CommandResult.Error(output: "[world.cameras: no arguments — lists every declared camera]");
        }

        var cameras = server.Definition.Cameras;

        if (cameras.Count == 0) {
            return new CommandResult(Output: "[world.cameras: none declared]");
        }

        var builder = new StringBuilder(value: "[world.cameras:");

        for (var index = 0; (index < cameras.Count); index++) {
            var camera = cameras[index];

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{((index == 0)
                ? " "
                : " | ")}{camera.Name} {((camera.Anchors is { Count: > 0 })
                ? CameraAnchorCandidates(camera: camera)
                : CameraAnchorKind(anchor: camera.Anchor))} {CameraRigKind(rig: camera.Rig)} {camera.RenderWidth}x{camera.RenderHeight}"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    /// <summary>Owns the automatic population threshold and readout shape shared by adaptive render-quality levers.</summary>
    private string DescribeAdaptiveQuality(string verb, (string Configured, bool? Fast) modes, string exact, string fast) {
        var resolved = ((modes.Fast ?? (population.SimulatedCount >= 16))
            ? fast
            : exact
        );

        return $"[{verb}: {modes.Configured} → {resolved} | simulated={population.SimulatedCount}]";
    }
    private string DescribeAmbientOcclusionQuality() =>
        DescribeAdaptiveQuality(
            verb: "world.ao-quality",
            modes: settings.AmbientOcclusionQuality switch {
                AmbientOcclusionMode.Exact => ("exact", false),
                AmbientOcclusionMode.Fast => ("fast", true),
                _ => ("auto", ((bool?)null)),
            },
            exact: "exact",
            fast: "fast"
        );
    // The world.gpu readout: the previous frame's per-pass GPU ms, read live off the render probe's engine node.
    private string DescribeGpu() {
        if (renderProbe.Node is not { } node) {
            return "[world.gpu: renderer not built yet]";
        }

        Span<double> passMilliseconds = stackalloc double[SdfEngineNode.PassTimingCount];

        if (!node.TryReadPassTimings(
            frame: out var frame,
            passCount: out var passCount,
            passMilliseconds: passMilliseconds
        )) {
            return "[world.gpu: timing off — world.timing on]";
        }

        var builder = new StringBuilder(value: "[world.gpu:");
        var labels = SdfEngineNode.PassTimingLabels;

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" frame {frame:0.00}ms"
        );

        for (var index = 0; (index < passCount); index++) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" | {labels[index]} {passMilliseconds[index]:0.00}"
            );
        }

        // The unified overlay decorator's own pass (a separate submit after the engine's) — appended once the
        // overlay has drawn a timed frame.
        if (renderProbe.Overlay is { } overlay) {
            Span<double> overlayMilliseconds = stackalloc double[1];

            if (
                overlay.TryReadPassTimings(
                frameMilliseconds: out _,
                passCount: out var overlayCount,
                passMilliseconds: overlayMilliseconds
            ) &&
                (overlayCount > 0)
            ) {
                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $" | overlay {overlayMilliseconds[0]:0.000}"
                );
            }
        }

        return builder.Append(value: ']').ToString();
    }
    // The world.quality echo: the current individual settings the preset (or a later override) left in place.
    private string DescribeQuality() {
        return $"[world.quality: shadows={ShadowTiers.Name(reach: settings.ShadowReach)} ao={(settings.AmbientOcclusion
            ? "on"
            : "off")} render-scale={RenderScaleName(scale: settings.RenderScale)} upscale={UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)}]";
    }
    private string DescribeShadowMarch() =>
        DescribeAdaptiveQuality(
            verb: "world.shadow-march",
            modes: settings.ShadowMarch switch {
                ShadowMarchMode.Exact => ("exact", false),
                ShadowMarchMode.Fast => ("fast", true),
                _ => ("auto", ((bool?)null)),
            },
            exact: "exact",
            fast: "fast"
        );
    private string DescribeShadowMask() =>
        DescribeAdaptiveQuality(
            verb: "world.shadow-mask",
            modes: settings.ShadowMask switch {
                ShadowMaskMode.ExactGather => ("exact", false),
                ShadowMaskMode.CameraTile => ("camera-tile", true),
                _ => ("auto", ((bool?)null)),
            },
            exact: "exact",
            fast: "camera-tile"
        );
    // The FPS-target readout: a set rate paces to that Hz; 0 is automatic display pacing.
    private static string DescribeTarget(double target) {
        return ((target > 0.0)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{target:0.###} Hz — the display-aware pacer targets this rate"
            )
            : "display (automatic — verified VRR capabilities or active signal timing)"
        );
    }
    // The world.far-field echo: both isolator lanes (F1 bound, F2 shadow exit) and their on/off state.
    private static string FarFieldEcho(WorldRenderSettings settings) {
        return $"[world.far-field: bound {(settings.FarBound
            ? "on"
            : "off")}, shadow {(settings.ShadowFarExit
            ? "on"
            : "off")}]";
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
    // The source-kind keyword for a screen's declared source — the stable token a piped proof asserts against.
    private static string ScreenSourceKind(WorldScreenSource source) {
        return source switch {
            WorldScreenSource.TestPattern => "test-pattern",
            WorldScreenSource.Machine machine => $"machine:{machine.Engine}",
            WorldScreenSource.Camera => "camera",
            WorldScreenSource.View => "view",
            WorldScreenSource.Capture => "capture",
            WorldScreenSource.Console => "console",
            WorldScreenSource.Qr => "qr",
            WorldScreenSource.Session session => $"session:{session.Destination}",
            WorldScreenSource.Text text => $"text:{text.Lines.Count}-line",
            _ => "none",
        };
    }
    // The world.screens listing: one segment per declared screen — index, source kind, live bound/unbound state (a
    // nonzero provider handle this frame), and engage policy. A query (not AcknowledgementOnly): its listing always surfaces, so a
    // piped proof can assert the test-pattern screen is bound and the None screen stays unbound (procedural fallback).
    private CommandResult ScreensHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return CommandResult.Error(output: "[world.screens: no arguments — lists every declared screen]");
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
            var engaged = server.Engagement.PlayersOn(screenIndex: screen.Index);
            var engagedText = ((engaged.Count > 0)
                ? $" engaged:{string.Join(
                    separator: "+",
                    values: engaged.Select(selector: static entry => (entry.Capture
                    ? $"p{entry.Display}"
                    : $"p{entry.Display}(mirror)"))
                )}"
                : ""
            );

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{((index == 0)
                ? " "
                : " | ")}{screen.Index} {ScreenSourceKind(source: screen.Source)} {(bound
                ? "bound"
                : "unbound")} {(screen.Route.Engageable
                ? "engageable"
                : "fixed")}{engagedText}"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    // The world.shadow.accumulate echo.
    private static string ShadowAccumulationEcho(WorldRenderSettings settings) {
        return $"[world.shadow.accumulate: {(settings.ShadowAccumulation
            ? "on"
            : "off")}]";
    }
    private static string ShadowEcho(WorldRenderSettings settings) {
        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.shadows: {ShadowTiers.Name(reach: settings.ShadowReach)} | crowd {settings.ShadowCrowdRadius:0.##}]"
        );
    }
    // The world.shadows echo: continuous reach plus crowd radius; named-notch values render through their facade.
    // Submits one live presentation-knob write through the server's grant check (WorldServer.ApplySessionLever) instead
    // of writing the injected service here. Defaults to the Render section because most of these knobs fold into it;
    // world.target passes Host explicitly.
    private void SubmitLever(WorldPrincipal principal, string name, double a, double b = 0.0, WorldSection section = WorldSection.Render) {
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
    private CommandResult SubmitLever(WorldPrincipal principal, string name, double a, Func<CommandResult> formatEcho, double b = 0.0, WorldSection section = WorldSection.Render) {
        SubmitLever(
            a: a,
            b: b,
            name: name,
            principal: principal,
            section: section
        );

        return formatEcho();
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
                    principal: context.ActingPrincipal(),
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
            description: "Toggles ambient occlusion engine-wide, live (no rebuild): world.ao [on|off] — no argument echoes the current state. AO darkens creases and contact seams; turning it off skips the per-lit-pixel occlusion march (a small GPU saving, see world.gpu).",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.ao: {(settings.AmbientOcclusion
                        ? "on"
                        : "off")}]");
                }

                var on = ParseOnOff(token: args[0]);

                if (on is not { } resolved) {
                    return CommandResult.Error(output: $"[world.ao: unknown state '{args[0]}' — on|off]");
                }

                return SubmitLever(
                    principal: context.ActingPrincipal(),
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
            description: "Toggles the far-field termination optimizations live (no rebuild) — the isolators for the owner's paired A/B: world.far-field [on|off|status] moves BOTH lanes together; world.far-field bound [on|off] is the F1 beam-published per-tile far bound (output-identical, skips empty-sky march steps); world.far-field shadow [on|off] is the F2 soft-shadow light-side early exit (a march-path change). No argument (or 'status') echoes both. Both ship ON; 'off' is the paired-run baseline.",
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

                // Lane-scoped form: world.far-field bound|shadow on|off.
                if (
                    args.Is(
                    index: 0,
                    value: "bound"
                ) ||
                    args.Is(
                    index: 0,
                    value: "shadow"
                )
                ) {
                    if (
                        (args.Count < 2) ||
                        (ParseOnOff(token: args[1]) is not { } laneState)
                    ) {
                        return CommandResult.Error(output: $"[world.far-field: expected '{args[0].ToString().ToLowerInvariant()} on|off']");
                    }

                    if (args.Is(
                        index: 0,
                        value: "bound"
                    )) {
                        SubmitLever(
                            principal: context.ActingPrincipal(),
                            name: WorldSessionLevers.FarBound,
                            a: (laneState
                            ? 1.0
                            : 0.0)
                        );
                    } else {
                        SubmitLever(
                            principal: context.ActingPrincipal(),
                            name: WorldSessionLevers.ShadowFarExit,
                            a: (laneState
                            ? 1.0
                            : 0.0)
                        );
                    }

                    // Read AFTER the lever above has applied (or been refused) — loopback drains it inline, so this
                    // is not a stale/racing read.
                    return new CommandResult(Output: FarFieldEcho(settings: settings));
                }

                // Bare form: world.far-field on|off drives BOTH lanes.
                if (ParseOnOff(token: args[0]) is not { } bothState) {
                    return CommandResult.Error(output: $"[world.far-field: unknown '{args.Tail(start: 0)}' — on|off|status, or bound|shadow on|off]");
                }

                SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.FarBound,
                    a: (bothState
                    ? 1.0
                    : 0.0)
                );

                return SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.ShadowFarExit,
                    a: (bothState
                    ? 1.0
                    : 0.0),
                    formatEcho: () => new CommandResult(Output: FarFieldEcho(settings: settings))
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shadow.accumulate",
            description: "Toggles the area-light shadow estimator's TEMPORAL ACCUMULATION live (no rebuild): world.shadow.accumulate [on|off|status]. On (the default) folds each frame's two sun-disc samples into the reprojected previous value, which is what makes the penumbra smooth; off shades the raw per-frame estimate and is deliberately stippled — an A/B isolator, not a quality tier.",
            handler: (context, args) => {
                if (
                    (args.Count == 0) ||
                    args.Is(
                    index: 0,
                    value: "status"
                )
                ) {
                    return new CommandResult(Output: ShadowAccumulationEcho(settings: settings));
                }

                if (ParseOnOff(token: args[0]) is not { } state) {
                    return CommandResult.Error(output: $"[world.shadow.accumulate: unknown '{args.Tail(start: 0)}' — on|off|status]");
                }

                return SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.ShadowAccumulation,
                    a: (state
                    ? 1.0
                    : 0.0),
                    formatEcho: () => new CommandResult(Output: ShadowAccumulationEcho(settings: settings))
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

                ShadowMaskMode? mode = null;

                if (args.Is(
                    index: 0,
                    value: "auto"
                )) {
                    mode = ShadowMaskMode.Auto;
                } else if (
                    args.Is(
                    index: 0,
                    value: "exact"
                ) ||
                    args.Is(
                    index: 0,
                    value: "gather"
                )
                ) {
                    mode = ShadowMaskMode.ExactGather;
                } else if (
                    args.Is(
                    index: 0,
                    value: "camera"
                ) ||
                    args.Is(
                    index: 0,
                    value: "camera-tile"
                ) ||
                    args.Is(
                    index: 0,
                    value: "tile"
                )
                ) {
                    mode = ShadowMaskMode.CameraTile;
                }

                if (mode is not { } resolved) {
                    return CommandResult.Error(output: $"[world.shadow-mask: unknown mode '{args[0]}' — auto|exact|camera-tile]");
                }

                return SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.ShadowMask,
                    a: ((double)resolved),
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

                AmbientOcclusionMode? mode = null;

                if (args.Is(
                    index: 0,
                    value: "auto"
                )) {
                    mode = AmbientOcclusionMode.Auto;
                } else if (
                    args.Is(
                    index: 0,
                    value: "exact"
                ) ||
                    args.Is(
                    index: 0,
                    value: "quality"
                )
                ) {
                    mode = AmbientOcclusionMode.Exact;
                } else if (
                    args.Is(
                    index: 0,
                    value: "fast"
                ) ||
                    args.Is(
                    index: 0,
                    value: "fleet"
                )
                ) {
                    mode = AmbientOcclusionMode.Fast;
                }

                if (mode is not { } resolved) {
                    return CommandResult.Error(output: $"[world.ao-quality: unknown mode '{args[0]}' — auto|exact|fast]");
                }

                return SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.AmbientOcclusionQuality,
                    a: ((double)resolved),
                    formatEcho: () => new CommandResult(Output: DescribeAmbientOcclusionQuality())
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shadow-march",
            description: "Selects the soft-shadow marcher live: world.shadow-march [auto|exact|fast]. auto keeps the exact 48-step, 12-unit path below 16 simulated stand-ins and uses the bounded-cost 16-step, 6-unit near-field path at the 16/64/128 fleet tiers; exact and fast force either side for visual/performance A/B.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribeShadowMarch());
                }

                ShadowMarchMode? mode = null;

                if (args.Is(
                    index: 0,
                    value: "auto"
                )) {
                    mode = ShadowMarchMode.Auto;
                } else if (
                    args.Is(
                    index: 0,
                    value: "exact"
                ) ||
                    args.Is(
                    index: 0,
                    value: "quality"
                )
                ) {
                    mode = ShadowMarchMode.Exact;
                } else if (
                    args.Is(
                    index: 0,
                    value: "fast"
                ) ||
                    args.Is(
                    index: 0,
                    value: "fleet"
                )
                ) {
                    mode = ShadowMarchMode.Fast;
                }

                if (mode is not { } resolved) {
                    return CommandResult.Error(output: $"[world.shadow-march: unknown mode '{args[0]}' — auto|exact|fast]");
                }

                return SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.ShadowMarch,
                    a: ((double)resolved),
                    formatEcho: () => new CommandResult(Output: DescribeShadowMarch())
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view-refresh",
            description: "Sets the diegetic views' deterministic offscreen refresh cadence: world.view-refresh [1..8]. 1 renders every produced frame; 4 (the default) renders every fourth frame and preserves the previous images between refreshes. No argument echoes the current divisor and how many camera views are registered in the offscreen pool (a removed View screen releases its camera's render, dropping that count).",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.view-refresh: every {screens.ViewRefreshDivisor} produced frame(s); {screens.ActiveCameraViewCount} camera view(s) registered]");
                }

                if (
                    !args.TryInt(
                    index: 0,
                    value: out var divisor
                ) ||
                    (divisor < 1) ||
                    (divisor > 8)
                ) {
                    return CommandResult.Error(output: $"[world.view-refresh: expected an integer divisor from 1 through 8, got '{args[0]}']");
                }

                screens.SetViewRefreshDivisor(divisor: divisor);

                return new CommandResult(Output: $"[world.view-refresh: every {divisor} produced frame(s)]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.debug-view",
            description: "Selects the live SDF diagnostic output for every World camera: world.debug-view [off|depth|normals|raydir|material-id|iteration-count|termination|slice|mask|overshoot]. Depth is the primary-march-only performance probe; off restores final shading.",
            handler: (_, args) => {
                if (renderProbe.Node is not { } node) {
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
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.RenderScale,
                    a: scale,
                    formatEcho: () => {
                        var liveScale = settings.RenderScale;
                        var pixelPercent = ((int)Math.Round(a: ((liveScale * liveScale) * 100f)));

                        return new CommandResult(Output: $"[world.render-scale: {RenderScaleName(scale: liveScale)} — ~{pixelPercent}% of native internal pixels; measure GPU cost with world.gpu]");
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
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.UpscaleSharpness,
                    a: sharpness,
                    formatEcho: () => new CommandResult(Output: $"[world.upscale-sharpness: {UpscaleSharpnessName(sharpness: settings.UpscaleSharpness)}]")
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.target",
            description: "Sets the continuous presentation target live: world.target [<hz>|display]. <hz> is any positive finite number, capped by the effective display ceiling. 'display' (or 'vrr') uses verified VRR bounds when advertised and otherwise the active signal timing. Presentation only; present-mode switching remains a boot option.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.target: {DescribeTarget(target: pacing.TargetHertz)}]");
                }

                if (
                    args.Is(
                    index: 0,
                    value: "display"
                ) ||
                    args.Is(
                    index: 0,
                    value: "vrr"
                )
                ) {
                    return SubmitLever(
                        principal: context.ActingPrincipal(),
                        name: WorldSessionLevers.TargetHertz,
                        a: 0.0,
                        section: WorldSection.Host,
                        formatEcho: () => new CommandResult(Output: $"[world.target: {DescribeTarget(target: pacing.TargetHertz)}]")
                    );
                }

                if (
                    !double.TryParse(
                    args[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var hz
                ) ||
                    !double.IsFinite(d: hz) ||
                    (hz <= 0.0)
                ) {
                    return CommandResult.Error(output: "[world.target: expected a positive finite Hz value, or 'display'/'vrr' for automatic display pacing]");
                }

                // The echo formats INSIDE the completion — after the lever has applied (or been refused).
                return SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.TargetHertz,
                    a: hz,
                    section: WorldSection.Host,
                    formatEcho: () => new CommandResult(Output: $"[world.target: {DescribeTarget(target: pacing.TargetHertz)}]")
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
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.Shadows,
                    a: ShadowTiers.Scale(tier: preset.Shadows),
                    b: settings.ShadowCrowdRadius
                );
                SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.AmbientOcclusion,
                    a: (preset.AmbientOcclusion
                    ? 1.0
                    : 0.0)
                );

                // The echo formats INSIDE the LAST lever's completion — all three have applied (or the last was
                // refused) by the time formatEcho runs, since loopback drains each inline before its Submit* returns.
                return SubmitLever(
                    principal: context.ActingPrincipal(),
                    name: WorldSessionLevers.RenderScale,
                    a: WorldRenderScaleTiers.Scale(tier: preset.RenderScale),
                    formatEcho: () => new CommandResult(Output: DescribeQuality())
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.timing",
            description: "Arms per-pass GPU timing engine-wide, live (no restart, no magic env var): world.timing [on|off] — no argument echoes the armed state. On lights BOTH the GPU per-pass digest (readable with world.gpu) and the launcher's CPU frame-timing hub; performance metrics are a first-class citizen here.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.timing: {(GpuTimingControl.Shared.Armed
                        ? "on"
                        : "off")}]");
                }

                var on = ParseOnOff(token: args[0]);

                if (on is not { } resolved) {
                    return CommandResult.Error(output: $"[world.timing: unknown state '{args[0]}' — on|off]");
                }

                GpuTimingControl.Shared.SetArmed(armed: resolved);

                return new CommandResult(Output: $"[world.timing: {(resolved
                    ? "on"
                    : "off")}]");
            }
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.gpu",
            description: "Echoes the previous frame's per-pass GPU milliseconds — the whole-frame total plus each render pass (upload/sky/mask/beam/cull-args/views/composite) — read live off the renderer. Arm it first with world.timing on; the metrics are first-class, no env var needed.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: DescribeGpu())
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.screens",
            description: "Lists every declared diegetic screen, one segment each — index, source kind (test-pattern|none|machine|camera|view|capture; a machine reads machine:<engine>), bound/unbound (a nonzero live provider handle this frame), and its engage policy (engageable|fixed). No argument; the pipe-assertable state proving the test-pattern screen is bound and the unbound screen falls back to the engine's procedural no-signal card (never black). A query — its listing always echoes, even under wire.ack quiet.",
            handler: ScreensHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.cameras",
            description: "Lists every declared placeable camera with its anchor, independent motion and aim policies, and render dimensions. No argument; the camera-table twin of world.screens.",
            handler: CamerasHandler
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.fps",
            description: "Echoes the measured frame rate over the recent window — avg, the slowest single frame (the floor check), the sample count — and the pacer's current target. The world's reference desktop contract is 120 FPS under VRR.",
            valueKind: CommandValueKind.Digital,
            handler: _ => {
                var (averageFps, worstFps, frameCount) = frameRate.Summarize();

                if (frameCount == 0) {
                    return CommandResult.Error(output: "[world.fps: no frames sampled yet]");
                }

                var target = pacing.TargetHertz;
                var pacer = ((target > 0.0)
                    ? string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"{target:0.###} Hz"
                    )
                    : "automatic (verified VRR range or active signal timing)"
                );

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.fps: avg={averageFps:0.0} worst={worstFps:0.0} over {frameCount} frames | pacer: {pacer}]"
                ));
            }
        );
    }
}
