using System.Globalization;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The editor-mode console surface — the assist-layer twin of every editor chord act (§chord-first: pad chords are
/// the primary interface; these verbs script and narrate the same acts over the pipe). <c>editor.enter</c>/<c>exit</c>
/// flip a seat's mode through <see cref="WorldEditorSession"/> (binding mode layer + intent diversion + camera swap);
/// the camera verbs (<c>editor.fly</c>/<c>orbit</c>/<c>cam.speed</c>/<c>cam.pose</c>) are the typed twins of the
/// chord toggles plus the numeric setters a chord cannot express; the router/gesture verbs (<c>editor.stick.move</c>/
/// <c>stick.look</c>/<c>ascend</c>/<c>descend</c>/<c>camera</c>/<c>faster</c>/<c>slower</c>) are the bound-control channels
/// the editor pages dispatch. Every discrete chord act returns an echo line, so the pad's acts narrate on stdout
/// exactly like typed verbs. A SEPARATE module to keep every class under its analyzer ceilings.
/// </summary>
/// <remarks><c>editor.enter</c>/<c>exit</c> route Simulation (they divert intent through the same tick-applied
/// <c>SetControl</c> wire as <c>player.control</c>, and the stdin barrier then serializes a following read); the
/// camera verbs are presentation-only and stay Immediate.</remarks>
internal sealed class EditorCommandModule(PlayerRoster roster, WorldEditorSession session, WorldSeatBindings seatBindings, WorldEditorTargeting targeting, WorldEditorDrag drag) : ICommandModule {
    /// <summary>The Axis2D command the editor pages bind the LEFT stick to (+Y flies forward, +X strafes right) —
    /// routed into the editing seat's camera; not meant to be typed.</summary>
    public const string MoveCommand = "editor.stick.move";
    /// <summary>The Axis2D command the editor pages bind the RIGHT stick to (+X looks right, +Y looks up). Same
    /// routing contract as <see cref="MoveCommand"/>.</summary>
    public const string LookCommand = "editor.stick.look";
    /// <summary>The rise channel (Right Shoulder, both edges) — held vertical ascent while flying.</summary>
    public const string AscendCommand = "editor.ascend";
    /// <summary>The sink channel (Left Shoulder, both edges) — held vertical descent while flying.</summary>
    public const string DescendCommand = "editor.descend";
    /// <summary>The camera-mode toggle chord act (South on the editor base page): fly ⇄ orbit.</summary>
    public const string CameraToggleCommand = "editor.camera";
    /// <summary>The speed-step-up chord act (D-pad Up on the editor pages).</summary>
    public const string FasterCommand = "editor.faster";
    /// <summary>The speed-step-down chord act (D-pad Down on the editor pages).</summary>
    public const string SlowerCommand = "editor.slower";
    /// <summary>The mode entry act — bound on the DEFAULT page (Gamepad Back / Keyboard Tab) and typed as
    /// <c>editor.enter [seat]</c>.</summary>
    public const string EnterCommand = "editor.enter";
    /// <summary>The mode exit act — bound on the editor base page (East / Back / Tab) and typed as
    /// <c>editor.exit [seat]</c>.</summary>
    public const string ExitCommand = "editor.exit";
    /// <summary>The mode read-back — bound on the editor base page (West) and typed as <c>editor.status [seat]</c>.</summary>
    public const string StatusCommand = "editor.status";
    /// <summary>The explicit fly-mode selection (the camera page's South; typed <c>editor.fly [seat]</c>).</summary>
    public const string FlyCommand = "editor.fly";
    /// <summary>The explicit orbit-mode selection (the camera page's West; typed <c>editor.orbit [seat]</c>).</summary>
    public const string OrbitCommand = "editor.orbit";

    private readonly PlayerRoster m_roster = roster;
    private readonly WorldEditorSession m_session = session;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly WorldEditorTargeting m_targeting = targeting;
    private readonly WorldEditorDrag m_drag = drag;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: EnterCommand,
            description: "Enters editor mode for a seat: editor.enter [seat] (1..4, default 1; the pressing device's seat on the bound LT-then-RT chord or Gamepad Back / Keyboard Tab). The seat's avatar idles honestly (intent diverts to the player.control idle contract — a live tape or player.press still drives), its sticks fly the editor camera seeded exactly at the current chase framing, and the seat's active binding group flips to 'editor' (a pointer switch on the compiled profile — the bar renders the editor pages at once; LT holds the camera page, RT the select page). Exit with East / Back / Tab or editor.exit.",
            handler: EnterHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: ExitCommand,
            description: "Leaves editor mode for a seat: editor.exit [seat] (1..4, default 1; the pressing device's seat on the bound East / Back / Tab). Restores the seat's prior intent source and its chase camera (re-anchored to the avatar — no pose pop) and flips the active binding group back to 'play'. A friendly no-op when the seat was not editing.",
            handler: ExitHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: StatusCommand,
            description: "Echoes a seat's editor state: editor.status [seat] (1..4, default 1) — editing/not-editing, the camera mode and speed, the active binding group and page (id + label), and the editor eye. The scripted assertion point for mode and group flips.",
            handler: StatusHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: FlyCommand,
            description: "Selects the FREE-FLY editor camera for a seat (the chord twin is South on the LT camera page): editor.fly [seat]. Sticks fly (left translates along the view, right looks, shoulders rise/sink); the switch adopts the orbit's vantage seamlessly.",
            handler: (context, args) => ModeHandler(context: context, args: args, mode: EditorCameraMode.Fly, verb: FlyCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: OrbitCommand,
            description: "Selects the ORBIT editor camera for a seat (the chord twin is West on the LT camera page): editor.orbit [seat]. The left stick orbits the seat's avatar (P3 retargets the pivot onto the selection), the right stick's Y zooms.",
            handler: (context, args) => ModeHandler(context: context, args: args, mode: EditorCameraMode.Orbit, verb: OrbitCommand)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.cam.speed",
            description: "Sets a seat's editor fly speed in world units per second (clamped 0.5..64; the chord twins are D-pad Up/Down speed steps): editor.cam.speed <unitsPerSecond> [seat].",
            handler: SpeedHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "editor.cam.pose",
            description: "Teleports a seat's editor camera to an explicit pose — the console twin of stick flight (forces fly mode): editor.cam.pose <x> <y> <z> [<yawDeg> <pitchDeg>] [seat]. Yaw 0 looks down +Z (the camera-rig convention), pitch positive looks up (clamped). Accepted shapes: 3 values (pose, level, seat 1), 4 (+seat), 5 (+yaw+pitch), 6 (+yaw+pitch+seat).",
            handler: PoseHandler
        );
        yield return CommandDefinition.Verb(
            name: MoveCommand,
            description: "The editor pages' left-stick flight channel (Axis2D) — routed to the editing seat's camera each tick; not meant to be typed (script the camera with editor.cam.pose instead).",
            valueKind: CommandValueKind.Axis2D,
            handler: MoveRouter
        );
        yield return CommandDefinition.Verb(
            name: LookCommand,
            description: "The editor pages' right-stick look channel (Axis2D) — routed to the editing seat's camera each tick; not meant to be typed (script the camera with editor.cam.pose instead).",
            valueKind: CommandValueKind.Axis2D,
            handler: LookRouter
        );
        yield return CommandDefinition.Verb(
            name: AscendCommand,
            description: "Holds the editing seat's vertical RISE channel while its button is down (Right Shoulder, both edges). A held control, not a typed verb — script the camera with editor.cam.pose.",
            valueKind: CommandValueKind.Digital,
            handler: context => VerticalHandler(context: context, ascend: true, name: AscendCommand)
        );
        yield return CommandDefinition.Verb(
            name: DescendCommand,
            description: "Holds the editing seat's vertical SINK channel while its button is down (Left Shoulder, both edges). A held control, not a typed verb — script the camera with editor.cam.pose.",
            valueKind: CommandValueKind.Digital,
            handler: context => VerticalHandler(context: context, ascend: false, name: DescendCommand)
        );
        yield return CommandDefinition.Verb(
            name: CameraToggleCommand,
            description: "Toggles the editing seat's camera between fly and orbit (South on the editor base page). The typed twins are editor.fly / editor.orbit.",
            valueKind: CommandValueKind.Digital,
            handler: ToggleHandler
        );
        yield return CommandDefinition.Verb(
            name: FasterCommand,
            description: "Steps the editing seat's fly speed up ×1.5 (D-pad Up on the editor pages). The typed twin is editor.cam.speed <v>.",
            valueKind: CommandValueKind.Digital,
            handler: context => StepHandler(context: context, up: true, name: FasterCommand)
        );
        yield return CommandDefinition.Verb(
            name: SlowerCommand,
            description: "Steps the editing seat's fly speed down ÷1.5 (D-pad Down on the editor pages). The typed twin is editor.cam.speed <v>.",
            valueKind: CommandValueKind.Digital,
            handler: context => StepHandler(context: context, up: false, name: SlowerCommand)
        );
    }

    // Resolve the acting seat: a PRESENT trailing [seat] token (1..4) is authoritative; an absent one falls back to
    // the invocation's slot — the pressing device's seat for a bound chord act, and the text path's default seat 1
    // (CommandContext.Slot is 0 there by contract). Token PRESENCE is the discriminator, never context.Parse: the
    // registry's Immediate fast path hands trailing-args handlers a null Parse for TYPED lines too, so a Parse-null
    // test silently ignored a typed seat token (the editor-edit proof's depart round caught it). Returns -1 with an
    // error result on a malformed index. Internal: EditorSelectionCommandModule shares the same convention.
    internal static (int Slot, CommandResult? Error) ResolveSlot(CommandContext context, string[] args, int at, string verb) {
        if (args.Length <= at) {
            return (Slot: context.Slot, Error: null);
        }

        if (!WorldArgs.TryParseIndex(args: args, at: at, min: 1, max: PlayerRoster.MaxSlots, fallback: null, value: out var seat)) {
            return (Slot: -1, Error: new CommandResult(Output: $"[{verb}: seat must be an integer 1..{PlayerRoster.MaxSlots}]") {
                IsError = true,
            });
        }

        return (Slot: PlayerRoster.SlotFromDisplay(number: seat), Error: null);
    }

    private CommandResult EnterHandler(CommandContext context, string[] args) {
        var (slot, error) = ResolveSlot(context: context, args: args, at: 0, verb: EnterCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        return (m_session.Enter(slot: slot) switch {
            EditorModeOutcome.Applied => new CommandResult(Output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)} editing — group editor, sticks fly, LT camera page, East/Back exits]"),
            EditorModeOutcome.AlreadyThere => new CommandResult(Output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)} is already editing]"),
            EditorModeOutcome.Pending => new CommandResult(Output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)} is pending — confirm a profile first (South/Enter or player.profile)]") {
                IsError = true,
            },
            _ => new CommandResult(Output: $"[editor.enter: seat {PlayerRoster.DisplayNumber(slot: slot)} is not joined — see world.players]") {
                IsError = true,
            },
        });
    }

    private CommandResult ExitHandler(CommandContext context, string[] args) {
        var (slot, error) = ResolveSlot(context: context, args: args, at: 0, verb: ExitCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        return (m_session.Exit(slot: slot) switch {
            EditorModeOutcome.Applied => new CommandResult(Output: $"[editor.exit: seat {PlayerRoster.DisplayNumber(slot: slot)} — chase camera restored, avatar drives again]"),
            _ => new CommandResult(Output: $"[editor.exit: seat {PlayerRoster.DisplayNumber(slot: slot)} was not editing]"),
        });
    }

    private CommandResult StatusHandler(CommandContext context, string[] args) {
        var (slot, error) = ResolveSlot(context: context, args: args, at: 0, verb: StatusCommand);

        if (error is { } resolveError) {
            return resolveError;
        }

        var seat = PlayerRoster.DisplayNumber(slot: slot);

        if (!m_session.IsEditing(slot: slot)) {
            // The active group rides the not-editing echo too — the scripted assertion point for the exit flip.
            return new CommandResult(Output: $"[editor.status: seat {seat} not editing group={m_seatBindings.PageView(slot: slot).Group}]");
        }

        var view = m_seatBindings.PageView(slot: slot);
        var eye = m_session.Eye(slot: slot);
        // The selection/drag facts ride the same line — the scripted assertion point for the P3 acts.
        var selection = "sel=none";

        if (m_targeting.Selected(slot: slot) is { } selected) {
            var position = (m_targeting.SelectionPosition(slot: slot) ?? default);

            selection = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"sel={selected.Describe()} at ({position.X:0.00}, {position.Y:0.00}, {position.Z:0.00})"
            );
        }

        var dragState = ((m_drag.Describe(slot: slot) is { } dragLine) ? $" drag={dragLine}" : string.Empty);

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[editor.status: seat {seat} editing {ModeWord(mode: m_session.Mode(slot: slot))} speed={m_session.Speed(slot: slot):0.##} group={view.Group} page={view.PageId} '{view.Label ?? view.PageId}' eye=({eye.X:0.00}, {eye.Y:0.00}, {eye.Z:0.00}) {selection} cand={m_targeting.CandidateCount(slot: slot)} (r {m_targeting.CandidateRadius:0}u, cap {m_targeting.CandidateCap}){dragState}]"
        ));
    }

    private CommandResult ModeHandler(CommandContext context, string[] args, EditorCameraMode mode, string verb) {
        var (slot, error) = ResolveSlot(context: context, args: args, at: 0, verb: verb);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (NotEditingError(slot: slot, verb: verb) is { } notEditing) {
            return notEditing;
        }

        m_session.SetMode(slot: slot, mode: mode);

        return new CommandResult(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} camera {ModeWord(mode: mode)}]");
    }

    private CommandResult SpeedHandler(CommandContext context, string[] args) {
        if (args.Length is (< 1 or > 2)) {
            return new CommandResult(Output: "[editor.cam.speed: expected <unitsPerSecond> plus an optional seat 1..4]") {
                IsError = true,
            };
        }

        if (!TryFloat(args: args, at: 0, value: out var speed)) {
            return new CommandResult(Output: "[editor.cam.speed: could not parse <unitsPerSecond> as a finite number]") {
                IsError = true,
            };
        }

        var (slot, error) = ResolveSlot(context: context, args: args, at: 1, verb: "editor.cam.speed");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (NotEditingError(slot: slot, verb: "editor.cam.speed") is { } notEditing) {
            return notEditing;
        }

        var applied = m_session.SetSpeed(slot: slot, unitsPerSecond: speed);

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[editor.cam.speed: seat {PlayerRoster.DisplayNumber(slot: slot)} {applied:0.##} u/s]"
        ));
    }

    private CommandResult PoseHandler(CommandContext context, string[] args) {
        // Shapes: 3 = <x y z>; 4 = +seat; 5 = +<yaw pitch>; 6 = +<yaw pitch> +seat.
        if (args.Length is (< 3 or > 6)) {
            return new CommandResult(Output: "[editor.cam.pose: expected <x> <y> <z> [<yawDeg> <pitchDeg>] [seat]]") {
                IsError = true,
            };
        }

        var hasAngles = (args.Length >= 5);
        var seatAt = (hasAngles ? 5 : 3);

        if (!TryFloat(args: args, at: 0, value: out var x) ||
            !TryFloat(args: args, at: 1, value: out var y) ||
            !TryFloat(args: args, at: 2, value: out var z)) {
            return new CommandResult(Output: "[editor.cam.pose: could not parse <x> <y> <z> as finite numbers]") {
                IsError = true,
            };
        }

        var yawDegrees = 0f;
        var pitchDegrees = 0f;

        if (hasAngles && (!TryFloat(args: args, at: 3, value: out yawDegrees) || !TryFloat(args: args, at: 4, value: out pitchDegrees))) {
            return new CommandResult(Output: "[editor.cam.pose: could not parse <yawDeg> <pitchDeg> as finite numbers]") {
                IsError = true,
            };
        }

        var (slot, error) = ResolveSlot(context: context, args: args, at: seatAt, verb: "editor.cam.pose");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (NotEditingError(slot: slot, verb: "editor.cam.pose") is { } notEditing) {
            return notEditing;
        }

        const float toRadians = (MathF.PI / 180f);

        m_session.SetPose(
            slot: slot,
            eye: new System.Numerics.Vector3(x: x, y: y, z: z),
            yawRadians: (yawDegrees * toRadians),
            pitchRadians: (pitchDegrees * toRadians)
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[editor.cam.pose: seat {PlayerRoster.DisplayNumber(slot: slot)} eye=({x:0.00}, {y:0.00}, {z:0.00}) yaw={yawDegrees:0}° pitch={pitchDegrees:0}°]"
        ));
    }

    private CommandResult MoveRouter(CommandContext context) {
        if (context.Parse is not null) {
            return new CommandResult(Output: "[editor.stick.move: a routed stick channel, not a typed verb — script the camera with editor.cam.pose or a drag with editor.drag]");
        }

        m_session.RouteMove(slot: context.Slot, move: context.Value.AsAxis2D);

        return CommandResult.None;
    }

    private CommandResult LookRouter(CommandContext context) {
        if (context.Parse is not null) {
            return new CommandResult(Output: "[editor.stick.look: a routed stick channel, not a typed verb — script the camera with editor.cam.pose]");
        }

        m_session.RouteLook(slot: context.Slot, look: context.Value.AsAxis2D);

        return CommandResult.None;
    }

    private CommandResult VerticalHandler(CommandContext context, bool ascend, string name) {
        if (context.Parse is not null) {
            return new CommandResult(Output: $"[{name}: a held control, not a typed verb — use editor.cam.pose to script the camera]");
        }

        m_session.SetVertical(slot: context.Slot, ascend: ascend, held: (context.Phase is CommandPhase.Started or CommandPhase.Active));

        return CommandResult.None;
    }

    private CommandResult ToggleHandler(CommandContext context) {
        if (context.Parse is not null) {
            return new CommandResult(Output: "[editor.camera: the bound camera toggle — type editor.fly [seat] or editor.orbit [seat] instead]");
        }

        var slot = context.Slot;

        if (!m_session.IsEditing(slot: slot)) {
            return CommandResult.None;
        }

        var mode = ((m_session.Mode(slot: slot) == EditorCameraMode.Fly) ? EditorCameraMode.Orbit : EditorCameraMode.Fly);

        m_session.SetMode(slot: slot, mode: mode);

        // Every discrete chord act narrates — the assist-layer doctrine's console line.
        return new CommandResult(Output: $"[editor.camera: seat {PlayerRoster.DisplayNumber(slot: slot)} camera {ModeWord(mode: mode)}]");
    }

    private CommandResult StepHandler(CommandContext context, bool up, string name) {
        if (context.Parse is not null) {
            return new CommandResult(Output: $"[{name}: the bound speed step — type editor.cam.speed <unitsPerSecond> [seat] instead]");
        }

        var slot = context.Slot;

        if (!m_session.IsEditing(slot: slot)) {
            return CommandResult.None;
        }

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[{name}: seat {PlayerRoster.DisplayNumber(slot: slot)} {m_session.StepSpeed(slot: slot, up: up):0.##} u/s]"
        ));
    }

    // The friendly guard the camera verbs share: they act only on an editing seat (the mode owns the camera).
    private CommandResult? NotEditingError(int slot, string verb) {
        if (m_session.IsEditing(slot: slot)) {
            return null;
        }

        return new CommandResult(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]") {
            IsError = true,
        };
    }

    private static string ModeWord(EditorCameraMode mode) => ((mode == EditorCameraMode.Orbit) ? "orbit" : "fly");

    // The shared FINITE parse boundary (UIE-2): NaN/infinity never enters camera, snap, or preview state — a
    // non-finite center would poison the SDF rebuild and a NaN pitch slides past ordinary range guards.
    internal static bool TryFloat(string[] args, int at, out float value) {
        return (float.TryParse(s: args[at], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out value) && float.IsFinite(f: value));
    }
}
