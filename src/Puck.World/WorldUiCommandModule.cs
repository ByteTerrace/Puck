using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The overlay-UI verb surface: <c>world.screenshot</c> (arm a one-shot PNG capture of the next composed frame,
/// through the outermost render decorator, so the readback shows exactly what the player sees — overlay included)
/// and <c>world.binding-bar</c> (read or override a seat's authored binding-bar policy). A separate module from
/// <see cref="WorldCommandModule"/> to keep every class under its analyzer ceilings. The drawn cursor's live
/// read-back is <c>world.view.pointer</c> (<see cref="WorldViewCommandModule"/> — the <c>world.view.camera</c>
/// family, per-seat live presentation state).
/// </summary>
/// <remarks><c>world.screenshot</c> arms work; it does not do it. The file appears when a frame composes, which is
/// after this handler has returned and may be never — the run can end first. A second request while one remains
/// pending is refused. The echo therefore says pending and names the path as a request, never as a file
/// that exists; the render graph prints the resolved path when the frame lands, named by the root that served it
/// (<c>[capture] main$overlay -&gt; …</c> from a windowed World's root, which draws the overlay over the scene,
/// <c>[capture] main -&gt; …</c> from the offscreen default graph's root instance, which runs the <c>views.post</c>
/// passes, or <c>[capture] world -&gt; …</c> when the world's view itself is the root); a
/// request refused while another is pending is reported out loud; and an ordinary capture still outstanding when the
/// run ends is refused by name (<see cref="WorldCaptureScheduler.Drain"/>) before the render root is disposed.
/// A rendered host holds its clock at the tick an ordinary capture was armed after until a frame serves it or its
/// hold budget refuses it (<see cref="WorldCaptureScheduler.ArmUnscheduled"/>), so a tick-counted wait after it has the
/// capture behind it. A caller reading either
/// stream can always tell "written" from "never happened" — which is the whole point, since a scripted caller that
/// cannot is a caller being lied to.
/// <para>Registered in every boot shape so the command vocabulary stays stable; its presentation dependencies are
/// optional and handlers refuse by name when unavailable.</para></remarks>
internal sealed class WorldUiCommandModule(IServerLink link, WorldCaptureScheduler captureScheduler, WorldRenderProbe? renderProbe = null, WorldBindingBarControl? bindingBarControl = null, WorldCrossingCapture? crossingCapture = null, WorldStateRoot? stateRoot = null, WorldViewComposer? composer = null) : ICommandModule {
    /// <summary>The binding-bar visibility and read-back verb.</summary>
    public const string BindingBarCommand = "world.binding-bar";

    // The file a capture with no path writes: screenshots/puck-<local time>.png under the run's state root, numbered past
    // one that already exists so two captures in a second keep both. The name is host output, never simulation state.
    private string DefaultPath() {
        var directory = (stateRoot?.PathOf(name: "screenshots") ?? Path.Combine(path1: Path.GetTempPath(), path2: "puck-screenshots"));
        var stem = $"puck-{TimeProvider.System.GetLocalNow():yyyyMMdd-HHmmss}";
        var path = Path.Combine(path1: directory, path2: $"{stem}.png");

        for (var number = 2; File.Exists(path: path); number++) {
            path = Path.Combine(path1: directory, path2: $"{stem}-{number}.png");
        }

        return path;
    }

    private static CommandResult BindingBarHandler(CommandContext context, in WireArgs args, IServerLink link, WorldBindingBarControl? bindingBarControl) {
        if (bindingBarControl is null) {
            return CommandResult.Error(output: "[world.binding-bar: policy resolver is unavailable]");
        }
        if (args.Count > 2) {
            return CommandResult.Error(output: "[world.binding-bar: expected [on|off|auto] [player], or [player]]");
        }

        var player = 1;
        var writesOverride = false;
        bool? visibilityOverride = null;

        if (args.Count > 0) {
            if (args.Is(
                index: 0,
                value: "on"
            )) {
                writesOverride = true;
                visibilityOverride = true;
            } else if (args.Is(
                index: 0,
                value: "off"
            )) {
                writesOverride = true;
                visibilityOverride = false;
            } else if (args.Is(
                index: 0,
                value: "auto"
            )) {
                writesOverride = true;
            } else if (
                (args.Count == 1) &&
                args.TryInt(
                index: 0,
                value: out var parsedPlayer
            )
            ) {
                player = parsedPlayer;
            } else {
                return CommandResult.Error(output: $"[world.binding-bar: unknown state '{args[0].ToString()}' — on|off|auto, or a player index]");
            }
        }

        if (
            writesOverride &&
            (args.Count == 2)
        ) {
            if (!args.TryInt(
                index: 1,
                value: out player
            )) {
                return CommandResult.Error(output: "[world.binding-bar: player must be an integer in 1..4]");
            }
        }
        if (
            (player < 1) ||
            (player > WorldBodiesLimits.LocalSeatCount)
        ) {
            return CommandResult.Error(output: $"[world.binding-bar: player {player} is outside 1..{WorldBodiesLimits.LocalSeatCount}]");
        }

        var slot = PlayerRoster.SlotFromDisplay(number: player);

        if (writesOverride) {
            // Routed, not written: the server checks Mutate over section:bindings — the section the bar's authoring
            // lives in — and the client applies it on accept. Over loopback DeliverSessionLever runs synchronously
            // inside SubmitSessionLever, so the read-back below honestly reports the unchanged override when the
            // lever was denied.
            link.SubmitSessionLever(
                lever: new WorldSessionLever(
                    A: ((visibilityOverride is not { } forced)
                ? WorldSessionLevers.BindingBarAuto
                : (forced
                    ? 1.0
                    : 0.0)),
                    Name: WorldSessionLevers.BindingBar,
                    Seat: slot,
                    Section: WorldSection.Bindings
                ),
                principal: context.Principal
            );
        }

        var status = bindingBarControl.Status(slot: slot);
        var authoring = status.Authoring;
        var layout = status.Layout;
        var overrideWord = ((status.Override is null)
            ? "auto"
            : (status.Override.Value
                ? "on"
                : "off"
        ));

        return new CommandResult(Output: FormattableString.Invariant(formattable: $"[world.binding-bar p{player}: source {status.Source} authored {(authoring.Enabled
            ? "on"
            : "off")} text {(authoring.Text
            ? "on"
            : "off")} modifiers {(authoring.Modifiers
            ? "on"
            : "off")} declaredModifiersOnly {(authoring.DeclaredModifiersOnly
            ? "on"
            : "off")} pageLabel {(authoring.PageLabel
            ? "on"
            : "off")} visible {((authoring.Visible is null)
            ? "always"
            : "predicate")} override {overrideWord} hidden {status.Hidden.ToString().ToLowerInvariant()} reason {status.Reason} presence {status.Presence:0.##} slots {authoring.SlotSet.Count} banks {authoring.Banks.Count} hideUnbound {status.EffectiveHideUnbound.ToString().ToLowerInvariant()} stacked {status.Stacked.ToString().ToLowerInvariant()} layout buttonSize {layout.ResolvedButtonSize:0.###} banks {(layout.Banks?.Count ?? 0)} anchor {layout.ResolvedAnchor.Edge.ToString().ToLowerInvariant()} {layout.ResolvedAnchor.Inset:0.###} glyphOffsetRatio {layout.ResolvedGlyphOffsetRatio:0.###} glyphSizeRatio {layout.ResolvedGlyphSizeRatio:0.###} scale {status.EffectiveScale:0.###}]"));
    }

    // world.screenshot <path.png> presented [<frames>] [region <x> <y> <width> <height>]: arms a presented capture of the
    // frames the display shows next, holding nothing, and reports every frame and its difference from the one before it
    // on stderr once the last PNG is written.
    private static CommandResult ArmPresented(in WireArgs args, WorldCaptureScheduler captureScheduler, ICaptureRequestTarget render) {
        const string Usage = "world.screenshot <path.png> presented [<frames>] [region <x> <y> <width> <height>]";

        var frames = 1;
        var next = 2;

        if ((args.Count > next) && !args.Is(index: next, value: "region")) {
            if (!args.TryInt(index: next, value: out frames) || (frames < 1) || (frames > FrameCaptureRequest.MaxPresentedFrames)) {
                return CommandResult.Error(output: $"[world.screenshot: presented takes 1..{FrameCaptureRequest.MaxPresentedFrames} frames — {Usage}]");
            }

            next++;
        }

        FrameCaptureRegion? region = null;

        if (args.Count > next) {
            if (
                !args.Is(index: next, value: "region") ||
                (args.Count != (next + 5)) ||
                !args.TryFloats(count: 4, start: (next + 1), values: out var edges)
            ) {
                return CommandResult.Error(output: $"[world.screenshot: expected {Usage}]");
            }
            if (FrameCaptureRegion.RefusalOf(height: edges[3], width: edges[2], x: edges[0], y: edges[1]) is { } refusal) {
                return CommandResult.Error(output: $"[world.screenshot: {refusal}]");
            }

            region = new FrameCaptureRegion(Height: edges[3], Width: edges[2], X: edges[0], Y: edges[1]);
        }

        if (render.PendingCapturePath is { } outstanding) {
            return CommandResult.Error(output: $"[world.screenshot: a capture of {outstanding} is still pending — arming over it would write no file for it; fence it (world.wait captures <seconds>) and retry]");
        }

        var path = Path.GetFullPath(path: args[0].ToString());

        try {
            if (Path.GetDirectoryName(path: path) is { Length: > 0 } directory) {
                _ = Directory.CreateDirectory(path: directory);
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
            return CommandResult.Error(output: $"[world.screenshot: could not create the target directory ({exception.Message})]");
        }

        var request = FrameCaptureRequest.Presented(
            frames: frames,
            path: path,
            region: region
        );

        _ = request.Completion.ContinueWith(
            continuationAction: static (completed, state) => Console.Error.WriteLine(value: ((completed.Result.Error is { } error)
                ? $"[capture] refused {state}: {error.Message}"
                : PresentedFrameReport.Format(frames: completed.Result.Presented, region: completed.Result.Region))),
            scheduler: TaskScheduler.Default,
            state: path
        );

        try {
            captureScheduler.ArmUnscheduled(
                request: request,
                target: render
            );
        } catch (InvalidOperationException exception) {
            return CommandResult.Error(output: $"[world.screenshot: {exception.Message}]");
        }

        return new CommandResult(Output: ((frames == 1)
            ? $"[world.screenshot: pending {path} — presented, the next frame the display shows, read as presented; the report lands on stderr]"
            : $"[world.screenshot: pending {request.PathOf(index: 0)} .. {request.PathOf(index: (frames - 1))} — presented, the next {frames} frames the display shows, read as presented; world.wait captures <seconds> fences them, and the report lands on stderr]"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.screenshot",
            description: "Arms a one-shot PNG capture of the next composed frame (the render graph's root: the world, its views.post passes and the overlay): world.screenshot with no argument writes it to screenshots/puck-<local date and time>.png under the run's state root (--state-dir), the only form a binding or any seat may run, since a document never chooses where a file lands; world.screenshot <path.png>, the operator's, names the file, or of the first frame a seat presents after its route moves, the frame a crossing shows first: world.screenshot <path.png> crossing [player] (player defaults to 1; the capture waits for the crossing apart from an ordinary one, so another may be armed and land meanwhile). The ordinary form is a settled still: it holds presentation from the frame it is armed in and lets temporal views converge over the held frame, which hides what changes from frame to frame. world.screenshot <path.png> presented [<frames>] [region <x> <y> <width> <height>] instead reads what the display presented: it holds and settles nothing, and reads back each of the next frames the display shows (1..120, default 1) as composed for it, jitter, temporal history and motion included, to <path.png> for one frame or <path>-000.png onward for a burst; once the last PNG is written it prints on stderr a line per frame (its presentation index, the presentation its image was rendered on, marked standing when an earlier one, its tick, and the pixels that changed from the frame before with the mean and largest absolute Rec. 709 luma change, over the region given in fractions of the frame, origin top-left, or the whole frame) and a summary line. Use it for flicker (a still scene whose pixels change between frames), ghosting and trails, and the ordinary form for clean stills. Like every capture it never holds external desktop or camera pixels: while one is armed those screens show their capture fill on the display too. This REQUESTS a capture, it does not take one — the echo reads 'pending <path>' because no file exists yet, and the renderer prints the resolved path on stderr the moment the frame lands, named by the render-graph instance that served it ('[capture] main$overlay -> <path>' from a windowed World's overlay instance, '[capture] main -> <path>' from the offscreen root graph's instance, or '[capture] world -> <path>' when the world's view is the root because nothing is drawn over it). Let rendering progress (world.wait), then confirm the capture completion before reading the file. A rendered host steps no tick past the one the capture was armed after until a frame serves it or its hold budget refuses it, so world.wait <ticks> after it has the capture behind it. Arming a second capture while one is still pending REFUSES rather than silently replacing it — the earlier path would never be written. A capture the render chain refuses (one armed when the graphics device is lost, among others) writes no file and prints [capture] refused <path>: <reason> on stderr, and a request still outstanding when the run ends is refused the same way, naming the last completed tick, instead of leaving the caller believing a file exists. The parent directory is created here.",
            handler: (context, args) => {
                if ((args.Count > 0) && (context.Principal.Kind != PrincipalKind.Console)) {
                    return CommandResult.Error(output: $"[world.screenshot: a path is an operator form; {context.Principal.Describe()} captures with no argument, to the state root's screenshots folder]");
                }

                if (renderProbe is null) {
                    return CommandResult.Error(output: "[world.screenshot: requires offscreen or windowed presentation — headless has no renderer to capture]");
                }

                if (renderProbe.Root is not { } render) {
                    return CommandResult.Error(output: "[world.screenshot: the renderer is not built yet — retry after the first frame]");
                }

                // A pending request is UNFINISHED WORK, not a stale value to overwrite: arming over it would drop a
                // file the caller was already promised, with nothing on either stream to say so. Refusing names both
                // paths and leaves the first request intact, so the caller fences a frame and asks again.
                var crossing = args.Is(
                    index: 1,
                    value: "crossing"
                );

                if (args.Is(
                    index: 1,
                    value: "presented"
                )) {
                    return ArmPresented(
                        args: in args,
                        captureScheduler: captureScheduler,
                        render: render
                    );
                }
                var slots = args.Is(
                    index: 1,
                    value: "slots"
                );

                if (((args.Count > 1) && !crossing && !slots) || (args.Count > 3) || (slots && (args.Count != 2))) {
                    return CommandResult.Error(output: "[world.screenshot: expected world.screenshot <path.png> [slots | crossing [player]] or world.screenshot <path.png> presented [<frames>] [region <x> <y> <width> <height>]]");
                }

                if (
                    !crossing &&
                    (render.PendingCapturePath is { } outstanding)
                ) {
                    return CommandResult.Error(output: $"[world.screenshot: a capture of {outstanding} is still pending — arming over it would write no file for it; fence a frame (world.wait) and retry]");
                }

                var path = ((args.Count == 0)
                    ? DefaultPath()
                    : Path.GetFullPath(path: args[0].ToString()));

                try {
                    if (Path.GetDirectoryName(path: path) is { Length: > 0 } directory) {
                        _ = Directory.CreateDirectory(path: directory);
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
                    return CommandResult.Error(output: $"[world.screenshot: could not create the target directory ({exception.Message})]");
                }

                var request = new FrameCaptureRequest(path: path);

                _ = request.Completion.ContinueWith(
                    continuationAction: static (completed, state) => {
                        if (completed.Result.Error is { } error) {
                            Console.Error.WriteLine(value: $"[capture] refused {state}: {error.Message}");
                        }
                    },
                    scheduler: TaskScheduler.Default,
                    state: path
                );

                if (crossing) {
                    var player = 1;

                    if (
                        (crossingCapture is null) ||
                        (
                            (args.Count > 2) &&
                            (!int.TryParse(
                                provider: System.Globalization.CultureInfo.InvariantCulture,
                                result: out player,
                                s: args[2].ToString()
                            ) || (player < 1) || (player > PlayerRoster.MaxSlots))
                        )
                    ) {
                        return CommandResult.Error(output: $"[world.screenshot: crossing takes a player 1..{PlayerRoster.MaxSlots} — world.screenshot <path.png> crossing [player]]");
                    }
                    if (!crossingCapture.TryArm(
                        reason: out var armReason,
                        request: request,
                        slot: (player - 1)
                    )) {
                        return CommandResult.Error(output: $"[world.screenshot: {armReason}]");
                    }

                    return new CommandResult(Output: $"[world.screenshot: pending {path} — lands on the first frame player {player} presents after crossing]");
                }

                captureScheduler.ArmUnscheduled(
                    request: request,
                    target: render
                );

                if (slots) {
                    var views = WorldSlotStills.Of(slots: (composer?.Slots ?? []));

                    _ = request.Completion.ContinueWith(
                        continuationAction: static (completed, state) => WorldSlotStills.Write(result: completed.Result, slots: ((IReadOnlyList<WorldSlotStill>)state!)),
                        scheduler: TaskScheduler.Default,
                        state: views
                    );

                    return new CommandResult(Output: $"[world.screenshot: pending {path} and {views.Count} slot still(s) ({string.Join(separator: ", ", values: views.Select(selector: static view => view.Name))}) — lands on the next composed frame]");
                }

                // "pending", not the bare path: the words are true at the instant they are printed. The capture line
                // on stderr is what says the file exists.
                return new CommandResult(Output: $"[world.screenshot: pending {path} — lands on the next composed frame]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: BindingBarCommand,
            description: "Reads or overrides one local seat's resolved on-screen binding bar: world.binding-bar [on|off|auto] [player], or world.binding-bar [player]. on/off force visibility; auto returns to authored enabled/rest behavior. The override is a session lever — checked against Mutate over section:bindings before it applies, and player 1's forced state folds into bindingOverlays[0].bindingBar.enabled at world.save. The read-back reports the resolved world-or-identity policy, its text policy (off = a purely pictographic bar: no letter badges, no page name, no chord hint that needs either), its authored slot/bank counts, the resolved (world-or-player) hideUnbound and stacked preferences, current hidden state and reason, and every layout value (scale reflects a player's own override when set). Player defaults to 1 (1..4).",
            handler: (context, args) => BindingBarHandler(
                args: in args,
                bindingBarControl: bindingBarControl,
                context: context,
                link: link
            )
        );
    }
}
