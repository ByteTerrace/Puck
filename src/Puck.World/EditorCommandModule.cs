using System.Globalization;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The editor-mode console surface — the assist-layer twin of every editor chord act (pad chords are
/// the primary interface; these verbs script and narrate the same acts over the pipe). <c>editor.enter</c>/<c>exit</c>
/// flip a seat's mode through <see cref="WorldEditorSession"/> (binding mode layer + intent diversion + camera swap);
/// the camera verbs (<c>editor.camera</c>/<c>cam.speed</c>/<c>cam.pose</c>) are the typed twins of the
/// chord toggles plus the numeric setters a chord cannot express — <c>editor.camera [fly|orbit]</c> both toggles
/// (no argument) and selects explicitly, and <c>editor.cam.speed [unitsPerSecond|faster|slower]</c> both sets and
/// steps, so a chord's step/toggle twin is a bound dispatch of the same verb (a constant <see cref="CommandValue"/>
/// riding the binding row) rather than a sibling command; the router/gesture verbs (<c>editor.stick.move</c>/
/// <c>stick.look</c>/<c>ascend</c>/<c>descend</c>) are the bound-control channels the editor pages dispatch. Every
/// discrete chord act returns an echo line, so the pad's acts narrate on stdout exactly like typed verbs. A separate
/// module to keep every class under its analyzer ceilings.
/// </summary>
/// <remarks><c>editor.enter</c>/<c>exit</c> route Simulation (they divert intent through the same tick-applied
/// <c>SetControl</c> wire as <c>player.control</c>, and the stdin barrier then serializes a following read); the
/// camera verbs are presentation-only and stay Immediate.</remarks>
internal sealed class EditorCommandModule(PlayerRoster roster, WorldEditorSession session, WorldSeatBindings seatBindings, WorldEditorTargeting targeting, WorldEditorDrag drag, WorldWorkbench workbench) : ICommandModule {
    /// <summary>The rise channel (Right Shoulder, both edges) — held vertical ascent while flying.</summary>
    public const string AscendCommand = Puck.World.Client.EditorCommandNames.AscendCommand;
    /// <summary>The camera-mode act: <c>editor.camera [fly|orbit]</c>. Bound with no argument on the editor base
    /// page's South (toggles fly ⇄ orbit); bound with a constant Axis1D value on the camera page's South (fly, +1)
    /// and West (orbit, -1) for the explicit selection; typed with a literal <c>fly</c>/<c>orbit</c> token for the
    /// same explicit selection, or with none at all to toggle.</summary>
    public const string CameraToggleCommand = Puck.World.Client.EditorCommandNames.CameraToggleCommand;
    /// <summary>The sink channel (Left Shoulder, both edges) — held vertical descent while flying.</summary>
    public const string DescendCommand = Puck.World.Client.EditorCommandNames.DescendCommand;
    /// <summary>The mode entry act — bound on the default page (Gamepad Back), committed by the play wheel's
    /// Editor sector (hold Tab), and typed as <c>editor.enter [seat]</c>.</summary>
    public const string EnterCommand = Puck.World.Client.EditorCommandNames.EnterCommand;
    /// <summary>The mode exit act — bound on the editor base page (East / Back), committed by the editor wheel's
    /// Exit sector (hold Tab), and typed as <c>editor.exit [seat]</c>.</summary>
    public const string ExitCommand = Puck.World.Client.EditorCommandNames.ExitCommand;
    /// <summary>The Axis2D command the editor pages bind the right stick to (+X looks right, +Y looks up). Same
    /// routing contract as <see cref="MoveCommand"/>.</summary>
    public const string LookCommand = Puck.World.Client.EditorCommandNames.LookCommand;
    /// <summary>The Axis2D command the editor pages bind the left stick to (+Y flies forward, +X strafes right) —
    /// routed into the editing seat's camera; not meant to be typed.</summary>
    public const string MoveCommand = Puck.World.Client.EditorCommandNames.MoveCommand;
    /// <summary>The speed verb: <c>editor.cam.speed &lt;unitsPerSecond|faster|slower&gt;</c>. D-pad Up/Down on the
    /// editor pages bind it with a constant Axis1D value (+1 faster, -1 slower) in place of an argument.</summary>
    public const string SpeedCommand = Puck.World.Client.EditorCommandNames.SpeedCommand;
    /// <summary>The mode read-back — bound on the editor base page (West) and typed as <c>editor.status [seat]</c>.</summary>
    public const string StatusCommand = Puck.World.Client.EditorCommandNames.StatusCommand;

    private readonly PlayerRoster m_roster = roster;
    private readonly WorldEditorSession m_session = session;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly WorldEditorTargeting m_targeting = targeting;
    private readonly WorldEditorDrag m_drag = drag;
    private readonly WorldWorkbench m_workbench = workbench;

    /// <summary>Resolves the acting seat: a present trailing [seat] token (1..4) is authoritative; an absent one falls
    /// back to the invocation's slot — the pressing device's seat for a bound chord act, and the text path's default
    /// seat 1 (<see cref="CommandContext.Slot"/> is 0 there by contract). Token presence is the discriminator, never
    /// <see cref="CommandContext.Parse"/>: the registry's Immediate fast path hands wire handlers a null
    /// Parse for typed lines too, so a Parse-null test would silently ignore a typed seat token. Internal —
    /// <see cref="EditorSelectionCommandModule"/> shares the same convention.</summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="args">The verb args.</param>
    /// <param name="at">The trailing seat token's index.</param>
    /// <param name="verb">The verb name for error text.</param>
    /// <returns>The resolved 0-based slot, or an error result on a malformed index (-1 slot).</returns>
    internal static (int Slot, CommandResult? Error) ResolveSlot(CommandContext context, in WireArgs args, int at, string verb) {
        if (args.Count <= at) {
            return (Slot: context.Slot, Error: null);
        }

        if (!WorldArgs.TryParseIndex(
            args: args,
            at: at,
            fallback: null,
            max: PlayerRoster.MaxSlots,
            min: 1,
            value: out var seat
        )) {
            return (Slot: -1, Error: CommandResult.Error(output: $"[{verb}: seat must be an integer 1..{PlayerRoster.MaxSlots}]"));
        }

        return (Slot: PlayerRoster.SlotFromDisplay(number: seat), Error: null);
    }
    /// <summary>Resolves a two-way step/selection from either a literal token at <paramref name="at"/> (case-insensitive
    /// match against <paramref name="positive"/>/<paramref name="negative"/>) or, when no token is present, the sign of
    /// a bound dispatch's constant value: a step-twin binding row folds onto an argument-bearing verb by carrying
    /// <c>CommandValue.Axis(+1)</c>/<c>Axis(-1)</c> in place of an argument (the mechanism behind every
    /// <c>.next</c>/<c>.prev</c>/<c>.up</c>/<c>.down</c>/<c>.grow</c>/<c>.shrink</c> twin killed in this wave — see
    /// <c>editor.select</c>, <c>editor.sculpt.select/scale/material/blend/primitive/frame</c>). Every such verb declares
    /// <c>valueKind: CommandValueKind.Axis1D</c> at registration (see <see cref="CommandDefinition.WithWireArgs"/>'s
    /// doctrine comment) so <see cref="BindingVocabularyCheck"/> admits the row — which means
    /// <see cref="CommandContext.Value"/>'s kind can no longer discriminate bound from typed (the text path's own
    /// impulse value now reads Axis1D too). The discriminator is <see cref="CommandContext.Source"/> instead — the
    /// deterministic physical or synthesized binding owner, documented null for every text path. Internal — every
    /// sibling sculpt module shares this fold.</summary>
    /// <param name="context">The invocation context (its <see cref="CommandContext.Value"/> carries the bound constant
    /// when <see cref="CommandContext.Source"/> is non-null).</param>
    /// <param name="args">The verb args.</param>
    /// <param name="at">The token index a literal direction word would occupy.</param>
    /// <param name="positive">The literal token (e.g. <c>"next"</c>, <c>"faster"</c>, <c>"grow"</c>) meaning +1.</param>
    /// <param name="negative">The literal token (e.g. <c>"prev"</c>, <c>"slower"</c>, <c>"shrink"</c>) meaning -1.</param>
    /// <param name="direction">+1 or -1 when resolved; 0 otherwise.</param>
    /// <returns><see langword="true"/> when a direction was resolved (token match or a bound dispatch).</returns>
    internal static bool TryDirection(CommandContext context, in WireArgs args, int at, string positive, string negative, out int direction) {
        if (args.Count > at) {
            if (args.Is(
                index: at,
                value: positive
            )) {
                direction = 1;

                return true;
            }

            if (args.Is(
                index: at,
                value: negative
            )) {
                direction = -1;

                return true;
            }

            direction = 0;

            return false;
        }

        if (context.Origin == CommandOrigin.Binding) {
            direction = ((context.Value.AsAxis1D >= 0f)
                ? 1
                : -1
            );

            return true;
        }

        direction = 0;

        return false;
    }
    // The shared FINITE parse boundary: NaN/infinity never enters camera, snap, or preview state — a
    // non-finite center would poison the SDF rebuild and a NaN pitch slides past ordinary range guards.
    internal static bool TryFloat(in WireArgs args, int at, out float value) {
        return args.TryFloat(
            index: at,
            value: out value
        );
    }
    // The same boundary for a token already in hand — the drag/move/place/snap and speaker paths hold a span rather
    // than an argument index, and must not reach a weaker parse for it.
    internal static bool TryFloat(ReadOnlySpan<char> token, out float value) =>
        (float.TryParse(
            s: token,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out value
        ) && float.IsFinite(f: value));

    // The camera fold: a literal fly|orbit token -> explicit selection (typed only — a token always came through
    // WireArgs, never a bound dispatch, which carries none). With no token, the discriminator is
    // CommandContext.Origin (see CommandDefinition.WithWireArgs's valueKind doctrine comment; Value.Kind is NOT a
    // safe discriminator once this verb declares Axis1D, because the text path's own impulse value would then also
    // read as Axis1D):
    //   bound, axis > 0  -> fly (the camera page's South row, Axis(1f))
    //   bound, axis < 0  -> orbit (the camera page's West row, Axis(-1f))
    //   bound, axis == 0 -> toggle (the base page's South row, Axis(0f) — the plain chord act)
    //   typed, no token  -> echoes the current mode (read-only) — a genuinely NEW reachable case: the old bare Verb
    //                       always errored on a typed no-arg call, so this is a strict improvement, not a fold to
    //                       preserve, and matches the pervasive lever convention (no-arg reads back).
    private CommandResult CameraHandler(CommandContext context, WireArgs args) {
        var hasToken = (args.Count >= 1);

        if (
            hasToken &&
            !args.Is(
            index: 0,
            value: "fly"
        ) &&
            !args.Is(
            index: 0,
            value: "orbit"
        )
        ) {
            return CommandResult.Error(output: $"[{CameraToggleCommand}: expected fly, orbit, or nothing (toggle)]");
        }

        var (slot, error) = ResolveSlot(
            args: args,
            at: (hasToken
            ? 1
            : 0),
            context: context,
            verb: CameraToggleCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        var isBound = (context.Origin == CommandOrigin.Binding);
        EditorCameraMode? explicitMode = null;

        if (hasToken) {
            explicitMode = (args.Is(
                index: 0,
                value: "fly"
            )
                ? EditorCameraMode.Fly
                : EditorCameraMode.Orbit
            );
        } else if (isBound) {
            var axis = context.Value.AsAxis1D;

            explicitMode = ((axis > 0f)
                ? EditorCameraMode.Fly
                : ((axis < 0f)
                    ? EditorCameraMode.Orbit
                    : null
            ));
        }

        if (explicitMode is { } mode) {
            if (m_session.NotEditingError(
                slot: slot,
                verb: CameraToggleCommand
            ) is { } notEditingExplicit) {
                return notEditingExplicit;
            }

            m_session.SetMode(
                mode: mode,
                slot: slot
            );

            return new CommandResult(Output: $"[{CameraToggleCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} camera {ModeWord(mode: mode)}]");
        }

        if (!isBound) {
            // A genuinely typed no-arg line: read back, never mutate.
            if (m_session.NotEditingError(
                slot: slot,
                verb: CameraToggleCommand
            ) is { } notEditingRead) {
                return notEditingRead;
            }

            return new CommandResult(Output: $"[{CameraToggleCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} camera {ModeWord(mode: m_session.Mode(slot: slot))}]");
        }

        // The bound plain toggle (axis == 0, the base page's South chord). Silent None when not editing preserves
        // the chord's old dead-path behavior (the page never activates outside editor mode, so this is unreachable
        // through real input).
        if (!m_session.IsEditing(slot: slot)) {
            return CommandResult.None;
        }

        var toggled = ((m_session.Mode(slot: slot) == EditorCameraMode.Fly)
            ? EditorCameraMode.Orbit
            : EditorCameraMode.Fly
        );

        m_session.SetMode(
            mode: toggled,
            slot: slot
        );

        return new CommandResult(Output: $"[{CameraToggleCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} camera {ModeWord(mode: toggled)}]");
    }
    private CommandResult EnterHandler(CommandContext context, WireArgs args) {
        var (slot, error) = ResolveSlot(
            args: args,
            at: 0,
            context: context,
            verb: EnterCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        var outcome = m_session.Enter(slot: slot);

        return (outcome switch {
            EditorModeOutcome.Applied => new CommandResult(Output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)} editing — group editor, sticks fly, LT camera page, East/Back exits]"),
            EditorModeOutcome.AlreadyThere => new CommandResult(Output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)} is already editing]"),
            EditorModeOutcome.Pending => CommandResult.Error(output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)} is pending — confirm an identity first (South/Enter or player.identity)]"),
            EditorModeOutcome.NoBindingGroup => CommandResult.Error(output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)}'s document maps no group to the {WorldContextFamilies.Editor}={WorldContextFamilies.EditorEditing} context, so editor verbs would resolve against the play page — the mode was NOT entered]"),
            // Every outcome is named individually — a catch-all reporting a fixed reason would announce a future new
            // outcome under someone else's cause, and a refusal naming the wrong cause is worse than silence.
            EditorModeOutcome.NotJoined => CommandResult.Error(output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)} is not joined — see world.players]"),
            _ => throw new InvalidOperationException(message: $"unhandled {nameof(EditorModeOutcome)} '{outcome}'"),
        });
    }
    private CommandResult ExitHandler(CommandContext context, WireArgs args) {
        // A leading 'force' literal overrides the dirty-sculpt refusal; the seat token (if any) follows it.
        var forced = args.Is(
            index: 0,
            value: "force"
        );
        var seatAt = (forced
            ? 1
            : 0
        );

        var (slot, error) = ResolveSlot(
            args: args,
            at: seatAt,
            context: context,
            verb: ExitCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        // Leaving editor mode drops the seat's open sculpt bench (WorldEditorSession.Deactivate). Refuse loudly rather
        // than discard uncommitted sculpt work by side effect — the codebase's verification culture exists to prevent
        // exactly this silent-discard shape.
        if (
            !forced &&
            m_session.IsEditing(slot: slot) &&
            (m_workbench.UncommittedEdits(slot: slot) > 0)
        ) {
            return CommandResult.Error(output: $"[{ExitCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has {m_workbench.UncommittedEdits(slot: slot)} uncommitted sculpt edit(s) — editor.sculpt.commit to keep, editor.sculpt.exit to discard the bench, or 'editor.exit force' to leave anyway]");
        }

        return (m_session.Exit(slot: slot) switch {
            EditorModeOutcome.Applied => new CommandResult(Output: $"[editor.exit: seat {PlayerRoster.DisplayNumber(slot: slot)} — chase camera restored, avatar drives again]"),
            _ => CommandResult.Error(output: $"[editor.exit: seat {PlayerRoster.DisplayNumber(slot: slot)} was not editing]"),
        });
    }
    private CommandResult LookRouter(CommandContext context) {
        if (context.Origin != CommandOrigin.Binding) {
            return CommandResult.Error(output: "[editor.stick.look: a routed stick channel, not a typed verb — script the camera with editor.cam.pose]");
        }

        m_session.RouteLook(
            slot: context.Slot,
            look: context.Value.AsAxis2D
        );

        return CommandResult.None;
    }
    private static string ModeWord(EditorCameraMode mode) => ((mode == EditorCameraMode.Orbit)
        ? "orbit"
        : "fly"
    );
    private CommandResult MoveRouter(CommandContext context) {
        if (context.Origin != CommandOrigin.Binding) {
            return CommandResult.Error(output: "[editor.stick.move: a routed stick channel, not a typed verb — script the camera with editor.cam.pose or a drag with editor.drag]");
        }

        m_session.RouteMove(
            slot: context.Slot,
            move: context.Value.AsAxis2D
        );

        return CommandResult.None;
    }
    private CommandResult PoseHandler(CommandContext context, WireArgs args) {
        // Shapes: 3 = <x y z>; 4 = +seat; 5 = +<yaw pitch>; 6 = +<yaw pitch> +seat.
        if (args.Count is (< 3 or > 6)) {
            return CommandResult.Error(output: "[editor.cam.pose: expected <x> <y> <z> [<yawDeg> <pitchDeg>] [seat]]");
        }

        var hasAngles = (args.Count >= 5);
        var seatAt = (hasAngles
            ? 5
            : 3
        );

        if (
            !TryFloat(
            args: args,
            at: 0,
            value: out var x
        ) ||
            !TryFloat(
            args: args,
            at: 1,
            value: out var y
        ) ||
            !TryFloat(
            args: args,
            at: 2,
            value: out var z
        )
        ) {
            return CommandResult.Error(output: "[editor.cam.pose: could not parse <x> <y> <z> as finite numbers]");
        }

        var yawDegrees = 0f;
        var pitchDegrees = 0f;

        if (
            hasAngles &&
            (!TryFloat(
            args: args,
            at: 3,
            value: out yawDegrees
        ) || !TryFloat(
            args: args,
            at: 4,
            value: out pitchDegrees
        ))
        ) {
            return CommandResult.Error(output: "[editor.cam.pose: could not parse <yawDeg> <pitchDeg> as finite numbers]");
        }

        var (slot, error) = ResolveSlot(
            args: args,
            at: seatAt,
            context: context,
            verb: "editor.cam.pose"
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(
            slot: slot,
            verb: "editor.cam.pose"
        ) is { } notEditing) {
            return notEditing;
        }

        const float ToRadians = (MathF.PI / 180f);

        m_session.SetPose(
            slot: slot,
            eye: new System.Numerics.Vector3(
                x: x,
                y: y,
                z: z
            ),
            yawRadians: (yawDegrees * ToRadians),
            pitchRadians: (pitchDegrees * ToRadians)
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[editor.cam.pose: seat {PlayerRoster.DisplayNumber(slot: slot)} eye=({x:0.00}, {y:0.00}, {z:0.00}) yaw={yawDegrees:0}° pitch={pitchDegrees:0}°]"
        ));
    }
    private CommandResult SpeedHandler(CommandContext context, WireArgs args) {
        // The step fold: a literal faster|slower token, or (with no token) a bound constant Axis1D value from the
        // D-pad Up/Down step chord — see TryDirection. Anything else falls through to the numeric <unitsPerSecond> form.
        if (TryDirection(
            args: args,
            at: 0,
            context: context,
            direction: out var direction,
            negative: "slower",
            positive: "faster"
        )) {
            var (stepSlot, stepError) = ResolveSlot(
                context: context,
                args: args,
                at: ((args.Count >= 1)
                ? 1
                : 0),
                verb: "editor.cam.speed"
            );

            if (stepError is { } resolveStepError) {
                return resolveStepError;
            }

            if (m_session.NotEditingError(
                slot: stepSlot,
                verb: "editor.cam.speed"
            ) is { } notEditingStep) {
                return notEditingStep;
            }

            var stepped = m_session.StepSpeed(
                slot: stepSlot,
                up: (direction > 0)
            );

            return new CommandResult(Output: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"[editor.cam.speed: seat {PlayerRoster.DisplayNumber(slot: stepSlot)} {stepped:0.##} u/s]"
            ));
        }

        if (args.Count is (< 1 or > 2)) {
            return CommandResult.Error(output: "[editor.cam.speed: expected <unitsPerSecond|faster|slower> plus an optional seat 1..4]");
        }

        if (!TryFloat(
            args: args,
            at: 0,
            value: out var speed
        )) {
            return CommandResult.Error(output: "[editor.cam.speed: could not parse <unitsPerSecond> as a finite number]");
        }

        var (slot, error) = ResolveSlot(
            args: args,
            at: 1,
            context: context,
            verb: "editor.cam.speed"
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_session.NotEditingError(
            slot: slot,
            verb: "editor.cam.speed"
        ) is { } notEditing) {
            return notEditing;
        }

        var applied = m_session.SetSpeed(
            slot: slot,
            unitsPerSecond: speed
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[editor.cam.speed: seat {PlayerRoster.DisplayNumber(slot: slot)} {applied:0.##} u/s]"
        ));
    }
    private CommandResult StatusHandler(CommandContext context, WireArgs args) {
        var (slot, error) = ResolveSlot(
            args: args,
            at: 0,
            context: context,
            verb: StatusCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        var seat = PlayerRoster.DisplayNumber(slot: slot);

        if (!m_session.IsEditing(slot: slot)) {
            // The active group AND page ride the not-editing echo too — the scripted assertion point for the exit
            // flip and for the play group's held-chord page turns.
            var resting = m_seatBindings.PageView(slot: slot);

            return new CommandResult(Output: $"[editor.status: seat {seat} not editing group={resting.Group} page={resting.PageId} '{(resting.Label ?? resting.PageId)}']");
        }

        var view = m_seatBindings.PageView(slot: slot);
        var eye = m_session.Eye(slot: slot);
        // The selection/drag facts ride the same line — the scripted assertion point for selection/drag acts.
        var selection = "sel=none";

        if (m_targeting.Selected(slot: slot) is { } selected) {
            var position = (m_targeting.SelectionPosition(slot: slot) ?? default);

            selection = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"sel={selected.Describe()} at ({position.X:0.00}, {position.Y:0.00}, {position.Z:0.00})"
            );
        }

        var dragState = ((m_drag.Describe(slot: slot) is { } dragLine)
            ? $" drag={dragLine}"
            : string.Empty
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[editor.status: seat {seat} editing {ModeWord(mode: m_session.Mode(slot: slot))} speed={m_session.Speed(slot: slot):0.##} group={view.Group} page={view.PageId} '{(view.Label ?? view.PageId)}' eye=({eye.X:0.00}, {eye.Y:0.00}, {eye.Z:0.00}) {selection} cand={m_targeting.CandidateCount(slot: slot)} (r {m_targeting.CandidateRadius:0}u, cap {m_targeting.CandidateCap}){dragState}]"
        ));
    }
    private CommandResult VerticalHandler(CommandContext context, bool ascend, string name) {
        if (context.Origin != CommandOrigin.Binding) {
            return CommandResult.Error(output: $"[{name}: a held control, not a typed verb — use editor.cam.pose to script the camera]");
        }

        m_session.SetVertical(
            slot: context.Slot,
            ascend: ascend,
            held: (context.Phase is CommandPhase.Started or CommandPhase.Active)
        );

        return CommandResult.None;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: EnterCommand,
            description: "Enters editor mode for a seat: editor.enter [seat] (1..4, default 1; the pressing device's seat on the bound Gamepad Back, or the play wheel's Editor sector — hold Tab, release over it; the triggers turn pages, they never enter a mode). The seat's avatar idles honestly (intent diverts to the player.control idle contract — a live tape or player.press still drives), its sticks fly the editor camera seeded exactly at the current chase framing, and the seat's active binding group flips to 'editor' (a pointer switch on the compiled profile — the bar renders the editor pages at once; the group's five ordered trigger chords select them: nothing held = resting, LT = camera, RT = select, LT-then-RT = place, RT-then-LT = the reverse page). Exit with East / Back, the editor wheel's Exit sector, or editor.exit.",
            handler: EnterHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ExitCommand,
            description: "Leaves editor mode for a seat: editor.exit [force] [seat] (seat 1..4, default 1; the pressing device's seat on the bound East / Back, or the editor wheel's Exit sector — hold Tab, release over it). Restores the seat's prior intent source and its chase camera (re-anchored to the avatar — no pose pop) and flips the active binding group back to 'play'. A friendly no-op when the seat was not editing. REFUSES when the seat has an open sculpt with uncommitted edits (leaving would silently discard them) — commit with editor.sculpt.commit, discard explicitly with editor.sculpt.exit, or force through with 'editor.exit force'.",
            handler: ExitHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: StatusCommand,
            description: "Echoes a seat's editor state: editor.status [seat] (1..4, default 1) — editing/not-editing, the camera mode and speed, the active binding group and page (id + label), and the editor eye. The scripted assertion point for mode and group flips.",
            handler: StatusHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SpeedCommand,
            description: "Sets or steps a seat's editor fly speed in world units per second (clamped 0.5..64): editor.cam.speed <unitsPerSecond|faster|slower> [seat]. 'faster'/'slower' step ×1.5 / ÷1.5 — the typed twins of the D-pad Up/Down speed-step chord, which binds this same verb with no argument (direction from the binding's constant value).",
            handler: SpeedHandler,
            // Every binding row targeting this verb carries a constant Axis1D value (no row sends a plain digital
            // press) — declared here so BindingVocabularyCheck admits the row instead of rejecting every future
            // recompose (player.bind / world.row.set bindingOverlays / profile load) that touches this seat.
            valueKind: CommandValueKind.Axis1D
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "editor.cam.pose",
            description: "Teleports a seat's editor camera to an explicit pose — the console twin of stick flight (forces fly mode): editor.cam.pose <x> <y> <z> [<yawDeg> <pitchDeg>] [seat]. Yaw 0 looks down +Z (the camera-rig convention), pitch positive looks up (clamped). Accepted shapes: 3 values (pose, level, seat 1), 4 (+seat), 5 (+yaw+pitch), 6 (+yaw+pitch+seat).",
            handler: PoseHandler
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: MoveCommand,
            description: "The editor pages' left-stick flight channel (Axis2D) — routed to the editing seat's camera each tick; not meant to be typed (script the camera with editor.cam.pose instead).",
            valueKind: CommandValueKind.Axis2D,
            handler: MoveRouter
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: LookCommand,
            description: "The editor pages' right-stick look channel (Axis2D) — routed to the editing seat's camera each tick; not meant to be typed (script the camera with editor.cam.pose instead).",
            valueKind: CommandValueKind.Axis2D,
            handler: LookRouter
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: AscendCommand,
            description: "Holds the editing seat's vertical RISE channel while its button is down (Right Shoulder, both edges). A held control, not a typed verb — script the camera with editor.cam.pose.",
            valueKind: CommandValueKind.Digital,
            held: true,
            handler: context => VerticalHandler(
                ascend: true,
                context: context,
                name: AscendCommand
            )
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: DescendCommand,
            description: "Holds the editing seat's vertical SINK channel while its button is down (Left Shoulder, both edges). A held control, not a typed verb — script the camera with editor.cam.pose.",
            valueKind: CommandValueKind.Digital,
            held: true,
            handler: context => VerticalHandler(
                ascend: false,
                context: context,
                name: DescendCommand
            )
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: CameraToggleCommand,
            description: "Toggles (no argument) or explicitly selects (fly|orbit) the editing seat's camera: editor.camera [fly|orbit] [seat]. The editor base page's South binds this with a constant zero value (toggle); the LT camera page's South/West bind it with a constant +1/-1 value selecting fly/orbit directly.",
            handler: CameraHandler,
            // The base page's toggle row ALSO carries a constant (Axis(0f), not a plain digital press) so every row
            // targeting this verb dispatches the SAME kind — BindingVocabularyCheck refuses a recompose the moment
            // any one row's dispatched kind disagrees with this declaration, so a mixed Digital/Axis1D row set on one
            // verb is not an option.
            valueKind: CommandValueKind.Axis1D
        );
    }
}
