using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The console's touchscreen: <c>world.view.touch &lt;id&gt; down|move|up|cancel &lt;x&gt; &lt;y&gt;</c> lays a synthetic
/// finger on the acting seat's view at a client-pixel point, through the doors a real finger takes. Its transition
/// enters the contact store (<see cref="WorldContacts"/>), which the snapshot capture casts through the seat's view into
/// the seat's next submission, and its touch and lift reach the router as <c>touch.contact&lt;id&gt;</c>, so bindings
/// and input patterns (a tap, a hold, a double tap) read it as they read the glass. With no arguments it echoes each
/// seat's live contacts.
/// </summary>
/// <remarks>Core-registered so every boot shape knows the verb; the contact store is presentation-only, so a boot with
/// no GPU presentation refuses it by name, and <c>source.contact</c> drives the authority's contact path there with a
/// world ray instead.</remarks>
/// <param name="router">Resolves the host's input router.</param>
/// <param name="contacts">The contact store, or <see langword="null"/> in a boot with no GPU presentation.</param>
internal sealed class WorldTouchCommandModule(Func<InputRouter> router, WorldContacts? contacts = null) : ICommandModule {
    private const string Verb = "world.view.touch";
    private static readonly CommandForms Forms = CommandForms.Of(verb: Verb, "[<id> down|move|up|cancel <client-x> <client-y>]");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: Verb,
            forms: Forms,
            description: $"Lays a synthetic finger on the acting seat's view: {Forms.Spelling} — the contact's number (1 to {SurfaceContact.MaximumContacts}, stable while it is down), its phase, and its point in CLIENT pixels, as a touchscreen reports one. The transition enters the contact store a real finger writes, the capture casts it through the seat's view into the seat's next submission (a forEach: $contacts:<screen> rule reads it when its ray lands on a Simulation screen), and its touch and lift reach the router as touch.contact<id>, so bindings and input patterns read it too. A touch outside the seat's view is not sent to the world; one that touched inside keeps sending until it lifts. With no arguments it echoes each seat's live contacts (device, number, client position). Refused by name in a boot with no GPU presentation; source.contact drives the same authority path there with a world ray.",
            valueKind: CommandValueKind.Digital,
            handler: (context, args) => Touch(args: args, context: context),
            routing: CommandRouting.Immediate
        );
    }

    private CommandResult Touch(CommandContext context, WireArgs args) {
        if (contacts is null) {
            return CommandResult.Error(output: $"[{Verb}: requires a GPU presentation; source.contact queues a contact as a world ray]");
        }
        if (args.Count == 0) {
            return new CommandResult(Output: Describe(store: contacts));
        }

        ContactPhase? phase = ((args.Count == 4) ? args[1].ToString() : null) switch {
            "down" => ContactPhase.Down,
            "move" => ContactPhase.Move,
            "up" => ContactPhase.Up,
            "cancel" => ContactPhase.Cancel,
            _ => null,
        };

        if (
            (phase is null) ||
            !byte.TryParse(s: args[0].ToString(), style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var id) ||
            (id is < 1 or > SurfaceContact.MaximumContacts) ||
            !float.TryParse(s: args[2].ToString(), provider: CultureInfo.InvariantCulture, result: out var x) ||
            !float.TryParse(s: args[3].ToString(), provider: CultureInfo.InvariantCulture, result: out var y) ||
            !float.IsFinite(f: x) ||
            !float.IsFinite(f: y)
        ) {
            return Forms.Refuse(args: in args);
        }

        var slot = context.Slot;

        if (!contacts.Record(
            device: ContactDevice.Touch,
            deviceId: default,
            id: id,
            phase: phase.Value,
            position: new Vector2(x: x, y: y),
            pressure: float.NaN,
            slot: slot,
            timestamp: Stopwatch.GetTimestamp()
        )) {
            return CommandResult.Error(output: $"[{Verb}: p{PlayerRoster.DisplayNumber(slot: slot)} contact {id} {((phase == ContactPhase.Down) ? "is already down" : "is not down")}, or {WorldContacts.Capacity} transitions already wait for a frame to cast them]");
        }

        // A touch and a lift are the binding sources' edges, as the window pump maps a real contact's; a move is only a
        // point and binds nothing.
        if (phase != ContactPhase.Move) {
            var source = InputSources.Touch.Contact(number: id);

            router().Capture(signal: ((phase == ContactPhase.Down)
                ? InputSignal.Press(source: source)
                : InputSignal.Release(source: source)) with {
                CaptureTick = InputRouter.EarliestCaptureTick,
                Slot = slot,
            });
        }

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[{Verb}: p{PlayerRoster.DisplayNumber(slot: slot)} contact {id} {phase.Value.ToString().ToLowerInvariant()} at {x},{y}]"));
    }
    private static string Describe(WorldContacts store) {
        var builder = new StringBuilder(value: $"[{Verb}:");
        var any = false;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            foreach (var contact in store.Live(slot: slot)) {
                any = true;
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" p{PlayerRoster.DisplayNumber(slot: slot)} {contact.Device.ToString().ToLowerInvariant()}{contact.Id} at {contact.Position.X:0.#},{contact.Position.Y:0.#}");
            }
        }

        return builder.Append(value: (any ? "]" : " no contact down]")).ToString();
    }
}
