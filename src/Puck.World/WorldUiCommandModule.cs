using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

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
/// after this handler has returned and may be never — the run can end first, and a second request armed inside the
/// same frame replaces the first. The echo therefore says pending and names the path as a request, never as a file
/// that exists; the render chain prints the resolved path when the frame lands, named by whichever node served it
/// (<c>[capture] unified overlay -&gt; …</c> from the overlay decorator, <c>[capture] &lt;shader-set id&gt; -&gt;
/// …</c> from a composed <c>render.extensions</c> pass, <c>[debug] captured frame N -&gt; …</c> from the engine node
/// at the bottom); a
/// request replaced before it was served is refused out loud, naming the path that will never be written; and
/// <see cref="WorldPostBuildWiring"/> reports anything still outstanding when the run ends. A caller reading either
/// stream can always tell "written" from "never happened" — which is the whole point, since a scripted caller that
/// cannot is a caller being lied to.
/// <para>Registered in every boot shape so the command vocabulary stays stable; its presentation dependencies are
/// optional and handlers refuse by name when unavailable.</para></remarks>
internal sealed class WorldUiCommandModule(IServerLink link, WorldRenderProbe? renderProbe = null, WorldBindingBarControl? bindingBarControl = null) : ICommandModule {
    /// <summary>The binding-bar visibility and read-back verb.</summary>
    public const string BindingBarCommand = "world.binding-bar";

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
            (player > WorldPopulationLimits.LocalSeatCount)
        ) {
            return CommandResult.Error(output: $"[world.binding-bar: player {player} is outside 1..{WorldPopulationLimits.LocalSeatCount}]");
        }

        var slot = (player - 1);

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
                        : 0.0
                    )),
                    Name: WorldSessionLevers.BindingBar,
                    Seat: slot,
                    Section: WorldSection.Bindings
                ),
                principal: context.ActingPrincipal()
            );
        }

        var status = bindingBarControl.Status(slot: slot);
        var authoring = status.Authoring;
        var layout = authoring.ResolvedLayout;
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
            : "off")} visible {((authoring.Visible is null)
            ? "always"
            : "predicate")} override {overrideWord} hidden {status.Hidden.ToString().ToLowerInvariant()} reason {status.Reason} slots {authoring.SlotSet.Count} banks {authoring.Banks.Count} hideUnbound {status.EffectiveHideUnbound.ToString().ToLowerInvariant()} stacked {status.Stacked.ToString().ToLowerInvariant()} layout buttonSize {layout.ResolvedButtonSize:0.###} centerGap {layout.ResolvedCenterGap:0.###} anchorOffsetY {layout.ResolvedAnchorOffsetY:0.###} glyphOffsetRatio {layout.ResolvedGlyphOffsetRatio:0.###} glyphSizeRatio {layout.ResolvedGlyphSizeRatio:0.###} scale {status.EffectiveScale:0.###}]"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.screenshot",
            description: "Arms a one-shot PNG capture of the next composed frame (world + overlay, via the outermost render decorator): world.screenshot <path.png>. This REQUESTS a capture, it does not take one — the echo reads 'pending <path>' because no file exists yet, and the render chain prints the resolved path on stderr the moment the frame lands, named by whichever node served it ('[capture] unified overlay -> <path>', '[capture] <shader-set id> -> <path>' from a composed render.extensions pass, or '[debug] captured frame N -> <path>' from the engine node when the nodes above drew nothing and forwarded the request down). Fence a frame (world.wait) before reading the file. Arming a second capture while one is still pending REFUSES rather than silently replacing it — the earlier path would never be written — and a request still outstanding when the run ends is reported on stderr instead of leaving the caller believing a file exists. The parent directory is created here.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return CommandResult.Error(output: "[world.screenshot: a target path is required — world.screenshot <path.png>]");
                }

                if (renderProbe is null) {
                    return CommandResult.Error(output: "[world.screenshot: requires a windowed boot — headless has no renderer to capture]");
                }

                if (renderProbe.Render is not { } render) {
                    return CommandResult.Error(output: "[world.screenshot: the renderer is not built yet — retry after the first frame]");
                }

                // A pending request is UNFINISHED WORK, not a stale value to overwrite: arming over it would drop a
                // file the caller was already promised, with nothing on either stream to say so. Refusing names both
                // paths and leaves the first request intact, so the caller fences a frame and asks again.
                if (render.PendingCapturePath is { } outstanding) {
                    return CommandResult.Error(output: $"[world.screenshot: a capture of {outstanding} is still pending — arming over it would write no file for it; fence a frame (world.wait) and retry]");
                }

                var path = Path.GetFullPath(path: args[0].ToString());

                try {
                    if (Path.GetDirectoryName(path: path) is { Length: > 0 } directory) {
                        _ = Directory.CreateDirectory(path: directory);
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
                    return CommandResult.Error(output: $"[world.screenshot: could not create the target directory ({exception.Message})]");
                }

                render.RequestCapture(path: path);

                // "pending", not the bare path: the words are true at the instant they are printed. The capture line
                // on stderr is what says the file exists.
                return new CommandResult(Output: $"[world.screenshot: pending {path} — lands on the next composed frame]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: BindingBarCommand,
            description: "Reads or overrides one local seat's resolved on-screen binding bar: world.binding-bar [on|off|auto] [player], or world.binding-bar [player]. on/off force visibility; auto returns to authored enabled/rest behavior. The override is a session lever — checked against Mutate over section:bindings before it applies, and player 1's forced state folds into bindingOverlays[0].bindingBar.enabled at world.save. The read-back reports the resolved world-or-identity policy, its text policy (off = a purely pictographic bar: no letter badges, no page name, no chord hints), its authored slot/bank counts, the resolved (world-or-player) hideUnbound and stacked preferences, current hidden state and reason, and every layout value (scale reflects a player's own override when set). Player defaults to 1 (1..4).",
            handler: (context, args) => BindingBarHandler(
                args: in args,
                bindingBarControl: bindingBarControl,
                context: context,
                link: link
            )
        );
    }
}
