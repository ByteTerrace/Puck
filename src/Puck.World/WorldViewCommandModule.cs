using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using static Puck.World.WorldCommandDefinition;

namespace Puck.World;

/// <summary>
/// The window-composition verb surface — the LIVE session override <c>view.override</c> (composition authority that
/// changes what every seat sees) plus the pipe-assertable <c>world.view.state</c>, <c>world.view.orbit</c>, and
/// <c>world.view.pointer</c> reads.
/// The durable views-section rows are authored through the general <see cref="WorldRowCommandModule"/> —
/// <c>world.row.set views.seatRig &lt;json&gt;</c> for the keyless row, and
/// <c>world.row.set</c>/<c>world.row.remove views.layouts ...</c> for the keyed one. Control FEEL is not a views row
/// at all: it is per-seat, authored at <c>world.row.set playerDefaults.seatLook</c>. A SEPARATE module from
/// <see cref="WorldMutationCommandModule"/> to keep every class under its analyzer ceilings. <c>view.override</c>
/// routes <see cref="CommandRouting.Simulation"/> so the stdin barrier serializes a following
/// <c>world.view.state</c> read-after-write; <c>world.view.state</c> and <c>world.view.orbit</c> are Immediate reads
/// of the live composer and the local seat's orbit, respectively.
/// </summary>
/// <remarks><c>view.override</c> reaches <see cref="WorldCapability.Control"/> over
/// <see cref="GrantSubject.Composition"/>, but Console can never be denied there by any reachable sequence: the
/// <c>world.grant</c>/<c>world.revoke</c> grammar has no token for the composition subject, so the seeded row can be
/// listed by <c>world.grants</c> but never revoked — and Console additionally holds
/// <see cref="WorldCapability.Control"/> over <see cref="GrantSubject.All"/>, which the check short-circuits on. Treat
/// the composition check as real for a principal that could hold it and inert for Console until the grammar can name
/// the subject.
/// <para><c>world.view.state</c> and <c>world.view.orbit</c> carry no principal at all — each is a direct read of
/// live presentation state.</para></remarks>
internal sealed class WorldViewCommandModule(IServerLink link, WorldViewComposer composer, WorldCameraOrbit orbit, WorldSeatFeel seatFeel, WorldCursorFeed cursorFeed) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Simulation(
            name: "view.override",
            description: "LIVE composition override, keyed by which slot kind it forces: view.override camera|layout <name|auto>. 'layout' forces the active window layout for every seat; 'camera' resolves every camera-bearing slot to one camera for every seat (the twin of a layout slot's own camera). 'auto' (or '-') clears the override back to the composer's own selection. Gated Control over composition; a denial prints loudly and changes nothing.",
            handler: (context, args) => {
                if (args.Count != 2) {
                    return Usage(verb: "view.override", form: "camera|layout <name|auto>");
                }

                var name = ClearOrName(token: args[1].ToString());
                WorldComposition? composition = args[0].ToString() switch {
                    "layout" => new WorldComposition.SetActiveLayout(Name: name),
                    "camera" => new WorldComposition.SelectCamera(Name: name),
                    _ => null,
                };

                if (composition is null) {
                    return CommandResult.Error(output: $"[view.override: unknown target '{args[0].ToString()}' — camera|layout]");
                }

                link.SubmitComposition(composition: composition, principal: context.ActingPrincipal());

                return CommandResult.None;
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.state",
            description: "Echoes the live window composition: world.view.state — the active layout name, selection reason (override|authored|builtin), transition progress, and each slot's rect + occupant (seat<order> | cam:<name>). A query (always echoes) — the pipe-assertable composition read.",
            handler: (context, args) => new CommandResult(Output: DescribeState()),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.orbit",
            description: "Echoes a local seat's live mouse-look orbit: world.view.orbit [player] — THAT SEAT's control feel in force (arming, worldAxes, sensitivities, invert flags, pitch clamp) and its live drag yaw/pitch in degrees. Feel is PER SEAT: a seat carrying a profile reads that profile's own playerDefaults.seatLook, and a seat with none reads the world document's, so two seats can answer differently in the same session and a world.row.set playerDefaults.seatLook moves only the seats sitting at the world's floor. The optional trailing player index is 1..4 (default 1) — the orbit ANGLES WorldCameraOrbit carries are per local seat too; only the seat WorldPointerSink currently resolves the mouse onto (the keyboard's own seat — see its remarks) ever carries a nonzero angle. A query (always echoes) — the pipe-assertable seat-look read.",
            handler: (context, args) => {
                if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: 1, value: out var player)) {
                    return CommandResult.Error(output: $"[world.view.orbit: player index must be an integer 1..{PlayerRoster.MaxSlots}]");
                }

                return new CommandResult(Output: DescribeOrbit(slot: PlayerRoster.SlotFromDisplay(number: player)));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.pointer",
            description: "Echoes the drawn cursor's last composed frame: world.view.pointer — the seat the pointer rides (1-based; the keyboard's seat, the one WorldPointerSink resolves the mouse onto), the cursor position in CLIENT pixels (position=), the same position mapped into the fixed FRAME extent the overlay draws in (frame= — the two diverge when the OS window is resized; WorldCursorFeed.Decide owns the mapping) and normalized within the seat's viewport (local=), the viewport rect, the visibility verdict (visible | no-position | no-view | outside-viewport | orbit-drag — WorldCursorFeed's one visibility rule), the held pointer buttons (buttons=, L/R/M in that order or '-' — the live store state, so an injected press is assertable before anything acts on it), the live hover target (hover=none, or the hovered panel/world row's label), and the seat's SYSTEM-RELEASE generation (syscount= — WorldPointer.SystemReleaseCount: how many times the store has force-cleared this seat's held buttons without a genuine release event; an edge-deriving consumer compares this against the value it captured at press time to tell a synthetic release from a real one). A query (always echoes) — the pipe-assertable pointer read, the world.view.orbit sibling: live per-seat presentation state nothing else can echo.",
            handler: (context, args) => new CommandResult(Output: DescribePointer()),
            routing: CommandRouting.Immediate
        );
    }

    private string DescribePointer() {
        var status = cursorFeed.Status;
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
            ));

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.view.pointer: player={PlayerRoster.DisplayNumber(slot: status.Slot)} position={status.Position.X:0.#},{status.Position.Y:0.#}{mapped} viewport={region.X:0.##},{region.Y:0.##},{region.Width:0.##},{region.Height:0.##} visible={status.Visible.ToString().ToLowerInvariant()} reason={status.Reason} buttons={status.Buttons} hover={((status.Hover.Length > 0) ? status.Hover : "none")} syscount={status.SystemReleaseCount}]"
        );
    }
    private string DescribeOrbit(int slot) {
        const float RadiansToDegrees = (180f / MathF.PI);
        // THIS seat's feel, not a document-wide one: the echo prints player=N, so it must read the policy that seat
        // actually drags with — its own profile's, or the world's until one is delivered.
        var seatLook = seatFeel.Look(slot: slot);
        var builder = new StringBuilder(value: "[world.view.orbit: ");

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"player={PlayerRoster.DisplayNumber(slot: slot)} arming={seatLook.Arming.ToString().ToLowerInvariant()} worldAxes={seatLook.WorldAxes.ToString().ToLowerInvariant()} yawSensitivity={seatLook.YawSensitivity.ToString(format: "0.#####", provider: CultureInfo.InvariantCulture)} pitchSensitivity={seatLook.PitchSensitivity.ToString(format: "0.#####", provider: CultureInfo.InvariantCulture)} invertYaw={seatLook.InvertYaw.ToString().ToLowerInvariant()} invertPitch={seatLook.InvertPitch.ToString().ToLowerInvariant()} minPitch={(seatLook.MinPitch * RadiansToDegrees).ToString(format: "0.##", provider: CultureInfo.InvariantCulture)} maxPitch={(seatLook.MaxPitch * RadiansToDegrees).ToString(format: "0.##", provider: CultureInfo.InvariantCulture)}");
        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" yaw={(orbit.Yaw(slot: slot) * RadiansToDegrees).ToString(format: "0.##", provider: CultureInfo.InvariantCulture)} pitch={(orbit.Pitch(slot: slot) * RadiansToDegrees).ToString(format: "0.##", provider: CultureInfo.InvariantCulture)}");

        return builder.Append(value: ']').ToString();
    }
    private string DescribeState() {
        var builder = new StringBuilder(value: "[world.view.state: ");

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"active={composer.ActiveLayoutName} selection={composer.SelectionReason} transition={composer.TransitionProgress.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)} slots={composer.Slots.Count}");

        for (var index = 0; (index < composer.Slots.Count); index++) {
            var slot = composer.Slots[index];
            var occupant = ((slot.Camera is { } camera) ? $"cam:{camera}" : $"seat{slot.SeatOrder}");

            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" slot{index}={slot.Region.X.ToString(format: "0.##", provider: CultureInfo.InvariantCulture)},{slot.Region.Y.ToString(format: "0.##", provider: CultureInfo.InvariantCulture)},{slot.Region.Width.ToString(format: "0.##", provider: CultureInfo.InvariantCulture)},{slot.Region.Height.ToString(format: "0.##", provider: CultureInfo.InvariantCulture)}:{occupant}");
        }

        return builder.Append(value: ']').ToString();
    }

    // The plan-wide clear-to-absent tokens for a live override: 'auto' (and '-') clear it back to the composer's own
    // selection; any other token is the forced name.
    private static string? ClearOrName(string token) =>
        ((string.Equals(a: token, b: "auto", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: token, b: "-", comparisonType: StringComparison.Ordinal)) ? null : token);
    private static CommandResult Usage(string verb, string form) {
        return CommandResult.Error(output: $"[{verb}: expected {form}]");
    }
}
