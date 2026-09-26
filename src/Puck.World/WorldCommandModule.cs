using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Launcher;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The world's own PRESENTATION console surface — its frame-rate readout (<c>world.fps</c>), the
/// declared-row tables (<c>world.screens</c>, <c>world.cameras</c>), the FPS target, and the renderer's own
/// diagnostics (shader reload, debug view, view refresh) — all live console verbs, each echoing the current value when
/// called with no argument. The render levers every presentation shape honors live in
/// <see cref="WorldRenderLeverCommandModule"/>. Registered ONLY when presentation is composed (<c>AddWorldPresentation</c>); over
/// headless stdin every one of these refuses as unknown. The participant/census verbs (<c>world.players</c>,
/// <c>world.devices</c>, <c>world.population</c>) and authoritative diagnostics (<c>world.navigation</c>,
/// <c>world.budget</c>) moved to <see cref="WorldPopulationCommandModule"/> — server-safe, registered in core either
/// way. The render nodes' GPU work is the <c>gpu</c> section of <c>world.counters</c>
/// (<see cref="WorldCountersCommandModule"/>), registered in core. The FPS target rides the live
/// <see cref="PresentPacingControl"/>, read by the frame source each captured frame.
/// </summary>
internal sealed class WorldCommandModule(FrameRateMonitor frameRate, PresentPacingControl pacing, WorldRenderProbe renderProbe, WorldServer server, WorldScreenBinder screens, IServerLink link, WorldOverlayFacts facts, PlayerRoster roster) : ICommandModule {
    // A ranked camera's listing: every candidate's anchor kind in rank order, then the candidate currently winning
    // for each joined seat (a seat-relative list can win differently per seat).
    private string CameraAnchorCandidates(WorldCamera camera) {
        var candidates = camera.Anchors!;
        var builder = new StringBuilder(value: "anchors=[");

        for (var index = 0; (index < candidates.Count); index++) {
            _ = builder.Append(value: ((index == 0)
                ? ""
                : ",")).Append(value: CameraAnchorKind(anchor: candidates[index].Anchor));
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
                handler: $"{(any
                ? ","
                : "")}seat{PlayerRoster.DisplayNumber(slot: slot)}:{((winner >= 0)
                ? winner.ToString(provider: CultureInfo.InvariantCulture)
                : "none")}"
            );
            any = true;
        }

        return (any
            ? builder.ToString()
            : builder.Append(value: "none").ToString()
        );
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
    // The source-kind keyword for a screen's declared source — the stable token a piped proof asserts against. A
    // producer source reads as its producer id.
    private static string ScreenSourceKind(WorldScreenSource source) {
        return source switch {
            WorldScreenSource.Machine machine => $"machine:{machine.Instance}:{machine.Output}",
            WorldScreenSource.Producer producer => producer.Id,
            WorldScreenSource.View => "view",
            WorldScreenSource.Session session => $"session:{session.Destination}",
            WorldScreenSource.Text text => $"text:{text.Lines.Count}-line",
            _ => "none",
        };
    }
    // The world.screens listing: one segment per declared screen — index, source kind, live bound/unbound state (a
    // nonzero provider handle this frame), engage policy, and the destination a pointer hit on it goes to. A query (not AcknowledgementOnly): its listing always surfaces, so a
    // piped proof can assert the test-pattern screen is bound and the None screen stays unbound (procedural fallback).
    private CommandResult ScreensHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return CommandResult.Error(output: "[world.screens: no arguments — lists every declared screen]");
        }

        // The LIVE definition's rows (never the boot snapshot), so a screen mutation's new source narrates honestly, then
        // each creation face showing a source, as the presentation last derived it.
        var declaredScreens = new List<WorldScreen>(collection: server.Definition.Screens);

        foreach (var face in screens.Mappings.Screens) {
            if (
                (face.Index >= WorldPrototypeFacets.DerivedFaceBase) &&
                (face.Source is not WorldScreenSource.None)
            ) {
                declaredScreens.Add(item: face);
            }
        }

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
            // How a camera or capture image crosses devices: the shared fence, or the producer's CPU wait and why.
            var orderText = ((screens.FenceOrderAt(index: screen.Index) is { } order)
                ? $" order:{order}"
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
                : "fixed")} input:{(screen.Route.Input ?? SourceDestination.Presentation)}{engagedText}{orderText} mapping {(screens.Mappings.Describe(screen: screen.Index) ?? "none (no slot)")}"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shaders.reload",
            description: "Reloads compiled SDF kernels on the next produced frame: world.shaders.reload [directory]. Defaults to deployed Assets/Shaders/Sdf; a source checkout can name src/Puck.SdfVm/Assets/Shaders/Sdf after CompileShaders completes. Uses the current backend. Replaces changed pipelines while retaining world state, GPU buffers and textures; failed loads or ISA validation keep the previous set. This queues work: world.shaders.status reports completion. Binding/ABI changes require a host rebuild; child engines and overlay/postprocess shaders are outside this command.",
            handler: (_, args) => {
                if (renderProbe.Node is not { } node) {
                    return CommandResult.Error(output: "[world.shaders.reload: renderer not ready]");
                }
                try {
                    if (!node.RequestShaderReload(directory: ((args.Count == 0)
                        ? null
                        : args.Tail(start: 0)))) {
                        return CommandResult.Error(output: "[world.shaders.reload: another request is pending — world.shaders.status]");
                    }
                    var status = node.ShaderReloadStatus;

                    return new CommandResult(Output: $"[world.shaders.reload: request={status.RequestId} pending directory={status.Directory}]");
                } catch (Exception exception) when ((exception is ArgumentException or IOException or UnauthorizedAccessException)) {
                    return CommandResult.Error(output: $"[world.shaders.reload: {exception.Message}]");
                }
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.shaders.status",
            description: "Reports the most recent compiled SDF shader reload: request number, state (idle/pending/applied/unchanged/failed), generation, changed pipeline count, directory and failure reason. A request is complete only after pending changes to an outcome.",
            handler: (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: "[world.shaders.status: no arguments]");
                }
                if (renderProbe.Node is not { } node) {
                    return new CommandResult(Output: "[world.shaders.status: renderer not ready]");
                }
                var status = node.ShaderReloadStatus;

                return new CommandResult(Output: $"[world.shaders.status: request={status.RequestId} state={status.State} generation={status.Generation} pipelines={status.ChangedPipelines} directory={(status.Directory ?? "default")}{((status.Error is { } error)
                    ? $" error={error}"
                    : "")}]");
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
                    return WorldRenderLeverCommandModule.SubmitLever(
                        link: link,
                        principal: context.Principal,
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
                return WorldRenderLeverCommandModule.SubmitLever(
                    link: link,
                    principal: context.Principal,
                    name: WorldSessionLevers.TargetHertz,
                    a: hz,
                    section: WorldSection.Host,
                    formatEcho: () => new CommandResult(Output: $"[world.target: {DescribeTarget(target: pacing.TargetHertz)}]")
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.screens",
            description: "Lists every declared diegetic screen, then every creation face showing a source, one segment each — index, source kind (test-pattern|none|machine|camera|view|capture; a machine reads machine:<engine>), bound/unbound (a nonzero live provider handle this frame), its engage policy (engageable|fixed), for a camera on its GPU tier or a capture on its GPU route, order:fence (the render device waits on the producer's shared fence) or order:cpu-wait (reason) (a device that cannot share the fence waits on the CPU), and last the mapping the screen publishes, in the line world.view.panes prints for a pane (mapping producer:source$<producer>$<digest> surface … or instance:<view> surface …, the source extent, crop, layout, fit, the glass warp and the destination), or mapping none (reason): no image source, a live presentation source no row names, an extent not known yet, or not published by a boot that presents nothing. No argument; the pipe-assertable state proving the test-pattern screen is bound and the unbound screen falls back to the engine's procedural no-signal card (never black). A query — its listing always echoes, even under wire.ack quiet.",
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
                    return CommandResult.Error(output: ((worstFps > 0f)
                        ? string.Create(
                            provider: CultureInfo.InvariantCulture,
                            handler: $"[world.fps: no frame composed for {(1f / worstFps):0.0} s]"
                        )
                        : "[world.fps: no frames sampled yet]"
                    ));
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
