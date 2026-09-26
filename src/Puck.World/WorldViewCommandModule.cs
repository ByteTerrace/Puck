using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The window-composition verb surface — the LIVE session override <c>view.override</c> (composition authority that
/// changes what every seat sees) plus the pipe-assertable <c>world.view.state</c>, <c>world.view.pointer</c> and
/// <c>world.view.panes</c> reads.
/// The durable views-section rows are authored through the general <see cref="WorldRowCommandModule"/> —
/// <c>world.row.set views.seatRig &lt;json&gt;</c> for the keyless row, and
/// <c>world.row.set</c>/<c>world.row.remove views.layouts ...</c> for the keyed one. Control FEEL is not a views row
/// at all: it is per-seat, authored at <c>world.row.set playerDefaults.seatLook</c>. A SEPARATE module from
/// <see cref="WorldMutationCommandModule"/> to keep every class under its analyzer ceilings. <c>view.override</c>
/// routes <see cref="CommandRouting.Simulation"/> so the stdin barrier serializes a following
/// <c>world.view.state</c> read-after-write. Seat-camera state is authoritative control composition and is exposed
/// by <see cref="WorldSeatCameraCommandModule"/> in every executable shape.
/// </summary>
/// <remarks><c>view.override</c> reaches <see cref="WorldCapability.Control"/> over
/// <see cref="GrantSubject.Composition"/>, but Console can never be denied there by any reachable sequence: the
/// <c>world.grant</c>/<c>world.revoke</c> grammar has no token for the composition subject, so the seeded row can be
/// listed by <c>world.grants</c> but never revoked — and Console additionally holds
/// <see cref="WorldCapability.Control"/> over <see cref="GrantSubject.All"/>, which the check short-circuits on. Treat
/// the composition check as real for a principal that could hold it and inert for Console until the grammar can name
/// the subject.
/// <para><c>world.view.state</c> carries no principal — it is a direct read of live presentation state.</para>
/// <para>Core-registered for command-vocabulary parity: shipped worlds commit <c>view.override</c> on wheel rings,
/// and a boot shape that does not register the verb name refuses the document at vocabulary composition.
/// <see cref="IServerLink"/> and <see cref="WorldViewComposer"/> are core, so <c>view.override</c> and
/// <c>world.view.state</c> function headless; <see cref="WorldCursorFeed"/> is presentation-only, so it is optional
/// (default <see langword="null"/>) and <c>world.view.pointer</c> refuses by name when it is absent, as
/// <c>world.view.panes</c> does without the GPU presentation's <see cref="WorldViewGraphHost"/>.</para></remarks>
internal sealed class WorldViewCommandModule(IServerLink link, WorldViewComposer composer, WorldClient client, WorldCursorFeed? cursorFeed = null, WorldRenderProbe? renderProbe = null, WorldViewGraphHost? graphs = null) : ICommandModule {
    // The plan-wide clear-to-absent tokens for a live override: 'auto' (and '-') clear it back to the composer's own
    // selection; any other token is the forced name.
    private static string? ClearOrName(string token) =>
        ((string.Equals(
            a: token,
            b: "auto",
            comparisonType: StringComparison.OrdinalIgnoreCase
        ) || string.Equals(
            a: token,
            b: "-",
            comparisonType: StringComparison.Ordinal
        ))
            ? null
            : token
        );
    // A layout cycle ('toggle' or 'next') over the authored layouts the last composition saw, refused by name when there
    // are none rather than clearing the override.
    private CommandResult Cycle(CommandContext context, string? name, string token) => ((name is null)
        ? CommandResult.Error(output: $"[view.override: layout {token} has no authored views.layouts row to select]")
        : Submit(
            composition: new WorldComposition.SetActiveLayout(Name: name),
            context: context
        ));
    private static string NamesOf(IEnumerable<string> names) => ((string.Join(separator: ", ", values: names) is { Length: > 0 } joined)
        ? joined
        : "(none)");
    // Submits an override and echoes what it asked for; the server's composition gate prints a denial by name on stderr
    // and changes nothing.
    private CommandResult Submit(CommandContext context, WorldComposition composition) {
        link.SubmitComposition(
            composition: composition,
            principal: context.Principal
        );

        return new CommandResult(Output: composition switch {
            WorldComposition.SetActiveLayout layout => $"[view.override: layout {(layout.Name ?? "auto")}]",
            WorldComposition.SelectCamera camera => $"[view.override: camera {(camera.Name ?? "auto")}]",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(composition)),
        });
    }
    private CommandResult DescribePointer() {
        if (cursorFeed is not { } feed) {
            return CommandResult.Error(output: "[world.view.pointer: requires a windowed boot — headless registers this verb for vocabulary parity only]");
        }

        var status = feed.Status;
        var region = status.Viewport;
        // frame=/local= exist only once a view resolved and the client→frame mapping ran — before that (no
        // position yet, or no view this frame) printing them would pass raw client pixels off under the frame
        // label, and read-backs get believed. Omitted rather than zero-filled: an absent token cannot be mistaken
        // for a measured one.
        var mapped = ((status.Reason is "no-position" or "no-view")
            ? string.Empty
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $" frame={status.Frame.X:0.#},{status.Frame.Y:0.#} local={status.Local.X:0.###},{status.Local.Y:0.###}"
            )
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.view.pointer: player={PlayerRoster.DisplayNumber(slot: status.Slot)} position={status.Position.X:0.#},{status.Position.Y:0.#}{mapped} viewport={region.X:0.##},{region.Y:0.##},{region.Width:0.##},{region.Height:0.##} visible={status.Visible.ToString().ToLowerInvariant()} reason={status.Reason} buttons={status.Buttons} hover={((status.Hover.Length > 0)
            ? status.Hover
            : "none")} syscount={status.SystemReleaseCount}]"
        ));
    }
    // Lists the panes the render graph's root last published, in drawing order, and, given a display point, what the
    // presentation picker and the hit walk through the live instance set answer there.
    private CommandResult DescribePanes(WireArgs args) {
        if (graphs is not { } host) {
            return CommandResult.Error(output: "[world.view.panes: requires a GPU presentation — a headless boot publishes no panes]");
        }
        if (args.Count is not (0 or 2)) {
            return CommandResult.Usage(
                form: "[<x> <y>]",
                verb: "world.view.panes"
            );
        }

        var builder = new StringBuilder(value: "[world.view.panes: ");

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"display {host.DisplayWidth}x{host.DisplayHeight} panes={host.Panes.Count}"
        );

        for (var index = 0; (index < host.Panes.Count); index++) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" | pane{index} {host.Panes[index].Describe()}"
            );
        }

        if (args.Count == 2) {
            if (
                !float.TryParse(
                    provider: CultureInfo.InvariantCulture,
                    result: out var x,
                    s: args[0].ToString(),
                    style: NumberStyles.Float
                ) ||
                !float.TryParse(
                    provider: CultureInfo.InvariantCulture,
                    result: out var y,
                    s: args[1].ToString(),
                    style: NumberStyles.Float
                ) ||
                !float.IsFinite(f: x) ||
                !float.IsFinite(f: y)
            ) {
                return CommandResult.Error(output: "[world.view.panes: expected a display point as two finite numbers, in display pixels from the top-left corner]");
            }

            var picked = (host.Picker.TryPick(
                pick: out var pick,
                point: new Vector2(
                    x: x,
                    y: y
                )
            )
                ? string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{((pick.Source.Kind == SourceHandleKind.Producer) ? "producer" : "instance")}:{pick.Source.Name} pixel {pick.Hit.PixelX},{pick.Hit.PixelY}"
                )
                : "none");
            var walk = host.Walk(point: new FixedVector2(
                X: FixedQ4816.FromDouble(value: x),
                Y: FixedQ4816.FromDouble(value: y)
            ));
            var ended = ((walk is null)
                ? "no-runtime"
                : string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{walk.End} steps={walk.Steps.Count}{((walk.Instance >= 0) ? $" in {host.InstanceName(index: walk.Instance)}" : string.Empty)}"
                ));

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" | at {x:0.###},{y:0.###} pick={picked} walk={ended}"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    private string DescribeState() {
        var builder = new StringBuilder(value: "[world.view.state: ");

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"active={composer.ActiveLayoutName} selection={composer.SelectionReason} transition={composer.TransitionProgress.ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            )} slots={composer.Slots.Count}"
        );

        for (var index = 0; (index < composer.Slots.Count); index++) {
            var slot = composer.Slots[index];
            var occupant = ((slot.Instance is { } instance)
                ? (((renderProbe?.Root?.Runtime is { } runtime) && (runtime.Instances.IndexOf(name: instance) < 0))
                    ? $"instance:{instance}:missing"
                    : $"instance:{instance}")
                : ((slot.Camera is { } camera)
                    ? $"cam:{camera}"
                    : $"seat{slot.SeatOrder}"
            ));

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" slot{index}={slot.Region.X.ToString(
                    format: "0.##",
                    provider: CultureInfo.InvariantCulture
                )},{slot.Region.Y.ToString(
                    format: "0.##",
                    provider: CultureInfo.InvariantCulture
                )},{slot.Region.Width.ToString(
                    format: "0.##",
                    provider: CultureInfo.InvariantCulture
                )},{slot.Region.Height.ToString(
                    format: "0.##",
                    provider: CultureInfo.InvariantCulture
                )}:{occupant}"
            );
        }

        return builder.Append(value: ']').ToString();
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "view.override",
            description: "LIVE composition override, keyed by which slot kind it forces: view.override camera|layout <name|auto>. 'layout' forces the active window layout for every seat; 'camera' resolves every camera-bearing slot to one camera for every seat (the twin of a layout slot's own camera). 'auto' (or '-') clears the override back to the composer's own selection; 'layout toggle' and 'layout next' cycle the authored layouts. A BOUND dispatch (a wheel sector or chord row, which carries no tokens) selects the LAYOUT override by its constant Axis1D value: -1 toggles, -2 selects the next, n selects the nth authored views.layouts row (document order, 1-based), and any other value clears to auto. Echoes what it submitted ([view.override: layout <name|auto>] or [view.override: camera <name|auto>]) and refuses by name, submitting nothing, a layout or camera the live document does not author, an ordinal past its layouts, and a cycle with no authored layout. Gated Control over composition; a denial prints loudly on stderr and changes nothing.",
            routing: CommandRouting.Simulation,
            valueKind: CommandValueKind.Axis1D,
            handler: (context, args) => {
                if (
                    (context.Origin == CommandOrigin.Binding) &&
                    (args.Count == 0)
                ) {
                    var ordinal = ((int)MathF.Round(x: context.Value.AsAxis1D));

                    return (ordinal switch {
                        -1 => Cycle(context: context, name: composer.ToggleViewportIsolation(), token: "toggle"),
                        -2 => Cycle(context: context, name: composer.NextAuthoredLayoutName(), token: "next"),
                        >= 1 => ((composer.AuthoredLayoutName(ordinal: ordinal) is { } layoutName)
                            ? Submit(context: context, composition: new WorldComposition.SetActiveLayout(Name: layoutName))
                            : CommandResult.Error(output: $"[view.override: no authored layout at ordinal {ordinal}]")),
                        _ => Submit(context: context, composition: new WorldComposition.SetActiveLayout(Name: null)),
                    });
                }
                if ((args.Count == 1) && string.Equals(a: args[0].ToString(), b: "toggle", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return Cycle(
                        context: context,
                        name: composer.ToggleViewportIsolation(),
                        token: "toggle"
                    );
                }
                if (args.Count != 2) {
                    return CommandResult.Usage(
                        form: "camera|layout <name|auto|toggle|next>",
                        verb: "view.override"
                    );
                }

                var target = args[0].ToString();
                var token = args[1].ToString();

                if (string.Equals(a: target, b: "layout", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    if (string.Equals(a: token, b: "toggle", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                        return Cycle(context: context, name: composer.ToggleViewportIsolation(), token: "toggle");
                    }
                    if (string.Equals(a: token, b: "next", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                        return Cycle(context: context, name: composer.NextAuthoredLayoutName(), token: "next");
                    }

                    var layouts = client.Definition.Views.Layouts;

                    return (((ClearOrName(token: token) is { } layout) && !layouts.Any(predicate: candidate => string.Equals(a: candidate.Name, b: layout, comparisonType: StringComparison.Ordinal)))
                        ? CommandResult.Error(output: $"[view.override: no views.layouts row named '{layout}' — layouts: {NamesOf(names: layouts.Select(selector: static candidate => candidate.Name))}]")
                        : Submit(context: context, composition: new WorldComposition.SetActiveLayout(Name: ClearOrName(token: token))));
                }
                if (string.Equals(a: target, b: "camera", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    var cameras = client.Definition.Cameras;

                    return (((ClearOrName(token: token) is { } camera) && !cameras.Any(predicate: candidate => string.Equals(a: candidate.Name, b: camera, comparisonType: StringComparison.Ordinal)))
                        ? CommandResult.Error(output: $"[view.override: no camera named '{camera}' — cameras: {NamesOf(names: cameras.Select(selector: static candidate => candidate.Name))}]")
                        : Submit(context: context, composition: new WorldComposition.SelectCamera(Name: ClearOrName(token: token))));
                }

                return CommandResult.Error(output: $"[view.override: unknown target '{target}' — camera|layout]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.state",
            description: "Echoes the live window composition: world.view.state — the active layout name, selection reason (override|authored|builtin), transition progress, and each slot's rect + occupant (seat<order> | cam:<name> | instance:<name>, appended :missing when an instance slot names a views.graphs row the render graph does not run). A query (always echoes) — the pipe-assertable composition read.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "world.view.state"
            ) is { } refusal)
            ? refusal
            : new CommandResult(Output: DescribeState())),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.pointer",
            description: "Echoes the drawn cursor's last composed frame: world.view.pointer — the seat the pointer rides (1-based; the keyboard's seat, the one WorldPointerSink resolves the mouse onto), the cursor position in CLIENT pixels (position=), the same position mapped into the fixed FRAME extent the overlay draws in (frame= — the two diverge when the OS window is resized; WorldCursorFeed.Decide owns the mapping) and normalized within the seat's viewport (local=), the viewport rect, the visibility verdict (visible | no-position | no-view | outside-viewport | orbit-drag — WorldCursorFeed's one visibility rule), the held pointer buttons (buttons=, L/R/M in that order or '-' — the live store state, so an injected press is assertable before anything acts on it), the live hover target (hover=none, or the hovered panel/world row's label), and the seat's SYSTEM-RELEASE generation (syscount= — WorldPointer.SystemReleaseCount: how many times the store has force-cleared this seat's held buttons without a genuine release event; an edge-deriving consumer compares this against the value it captured at press time to tell a synthetic release from a real one). A query (always echoes) — the pipe-assertable pointer read, the world.view.camera sibling: live per-seat presentation state nothing else can echo.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "world.view.pointer"
            ) is { } refusal)
            ? refusal
            : DescribePointer()),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.panes",
            description: "Echoes the panes the render graph's root last published, in drawing order (the world's shown views, then the views.graphs panes): world.view.panes [<x> <y>] — the display extent and, per pane, its SourceMapping (the source by its instance handle, the pane's normalized rect, the source extent the instance last rendered at, the crop, layout, fit, any warp and the destination). Given a display point in display pixels from the top-left corner, it also echoes what the presentation picker answers there (pick=<kind>:<instance> pixel <x>,<y>, or none off every source) and how the hit walk through the live instance set ends (walk=<end> steps=<n>, and the instance whose world it ended in). The pipeline pane pointer maps through the same published mapping. A query (always echoes); refused by name in a boot with no GPU presentation.",
            handler: (context, args) => DescribePanes(args: args),
            routing: CommandRouting.Immediate
        );
    }
}
