using System.Globalization;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The capability-grant console surface — the dev reflection of the §2.7 principal/grant model: <c>world.grant</c> and
/// <c>world.revoke</c> mutate the server's ONE grant table over the wire, and <c>world.grants</c> echoes it. Grant
/// changes route <see cref="CommandRouting.Simulation"/> (they gate sim behavior) and apply SYNCHRONOUSLY at submit
/// (like a command), so a following <c>world.grants</c> read behind the stdin barrier sees the settled table; the
/// server prints the loud accept/reject line. This is a SEPARATE module from the mutation surface to keep both under
/// their analyzer ceilings.
/// </summary>
/// <remarks>Principal tokens: <c>seat1</c>..<c>seat4</c> | <c>console</c> | <c>addon:&lt;name&gt;</c> | <c>peer:&lt;n&gt;</c>
/// (a population entity index). Capability tokens: <c>drive</c> | <c>control</c> | <c>mutate</c> | <c>edit</c>. Subject
/// tokens: <c>body:&lt;n&gt;</c> | <c>screen:&lt;n&gt;</c> | <c>section:&lt;name&gt;</c> | <c>all</c>. A trailing
/// <c>exclusive</c> on <c>world.grant</c> requests an exclusive hold (rejected if a live holder owns it).</remarks>
internal sealed class WorldGrantCommandModule(WorldServer server, IServerLink link) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.grant",
            description: "Grants a capability to a principal: world.grant <principal> <capability> <subject> [exclusive]. principal = seat1..seat4|console|addon:<name>|peer:<n>; capability = drive|control|mutate|edit; subject = body:<n>|screen:<n>|section:<name>|all. Applies at submit; an exclusive grant a live holder owns is rejected loudly.",
            handler: (_, args) => Handle(args: args, exclusiveAllowed: true, revoke: false),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.revoke",
            description: "Revokes a capability from a principal: world.revoke <principal> <capability> <subject>. Same token grammar as world.grant (exclusive is ignored). Applies at submit; the body/section then denies that principal's writes loudly.",
            handler: (_, args) => Handle(args: args, exclusiveAllowed: false, revoke: true),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.grants",
            description: "Echoes the grant table (Immediate; the stdin barrier makes it read the settled table after any pending grant): world.grants [principal]. With a principal token it lists only that principal's rows. An exclusive grant is tagged (x).",
            handler: (_, args) => {
                if (args.Length > 1) {
                    return Usage(verb: "world.grants", form: "[principal]");
                }

                WorldPrincipal? filter = null;

                if (args.Length == 1) {
                    if (TryParsePrincipal(token: args[0], principal: out var principal)) {
                        filter = principal;
                    } else {
                        return new CommandResult(Output: $"[world.grants: unknown principal '{args[0]}' — seat1..seat4|console|addon:<name>|peer:<n>]") {
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
    private CommandResult Handle(string[] args, bool exclusiveAllowed, bool revoke) {
        var verb = (revoke ? "world.revoke" : "world.grant");
        var maximum = (exclusiveAllowed ? 4 : 3);

        if ((args.Length < 3) || (args.Length > maximum)) {
            return Usage(verb: verb, form: (exclusiveAllowed ? "<principal> <capability> <subject> [exclusive]" : "<principal> <capability> <subject>"));
        }

        if (!TryParsePrincipal(token: args[0], principal: out var principal)) {
            return new CommandResult(Output: $"[{verb}: unknown principal '{args[0]}' — seat1..seat4|console|addon:<name>|peer:<n>]") {
                IsError = true,
            };
        }

        if (!TryParseCapability(token: args[1], capability: out var capability)) {
            return new CommandResult(Output: $"[{verb}: unknown capability '{args[1]}' — drive|control|mutate|edit]") {
                IsError = true,
            };
        }

        if (!TryParseSubject(token: args[2], subject: out var subject)) {
            return new CommandResult(Output: $"[{verb}: unknown subject '{args[2]}' — body:<n>|screen:<n>|section:<name>|all]") {
                IsError = true,
            };
        }

        var exclusive = false;

        if (args.Length == 4) {
            if (!string.Equals(a: args[3], b: "exclusive", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return new CommandResult(Output: $"[{verb}: unknown flag '{args[3]}' — the only trailing token is 'exclusive']") {
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

    private static bool TryParsePrincipal(string token, out WorldPrincipal principal) {
        principal = WorldPrincipal.Console;

        if (string.Equals(a: token, b: "console", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (token.StartsWith(value: "addon:", comparisonType: StringComparison.OrdinalIgnoreCase) && (token.Length > 6)) {
            principal = WorldPrincipal.Addon(name: token[6..]);

            return true;
        }

        if (token.StartsWith(value: "seat", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token.AsSpan(start: 4), style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var seat) && (seat >= 1) && (seat <= 4)) {
            principal = WorldPrincipal.Seat(slot: (seat - 1));

            return true;
        }

        if (token.StartsWith(value: "peer:", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token.AsSpan(start: 5), style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var peer) && (peer >= 0)) {
            principal = WorldPrincipal.Peer(index: peer);

            return true;
        }

        return false;
    }

    private static bool TryParseCapability(string token, out WorldCapability capability) {
        switch (token.ToLowerInvariant()) {
            case "drive":
                capability = WorldCapability.Drive;

                return true;
            case "control":
                capability = WorldCapability.Control;

                return true;
            case "mutate":
                capability = WorldCapability.Mutate;

                return true;
            case "edit":
                capability = WorldCapability.Edit;

                return true;
            default:
                capability = WorldCapability.Drive;

                return false;
        }
    }

    private static bool TryParseSubject(string token, out GrantSubject subject) {
        subject = GrantSubject.All;

        if (string.Equals(a: token, b: "all", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (token.StartsWith(value: "body:", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token.AsSpan(start: 5), style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var body) && (body >= 0)) {
            subject = GrantSubject.Body(index: body);

            return true;
        }

        if (token.StartsWith(value: "screen:", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token.AsSpan(start: 7), style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var screen) && (screen >= 0)) {
            subject = GrantSubject.Screen(index: screen);

            return true;
        }

        if (token.StartsWith(value: "section:", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<WorldSection>(value: token[8..], ignoreCase: true, result: out var section)) {
            subject = GrantSubject.Section(section: section);

            return true;
        }

        return false;
    }
}
