using System.Globalization;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The capability-grant console surface — the dev reflection of the principal/grant model: <c>world.grant</c> and
/// <c>world.revoke</c> mutate the server's ONE grant table over the wire, and <c>world.grants</c> echoes it. Grant
/// changes route <see cref="CommandRouting.Simulation"/> (they gate sim behavior) and apply SYNCHRONOUSLY at submit
/// (like a command), so a following <c>world.grants</c> read behind the stdin barrier sees the settled table; the
/// server prints the loud accept/reject line. This is a SEPARATE module from the mutation surface to keep both under
/// their analyzer ceilings.
/// </summary>
/// <remarks>Principal tokens: <c>seat1</c>..<c>seat4</c> | <c>console</c> | <c>addon:&lt;name&gt;</c> | <c>peer:&lt;n&gt;</c>
/// (a population entity index). Capability tokens: <c>drive</c> | <c>control</c> | <c>mutate</c> | <c>edit</c>. Subject
/// tokens: <c>body:&lt;n&gt;</c> | <c>screen:&lt;n&gt;</c> | <c>section:&lt;name&gt;</c> | <c>profile:&lt;id&gt;</c> |
/// <c>all</c>. A trailing <c>exclusive</c> on <c>world.grant</c> requests an exclusive hold (rejected if a live holder
/// owns it).</remarks>
internal sealed class WorldGrantCommandModule(WorldServer server, IServerLink link) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.grant",
            description: "Grants a capability to a principal: world.grant <principal> <capability> <subject> [exclusive]. principal = seat1..seat4|console|addon:<name>|peer:<n>; capability = drive|control|mutate|edit; subject = body:<n>|screen:<n>|section:<name>|profile:<id>|all. Applies at submit; an exclusive grant a live holder owns is rejected loudly (the seeded permissive defaults never block one).",
            handler: (_, args) => Handle(args: args, exclusiveAllowed: true, revoke: false),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.revoke",
            description: "Revokes a capability from a principal: world.revoke <principal> <capability> <subject>. Same token grammar as world.grant (exclusive is ignored). Applies at submit; the body/section then denies that principal's writes loudly.",
            handler: (_, args) => Handle(args: args, exclusiveAllowed: false, revoke: true),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.grants",
            description: "Echoes the grant table (Immediate; the stdin barrier makes it read the settled table after any pending grant): world.grants [principal]. With a principal token it lists only that principal's rows. An exclusive grant is tagged (x).",
            handler: (_, args) => {
                if (args.Count > 1) {
                    return Usage(verb: "world.grants", form: "[principal]");
                }

                WorldPrincipal? filter = null;

                if (args.Count == 1) {
                    if (TryParsePrincipal(token: args[0], principal: out var principal)) {
                        filter = principal;
                    } else {
                        return new CommandResult(Output: $"[world.grants: unknown principal '{args[0].ToString()}' — seat1..seat4|console|addon:<name>|peer:<n>]") {
                            IsError = true,
                        };
                    }
                }

                return new CommandResult(Output: server.Grants.Describe(filter: filter));
            }
        );
    }

    // Parse and submit a grant/revoke. Both share the principal/capability/subject grammar; grant additionally takes an
    // optional trailing 'exclusive'. A parse error echoes inline and submits nothing.
    private CommandResult Handle(in WireArgs args, bool exclusiveAllowed, bool revoke) {
        var verb = (revoke ? "world.revoke" : "world.grant");
        var maximum = (exclusiveAllowed ? 4 : 3);

        if ((args.Count < 3) || (args.Count > maximum)) {
            return Usage(verb: verb, form: (exclusiveAllowed ? "<principal> <capability> <subject> [exclusive]" : "<principal> <capability> <subject>"));
        }

        if (!TryParsePrincipal(token: args[0], principal: out var principal)) {
            return new CommandResult(Output: $"[{verb}: unknown principal '{args[0].ToString()}' — seat1..seat4|console|addon:<name>|peer:<n>]") {
                IsError = true,
            };
        }

        if (!TryParseCapability(token: args[1], capability: out var capability)) {
            return new CommandResult(Output: $"[{verb}: unknown capability '{args[1].ToString()}' — drive|control|mutate|edit]") {
                IsError = true,
            };
        }

        if (!TryParseSubject(token: args[2], subject: out var subject)) {
            return new CommandResult(Output: $"[{verb}: unknown subject '{args[2].ToString()}' — body:<n>|screen:<n>|section:<name>|profile:<id>|all]") {
                IsError = true,
            };
        }

        var exclusive = false;

        if (args.Count == 4) {
            if (!args.Is(index: 3, value: "exclusive")) {
                return new CommandResult(Output: $"[{verb}: unknown flag '{args[3].ToString()}' — the only trailing token is 'exclusive']") {
                    IsError = true,
                };
            }

            exclusive = true;
        }

        var grant = new WorldGrant(Principal: principal, Capability: capability, Subject: subject, Exclusive: exclusive);

        if (revoke) {
            link.SubmitRevoke(grant: grant);
        } else {
            link.SubmitGrant(grant: grant);
        }

        // The server prints the loud [world.grant: …] / [world.revoke: …] line at submit; the verb stays quiet.
        return CommandResult.None;
    }

    private static CommandResult Usage(string verb, string form) {
        return new CommandResult(Output: $"[{verb}: expected {form}]") {
            IsError = true,
        };
    }

    private static bool TryParsePrincipal(ReadOnlySpan<char> token, out WorldPrincipal principal) {
        principal = WorldPrincipal.Console;

        if (token.Equals(other: "console", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (token.StartsWith(value: "addon:", comparisonType: StringComparison.OrdinalIgnoreCase) && (token.Length > 6)) {
            principal = WorldPrincipal.Addon(name: token[6..].ToString());

            return true;
        }

        if (token.StartsWith(value: "seat", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token[4..], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var seat) && (seat >= 1) && (seat <= 4)) {
            principal = WorldPrincipal.Seat(slot: (seat - 1));

            return true;
        }

        if (token.StartsWith(value: "peer:", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token[5..], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var peer) && (peer >= 0)) {
            principal = WorldPrincipal.Peer(index: peer);

            return true;
        }

        return false;
    }

    private static bool TryParseCapability(ReadOnlySpan<char> token, out WorldCapability capability) {
        if (token.Equals(other: "drive", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            capability = WorldCapability.Drive;

            return true;
        }

        if (token.Equals(other: "control", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            capability = WorldCapability.Control;

            return true;
        }

        if (token.Equals(other: "mutate", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            capability = WorldCapability.Mutate;

            return true;
        }

        if (token.Equals(other: "edit", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            capability = WorldCapability.Edit;

            return true;
        }

        capability = WorldCapability.Drive;

        return false;
    }

    private static bool TryParseSubject(ReadOnlySpan<char> token, out GrantSubject subject) {
        subject = GrantSubject.All;

        if (token.Equals(other: "all", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (token.StartsWith(value: "body:", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token[5..], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var body) && (body >= 0)) {
            subject = GrantSubject.Body(index: body);

            return true;
        }

        if (token.StartsWith(value: "screen:", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token[7..], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var screen) && (screen >= 0)) {
            subject = GrantSubject.Screen(index: screen);

            return true;
        }

        if (token.StartsWith(value: "section:", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<WorldSection>(value: token[8..], ignoreCase: true, result: out var section)) {
            subject = GrantSubject.Section(section: section);

            return true;
        }

        if (token.StartsWith(value: "profile:", comparisonType: StringComparison.OrdinalIgnoreCase) && (token.Length > 8)) {
            subject = GrantSubject.Profile(id: token[8..].ToString());

            return true;
        }

        return false;
    }
}
