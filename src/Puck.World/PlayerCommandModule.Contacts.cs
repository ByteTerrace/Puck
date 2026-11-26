using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private const string SourceContactCommand = "source.contact";
    private static readonly CommandForms SourceContactForms = CommandForms.Of(verb: SourceContactCommand, "<id> down|move|up|cancel <ox> <oy> <oz> <dx> <dy> <dz> [touch|pen|mouse]");

    // source.contact: one pointing-contact event, already a world ray, queued on the seat for its next submission, the
    // event a windowed host's capture casts from a finger, a pen tip or the mouse. It needs no view, so a headless World
    // and a scenario drive the authority's contact path with it.
    private CommandDefinition SourceContactVerb() => CommandDefinition.WithWireArgs(
        audience: CommandAudience.Anyone,
        bindability: CommandBindability.Unbindable,
        name: SourceContactCommand,
        forms: SourceContactForms,
        description: $"{SourceContactForms.Spelling} — Queues one pointing-contact event on the seat for its next submission: the contact's number (0 for the mouse, 1 to {SurfaceContact.MaximumContacts} for a finger or pen tip), its phase, and the world-space ray its point casts (origin, then a direction that need not be unit length), with the device touch unless named. The authority maps the ray through each Simulation screen a forEach: $contacts:<screen> rule sweeps, so a screen's rules read it as they read a real finger's; a windowed host casts the same event from the OS through the seat's view (world.view.touch drives that path from client pixels). Echoes the queued event.",
        valueKind: CommandValueKind.Digital,
        handler: (context, args) => SourceContact(args: args, context: context)
    );
    private CommandResult SourceContact(CommandContext context, WireArgs args) {
        if (args.Count is not (8 or 9)) {
            return SourceContactForms.Refuse(args: in args);
        }

        Span<float> numbers = stackalloc float[6];
        ContactPhase? phase = args[1].ToString() switch {
            "down" => ContactPhase.Down,
            "move" => ContactPhase.Move,
            "up" => ContactPhase.Up,
            "cancel" => ContactPhase.Cancel,
            _ => null,
        };
        ContactDevice? device = ((args.Count == 9) ? args[8].ToString() : "touch") switch {
            "touch" => ContactDevice.Touch,
            "pen" => ContactDevice.Pen,
            "mouse" => ContactDevice.Mouse,
            _ => null,
        };

        for (var index = 0; (index < numbers.Length); index++) {
            if (!float.TryParse(s: args[(index + 2)].ToString(), provider: CultureInfo.InvariantCulture, result: out numbers[index]) || !float.IsFinite(f: numbers[index])) {
                return SourceContactForms.Refuse(args: in args);
            }
        }

        if (
            !byte.TryParse(s: args[0].ToString(), style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var id) ||
            (phase is null) ||
            (device is null)
        ) {
            return SourceContactForms.Refuse(args: in args);
        }

        var contact = new SurfaceContact(
            Device: device.Value,
            Id: id,
            Phase: phase.Value,
            Ray: new SourceRay(
                Direction: CommandValueQuantization.QuantizeAxis3D(value: new Vector3(x: numbers[3], y: numbers[4], z: numbers[5])),
                Origin: CommandValueQuantization.QuantizeAxis3D(value: new Vector3(x: numbers[0], y: numbers[1], z: numbers[2]))
            )
        );

        if (!contact.IsWellFormed()) {
            return CommandResult.Error(output: $"[{SourceContactCommand}: contact {id} is outside its device's range (0 for the mouse, 1 to {SurfaceContact.MaximumContacts} for touch and pen)]");
        }
        if (m_roster.Seat(slot: context.Slot) is not { } seat) {
            return CommandResult.Error(output: $"[{SourceContactCommand}: p{PlayerRoster.DisplayNumber(slot: context.Slot)} has no seat]");
        }
        if (!seat.AddContact(contact: contact)) {
            return CommandResult.Error(output: $"[{SourceContactCommand}: p{PlayerRoster.DisplayNumber(slot: context.Slot)} already holds {SurfaceContact.MaximumPerTick} contact events for its next submission]");
        }

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[{SourceContactCommand}: p{PlayerRoster.DisplayNumber(slot: context.Slot)} {contact.Device.ToString().ToLowerInvariant()} {contact.Id} {contact.Phase.ToString().ToLowerInvariant()} queued]"));
    }
}
