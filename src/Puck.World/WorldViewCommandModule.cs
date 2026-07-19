using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The window-composition verb surface — the durable views-section mutations (<c>world.view.rig</c> seat-rig RMW,
/// <c>world.view.layout.set</c>/<c>.remove</c>) and the LIVE session overrides (<c>view.layout</c>/<c>view.camera</c>,
/// composition authority that changes what every seat sees), plus the pipe-assertable <c>world.view.state</c> read. A
/// SEPARATE module from <see cref="WorldMutationCommandModule"/> to keep every class under its analyzer ceilings. The
/// mutation verbs route <see cref="CommandRouting.Simulation"/> (the server prints the loud accept/reject when the
/// buffered edit applies); the override verbs also route Simulation so the stdin barrier serializes a following
/// <c>world.view.state</c> read-after-write; <c>world.view.state</c> is an Immediate read of the live composer.
/// </summary>
internal sealed class WorldViewCommandModule(WorldServer server, IServerLink link, WorldViewComposer composer) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Simulation(
            name: "world.view.rig",
            description: "Read-modify-writes the seat chase rig (the framing every seat wakes on) from one inline-JSON WorldRig into a whole views-section upsert: world.view.rig <rig-json> ($type chase|firstPerson|orbit|lookAt|dolly). Applies LIVE: every seat's camera re-frames the next frame.",
            handler: (context, args) => {
                var raw = RawArgument(context: context, args: args);

                if (!TryParseJson(json: raw, info: WorldJsonContext.Default.WorldRig, value: out var rig, error: out var error)) {
                    return new CommandResult(Output: $"[world.view.rig: {error}]") { IsError = true };
                }

                var views = (server.Definition.Views ?? WorldViewDefaults.Default);

                return Submit(mutation: new WorldMutation.SetViewDefaults(Principal: WorldPrincipal.Console, Views: (views with { SeatRig = rig })));
            }
        );
        yield return Simulation(
            name: "world.view.layout.set",
            description: "Upserts one named window layout (whole-row, keyed by name) from one inline-JSON WorldViewLayout: world.view.layout.set <layout-json>. A slot with a null camera shows the seat that owns it; seatCount 0 is the catch-all for any joined-seat count.",
            handler: (context, args) => {
                var raw = RawArgument(context: context, args: args);

                if (!TryParseJson(json: raw, info: WorldJsonContext.Default.WorldViewLayout, value: out var layout, error: out var error)) {
                    return new CommandResult(Output: $"[world.view.layout.set: {error}]") { IsError = true };
                }

                return Submit(mutation: new WorldMutation.UpsertViewLayout(Principal: WorldPrincipal.Console, Layout: layout));
            }
        );
        yield return Simulation(
            name: "world.view.layout.remove",
            description: "Removes a named window layout: world.view.layout.remove <name>. Always allowed — the composer falls back to the authored/built-in selection.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.view.layout.remove", form: "<name>");
                }

                return Submit(mutation: new WorldMutation.RemoveViewLayout(Principal: WorldPrincipal.Console, Name: args[0]));
            }
        );
        yield return Simulation(
            name: "view.layout",
            description: "LIVE composition override — forces the active window layout for every seat: view.layout <name|auto>. auto returns to the composer's own selection. Gated Control over composition; a denial prints loudly and changes nothing.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "view.layout", form: "<name|auto>");
                }

                link.SubmitComposition(composition: new WorldComposition.SetActiveLayout(Name: ClearOrName(token: args[0])), principal: WorldPrincipal.Console);

                return CommandResult.None;
            }
        );
        yield return Simulation(
            name: "view.camera",
            description: "LIVE composition override — resolves every camera-bearing slot to one camera for every seat: view.camera <name|auto>. auto clears the override. The twin of a layout slot's own camera and Arc 9's milestone camera cut. Gated Control over composition.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "view.camera", form: "<name|auto>");
                }

                link.SubmitComposition(composition: new WorldComposition.SelectCamera(Name: ClearOrName(token: args[0])), principal: WorldPrincipal.Console);

                return CommandResult.None;
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.view.state",
            description: "Echoes the live window composition: world.view.state — the active layout name, selection reason (override|authored|builtin), transition progress, and each slot's rect + occupant (seat<order> | cam:<name>). A query (always echoes) — the pipe-assertable composition read.",
            handler: (context, args) => new CommandResult(Output: DescribeState()),
            routing: CommandRouting.Immediate
        );
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
        (string.Equals(a: token, b: "auto", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: token, b: "-", comparisonType: StringComparison.Ordinal)) ? null : token;

    private static CommandDefinition Simulation(string name, string description, Func<CommandContext, string[], CommandResult> handler) {
        return CommandDefinition.WithTrailingArgs(name: name, description: description, handler: handler, routing: CommandRouting.Simulation);
    }

    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }

    private static CommandResult Usage(string verb, string form) {
        return new CommandResult(Output: $"[{verb}: expected {form}]") { IsError = true };
    }

    // The raw argument text after the verb token — reconstructed from the submitted line so inline-JSON quotes survive
    // the console tokenizer.
    private static string RawArgument(CommandContext context, string[] args) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();
            var separator = span.IndexOfAny(value0: ' ', value1: '\t');

            return ((separator < 0) ? string.Empty : span[(separator + 1)..].Trim().ToString());
        }

        return string.Join(separator: ' ', values: args);
    }

    private static bool TryParseJson<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, out T value, out string error) {
        value = default!;

        if (string.IsNullOrWhiteSpace(value: json)) {
            error = "expected a compact inline-JSON argument";

            return false;
        }

        try {
            if (JsonSerializer.Deserialize(json: json, jsonTypeInfo: info) is not { } parsed) {
                error = "the JSON parsed to null";

                return false;
            }

            value = parsed;
            error = string.Empty;

            return true;
        } catch (JsonException exception) {
            error = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }
}
