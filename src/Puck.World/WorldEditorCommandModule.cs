using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The editor's verbs. <c>world.grid</c> and <c>world.snap</c> move one value of the acting seat's
/// <see cref="WorldEditorSeats"/> state over the document's <c>editor</c> section, and each echoes the seat's whole
/// grid or snapping with no argument and after every change; they run inline and move presentation state only.
/// <c>world.place</c>, <c>world.nudge</c> and <c>world.turn</c> put down, move and turn a placement by whole grid and
/// angle steps, snapping through <see cref="Puck.World.Authoring.GridSnap"/>, and each submits one placements upsert
/// composed by the section upsert <c>world.row.set</c> composes with (<see cref="WorldRowCommandModule.TryComposeRoutedSet"/>),
/// under the issuing principal and a row-edit window guard, so the grant check, revalidation, tick-boundary apply,
/// journal and <c>world.undo</c> govern them as they govern every row edit.
/// <para>Editing follows the seat. Every verb acts on the world the acting seat is presented in, by its authority route
/// (<see cref="WorldSeatAuthorityRouter"/>): a seat that crossed into another world reads that world's document and
/// submits through that world's own link, whose admission names its verdict, and never touches the world it left. A
/// seat's selection (its current placement, its reference, the height its plane followed) belongs to the world it was
/// made in and does not cross with it. A slot with no route edits the world the console addresses. Every verb here is
/// bindable; the engine's build page binds them.</para></summary>
/// <param name="seats">Each seat's editor state.</param>
/// <param name="authority">Resolves the world a command addresses when its slot has no route.</param>
/// <param name="link">The link placement edits in the console's world are submitted over.</param>
/// <param name="echoes">Publishes each submitted edit's deferred verdict.</param>
/// <param name="stepGuard">The per-tick-window row claim every row-editing door shares.</param>
/// <param name="seatRouter">Each seat's authority route, so a seat's edits follow it into the world it is presented in;
/// <see langword="null"/> on a host whose seats never leave the console's world.</param>
public sealed partial class WorldEditorCommandModule(WorldEditorSeats seats, IWorldConsoleAuthority authority, IServerLink link, WorldDeferredVerbEchoes echoes, WorldRowStepWindowGuard stepGuard, WorldSeatAuthorityRouter? seatRouter = null) : ICommandModule {
    /// <summary>The verb that moves a seat's grid.</summary>
    public const string GridCommand = "world.grid";
    /// <summary>The finest grid pitch <c>world.grid pitch down</c> reaches, in world units.</summary>
    public const float MinStepPitch = 0.0625f;
    /// <summary>The coarsest grid pitch <c>world.grid pitch up</c> reaches, in world units.</summary>
    public const float MaxStepPitch = 64f;
    /// <summary>The verb that moves a seat's snapping.</summary>
    public const string SnapCommand = "world.snap";

    // The world a verb edits: its instance name as the seat's route names it, its document as this host sees it and the
    // read of whatever document it delivers next, the link an edit is submitted through, the input window an edit
    // targets, and the guard its console's other row-editing doors claim rows in, which only the console's own world has:
    // a routed world's rows are edited here by this module alone, one edit in flight per placement.
    private readonly record struct EditWorld(string Name, WorldDefinition Definition, Func<WorldDefinition> Delivered, IServerLink Link, ulong Window, WorldRowStepWindowGuard? Guard);

    private static string Format(float value) => value.ToString(
        format: "0.####",
        provider: CultureInfo.InvariantCulture
    );
    private static string Format(Vector3 value) => $"{Format(value: value.X)},{Format(value: value.Y)},{Format(value: value.Z)}";
    private static string ModeName(WorldEditorGridMode mode) => mode switch {
        WorldEditorGridMode.Follow => "follow",
        WorldEditorGridMode.Plane => "plane",
        _ => "surface",
    };
    private static WorldEditorGridMode NextMode(WorldEditorGridMode mode) => mode switch {
        WorldEditorGridMode.Surface => WorldEditorGridMode.Follow,
        WorldEditorGridMode.Follow => WorldEditorGridMode.Plane,
        _ => WorldEditorGridMode.Surface,
    };
    private static bool TryOnOff(ReadOnlySpan<char> token, out bool value) {
        value = token.SequenceEqual(other: "on");

        return (value || token.SequenceEqual(other: "off"));
    }
    private CommandResult EchoGrid(int slot, WorldDefinition definition) {
        var grid = seats.GridOf(document: definition.Editor, slot: slot);

        return new CommandResult(Output: CommandEcho.Open(verb: GridCommand)
            .Field(key: "seat", value: PlayerRoster.DisplayNumber(slot: slot))
            .Field(key: "visible", value: grid.Visible)
            .Field(key: "mode", value: ModeName(mode: grid.Mode))
            .Field(key: "pitch", value: Format(value: grid.ResolvedPitch))
            .Field(key: "plane", value: Format(value: grid.PlaneY))
            .Close());
    }
    private CommandResult EchoSnap(int slot, EditWorld world) {
        var snap = seats.SnapOf(document: world.Definition.Editor, slot: slot);

        return new CommandResult(Output: CommandEcho.Open(verb: SnapCommand)
            .Field(key: "seat", value: PlayerRoster.DisplayNumber(slot: slot))
            .Field(key: "enabled", value: snap.Enabled)
            .Field(key: "angle", value: Format(value: snap.AngleStepDegrees))
            .Field(key: "surface", value: snap.Surface)
            .Field(key: "reference", value: (seats.ReferenceOf(slot: slot, world: world.Name) ?? "none"))
            .Close());
    }
    // The world a verb edits: the one the acting seat is presented in, by its authority route, so a seat that crossed
    // into another world edits that world through that world's own link and never the world it left; else, for a slot
    // with no route, the world the console addresses.
    private bool TryEditWorld(CommandContext context, string verb, out EditWorld world, out CommandResult refusal) {
        world = default;
        refusal = CommandResult.None;

        if (!authority.TryResolve(context: context, instance: out var instance, refusal: out var reason)) {
            refusal = CommandResult.Error(output: $"[{verb}: refused ({reason})]");

            return false;
        }

        if (
            (seatRouter?.TryRoute(slot: context.Slot) is { } route) &&
            !string.Equals(a: route.Endpoint.Identity, b: instance.Name, comparisonType: StringComparison.Ordinal)
        ) {
            var endpoint = route.Endpoint;

            world = new EditWorld(
                Definition: endpoint.Definition,
                Delivered: () => endpoint.Definition,
                Guard: null,
                Link: endpoint.Submissions,
                Name: endpoint.Identity,
                Window: endpoint.NextInputTick
            );

            return true;
        }

        world = new EditWorld(
            Definition: instance.Server.Definition,
            Delivered: () => instance.Server.Definition,
            Guard: stepGuard,
            Link: link,
            Name: instance.Name,
            Window: instance.Server.NextInputTick
        );

        return true;
    }
    private CommandResult GridHandler(CommandContext context, WireArgs args) {
        if (!TryEditWorld(context: context, refusal: out var refusal, verb: GridCommand, world: out var world)) {
            return refusal;
        }

        var slot = context.Slot;
        var definition = world.Definition;

        if (args.Count == 0) {
            return EchoGrid(definition: definition, slot: slot);
        }

        var grid = seats.GridOf(document: definition.Editor, slot: slot);

        if ((args.Count == 1) && TryOnOff(token: args[0], value: out var visible)) {
            seats.SetGridVisible(slot: slot, visible: visible);
        } else if ((args.Count == 1) && args.Is(index: 0, value: "toggle")) {
            seats.SetGridVisible(slot: slot, visible: !grid.Visible);
        } else if ((args.Count == 1) && args.Is(index: 0, value: "next")) {
            seats.SetGridMode(mode: NextMode(mode: grid.Mode), slot: slot);
        } else if ((args.Count == 1) && args.Is(index: 0, value: "follow")) {
            seats.SetGridMode(mode: WorldEditorGridMode.Follow, slot: slot);
        } else if ((args.Count == 1) && args.Is(index: 0, value: "surface")) {
            seats.SetGridMode(mode: WorldEditorGridMode.Surface, slot: slot);
        } else if ((args.Count == 2) && args.Is(index: 0, value: "plane") && args.TryFloat(index: 1, value: out var planeY) && float.IsFinite(f: planeY)) {
            seats.SetGridMode(mode: WorldEditorGridMode.Plane, planeY: planeY, slot: slot);
        } else if ((args.Count == 2) && args.Is(index: 0, value: "pitch") && (args.Is(index: 1, value: "up") || args.Is(index: 1, value: "down"))) {
            var stepped = (grid.ResolvedPitch * (args.Is(index: 1, value: "up") ? 2f : 0.5f));

            if ((MathF.Min(x: stepped.X, y: MathF.Min(x: stepped.Y, y: stepped.Z)) < MinStepPitch) || (MathF.Max(x: stepped.X, y: MathF.Max(x: stepped.Y, y: stepped.Z)) > MaxStepPitch)) {
                return CommandResult.Error(output: $"[{GridCommand}: a stepped pitch stays within {Format(value: MinStepPitch)}..{Format(value: MaxStepPitch)}]");
            }

            seats.SetGridPitch(pitch: stepped, slot: slot);
        } else if ((args.Count is 2 or 3) && args.Is(index: 0, value: "pitch") && args.TryFloat(index: 1, value: out var x)) {
            var z = x;

            if ((args.Count == 3) && !args.TryFloat(index: 2, value: out z)) {
                return CommandResult.Usage(form: "pitch <x> [<z>]", verb: GridCommand);
            }

            var pitch = ((args.Count == 3)
                ? new Vector3(x: x, y: grid.ResolvedPitch.Y, z: z)
                : new Vector3(value: x));

            if (!(float.IsFinite(f: pitch.X) && float.IsFinite(f: pitch.Z) && (pitch.X > 0f) && (pitch.Z > 0f))) {
                return CommandResult.Error(output: $"[{GridCommand}: a pitch is finite and positive]");
            }

            seats.SetGridPitch(pitch: pitch, slot: slot);
        } else {
            return CommandResult.Usage(form: "on|off|toggle|next|pitch <x> [<z>]|pitch up|down|plane <y>|follow|surface", verb: GridCommand);
        }

        return EchoGrid(definition: definition, slot: slot);
    }
    private CommandResult SnapHandler(CommandContext context, WireArgs args) {
        if (!TryEditWorld(context: context, refusal: out var refusal, verb: SnapCommand, world: out var world)) {
            return refusal;
        }

        var slot = context.Slot;
        var definition = world.Definition;

        if (args.Count == 0) {
            return EchoSnap(slot: slot, world: world);
        }

        var snap = seats.SnapOf(document: definition.Editor, slot: slot);

        if ((args.Count == 1) && TryOnOff(token: args[0], value: out var enabled)) {
            seats.SetSnapEnabled(enabled: enabled, slot: slot);
        } else if ((args.Count == 1) && args.Is(index: 0, value: "toggle")) {
            seats.SetSnapEnabled(enabled: !snap.Enabled, slot: slot);
        } else if ((args.Count == 1) && args.Is(index: 0, value: "clear")) {
            seats.SetReference(placement: null, slot: slot, world: world.Name);
        } else if ((args.Count == 2) && args.Is(index: 0, value: "angle") && args.TryFloat(index: 1, value: out var degrees)) {
            if (!((degrees > 0f) && (degrees <= 180f))) {
                return CommandResult.Error(output: $"[{SnapCommand}: an angle step is within (0, 180] degrees]");
            }

            seats.SetAngleStep(degrees: degrees, slot: slot);
        } else if ((args.Count == 2) && args.Is(index: 0, value: "surface") && TryOnOff(token: args[1], value: out var surface)) {
            seats.SetSurfaceSnap(slot: slot, surface: surface);
        } else if ((args.Count is 1 or 2) && args.Is(index: 0, value: "reference")) {
            var id = ((args.Count == 2) ? args[1].ToString() : seats.CurrentOf(slot: slot, world: world.Name));

            if (id is null) {
                return CommandResult.Error(output: $"[{SnapCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no current placement; name one: reference <placement>]");
            }

            if (WorldDefinitionRows.FindPlacement(id: id, placements: definition.Placements) is null) {
                return CommandResult.Error(output: $"[{SnapCommand}: no placement '{id}' in '{world.Name}']");
            }

            seats.SetReference(placement: id, slot: slot, world: world.Name);
        } else {
            return CommandResult.Usage(form: "on|off|toggle|angle <deg>|surface on|off|reference [<placement>]|clear", verb: SnapCommand);
        }

        return EchoSnap(slot: slot, world: world);
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: GridCommand,
            description: "Moves the acting seat's editor grid, which its view draws while it builds: world.grid on|off|toggle; next (cycles surface, follow, plane); pitch <x> [<z>] (one value sets every axis); pitch up|down (doubles or halves it); plane <y> (a fixed working plane at that height); follow (the working plane follows the surface under the pointer); surface (the grid lies on every surface). With no argument, and after every change, echoes the seat's whole grid. A value no verb has moved reads through to the document's editor section, and world.save folds seat 1's moved values back into it.",
            handler: GridHandler,
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: SnapCommand,
            description: "Moves the acting seat's editor snapping: world.snap on|off|toggle; angle <deg> (the turn step, within (0, 180]); surface on|off (whether placing rests on the surface it lands on); reference [<placement>] (aligns to a placement's own lattice, drawn as the object grid; bare, the seat's current placement); clear (drops the reference). With no argument, and after every change, echoes the seat's whole snapping. world.save folds seat 1's moved values back into the editor section.",
            handler: SnapHandler,
            routing: CommandRouting.Immediate
        );

        foreach (var verb in PlacementVerbs()) {
            yield return verb;
        }
    }
}
