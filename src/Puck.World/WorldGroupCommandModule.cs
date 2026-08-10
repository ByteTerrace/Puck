using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The group and membership console surface — the group+binding substrate's dev reflection. The policy catalog
/// (<c>groups.kinds</c>) is authored through the general <see cref="WorldRowCommandModule"/> (<c>world.row.set</c>/
/// <c>.remove groups.kinds ...</c>); <c>world.group.form</c>/<c>.join</c>/<c>.leave</c>/<c>.kick</c> work the
/// live roster (a runtime group is added by <c>form</c> and wiped by the next whole-document rebuild — see
/// <see cref="WorldGroup"/>'s own remarks); <c>world.ownership.offer</c>/<c>.accept</c>/<c>.reclaim</c> work the
/// escrow/transfer lane over an already-declared <see cref="WorldOwnership"/> row (see
/// <see cref="WorldMutation.OfferOwnership"/>/<see cref="WorldMutation.SettleOwnership"/>); <c>world.groups</c> is
/// the read-back for all of it. Every mutating verb routes <see cref="CommandRouting.Simulation"/> (they buffer and
/// drain like every other <c>WorldMutation</c>) and returns <see cref="CommandResult.None"/> — the server prints the
/// loud <c>[world.mutation: … applied/rejected]</c> line.
/// </summary>
internal sealed class WorldGroupCommandModule(WorldServer server, IServerLink link) : ICommandModule {
    private delegate WorldMutation Build(string groupId, WorldPrincipal member, WorldPrincipal actor);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.group.form",
            description: "Forms a new, empty RUNTIME group of a declared kind: world.group.form <id> <kindName>. Rejected loudly if <id> is already taken or <kindName> names no declared kind. Never written back to the server's base document, so a whole-document rebuild (world.reset/.load/.reload) discards it — the runtime half of the party-vs-roster split. Buffers and applies at the tick boundary under Mutate/section:groups.",
            handler: (context, args) => {
                if (args.Count != 2) {
                    return Usage(verb: "world.group.form", form: "<id> <kindName>");
                }

                link.SubmitWorldMutation(mutation: new WorldMutation.FormGroup(Principal: context.ActingPrincipal(), Id: args[0].ToString(), KindName: args[1].ToString()));

                return CommandResult.None;
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.group.join",
            description: "Admits <principal> into the group named <group-id>: world.group.join <group-id> <principal>. Same principal token grammar as world.grant (seat1..seat4|console|addon:<name>|peer:<n>:<generation> — never group:<id>, FLAT ONLY refuses a group member that is itself a group, and never world/document, neither of which is a real actor). Rejected loudly if the group does not exist, the principal already belongs, or admitting it would exceed the kind's declared capacity. Buffers and applies at the tick boundary under Mutate/section:groups.",
            handler: (context, args) => Handle(context: context, args: args, verb: "world.group.join", build: static (groupId, member, actor) => new WorldMutation.JoinGroup(Principal: actor, GroupId: groupId, Member: member)),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.group.leave",
            description: "Removes <principal> from the group named <group-id> — voluntary self-departure, never the kind's evictionPolicy (that governs world.group.kick alone): world.group.leave <group-id> <principal>. Dissolves the whole group afterward if that empties it and the kind's lifetime is ephemeral. Rejected loudly if the group does not exist or the principal does not belong. Buffers and applies at the tick boundary under Mutate/section:groups.",
            handler: (context, args) => Handle(context: context, args: args, verb: "world.group.leave", build: static (groupId, member, actor) => new WorldMutation.LeaveGroup(Principal: actor, GroupId: groupId, Member: member)),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.group.kick",
            description: "Removes <principal> from the group named <group-id> under the kind's OWN evictionPolicy: world.group.kick <group-id> <principal>. 'remove' drops only the kicked member's row (then dissolves the group under the same ephemeral-lifetime rule world.group.leave applies); 'disband' drops the WHOLE group row immediately — the kind decides the consequence of a kick, never who may issue one (the same Mutate/section:groups hold every group mutation checks). Rejected loudly if the group does not exist or the principal does not belong. Buffers and applies at the tick boundary.",
            handler: (context, args) => Handle(context: context, args: args, verb: "world.group.kick", build: static (groupId, member, actor) => new WorldMutation.KickMember(Principal: actor, GroupId: groupId, Member: member)),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.groups",
            description: "Echoes the group+membership binding substrate (Immediate; the stdin barrier makes it read the settled table after any pending group mutation): world.groups [group-id]. With a group id, echoes only that group's row. Lists declared kinds (name, roles, ownershipPolicy, lifetime, evictionPolicy, capacity, sharedStateScope), every live group row (id, kind, members), and every ownership binding — including an escrowed row's offerer/recipient/deadline.",
            handler: (_, args) => {
                if (args.Count > 1) {
                    return Usage(verb: "world.groups", form: "[group-id]");
                }

                var filter = ((args.Count == 1) ? args[0].ToString() : null);

                return new CommandResult(Output: Describe(definition: server.Definition, filter: filter));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.ownership.offer",
            description: "Places a Principal-owned subject into ESCROW, naming the intended recipient and a tick deadline: world.ownership.offer <subject> <recipient> <deadlineTick>. <subject> is group:<id> (the only declared subject kind today); <recipient> is a principal token (seat1..seat4|console|addon:<name>|peer:<n>:<generation>). Rejected loudly unless the acting principal IS the subject's current owner, <recipient> differs from the acting principal, and <deadlineTick> lies strictly after the tick this applies at. While escrowed, the subject is owned by NEITHER party — see world.groups' ownership line. Buffers and applies at the tick boundary under Mutate/section:groups.",
            handler: (context, args) => {
                if (args.Count != 3) {
                    return Usage(verb: "world.ownership.offer", form: "<subject> <recipient> <deadlineTick>");
                }

                if (!OwnershipSubject.TryParse(token: args[0], subject: out var subject)) {
                    return CommandResult.Error(output: $"[world.ownership.offer: unknown subject '{args[0].ToString()}' — group:<id>]");
                }

                if (!WorldGrantCommandModule.TryParsePrincipal(token: args[1], principal: out var recipient)) {
                    return CommandResult.Error(output: $"[world.ownership.offer: unknown principal '{args[1].ToString()}' — seat1..seat4|console|addon:<name>|peer:<n>:<generation>]");
                }

                if (!args.TryInt(index: 2, value: out var deadline)) {
                    return CommandResult.Error(output: $"[world.ownership.offer: '{args[2].ToString()}' is not an integer tick]");
                }

                link.SubmitWorldMutation(mutation: new WorldMutation.OfferOwnership(Principal: context.ActingPrincipal(), Subject: subject, Recipient: recipient, DeadlineTick: deadline));

                return CommandResult.None;
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.ownership.accept",
            description: "Accepts a subject currently held in ESCROW: world.ownership.accept <subject>. Rejected loudly unless the subject is in escrow and the acting principal is that escrow's own named recipient. Buffers and applies at the tick boundary under Mutate/section:groups.",
            handler: (context, args) => Settle(context: context, args: args, verb: "world.ownership.accept", reclaim: false),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.ownership.reclaim",
            description: "Reclaims a subject currently held in ESCROW back to its offerer: world.ownership.reclaim <subject>. Rejected loudly unless the subject is in escrow, the acting principal is that escrow's own named offerer, and the tick this applies at has reached the escrow's deadline. The engine ALSO fires this automatically (as the world's own program) the tick a deadline passes with no accept — this verb is the operator's own remedy, on the identical gate, never a way to jump the queue. Buffers and applies at the tick boundary under Mutate/section:groups.",
            handler: (context, args) => Settle(context: context, args: args, verb: "world.ownership.reclaim", reclaim: true),
            routing: CommandRouting.Simulation
        );

        // Local function, not a static method: shares `link` with the module instance — accept/reclaim take the
        // identical <subject> shape and differ only in Reclaim.
        CommandResult Settle(CommandContext context, in WireArgs args, string verb, bool reclaim) {
            if (args.Count != 1) {
                return Usage(verb: verb, form: "<subject>");
            }

            if (!OwnershipSubject.TryParse(token: args[0], subject: out var subject)) {
                return CommandResult.Error(output: $"[{verb}: unknown subject '{args[0].ToString()}' — group:<id>]");
            }

            link.SubmitWorldMutation(mutation: new WorldMutation.SettleOwnership(Principal: context.ActingPrincipal(), Subject: subject, Reclaim: reclaim));

            return CommandResult.None;
        }

        // Local function, not a static method: shares `link` with the module instance — join/leave/kick above take
        // the identical <group-id> <principal> shape and differ only in which WorldMutation kind they build.
        CommandResult Handle(CommandContext context, in WireArgs args, string verb, Build build) {
            if (args.Count != 2) {
                return Usage(verb: verb, form: "<group-id> <principal>");
            }

            if (!WorldGrantCommandModule.TryParsePrincipal(token: args[1], principal: out var member)) {
                return CommandResult.Error(output: $"[{verb}: unknown principal '{args[1].ToString()}' — seat1..seat4|console|addon:<name>|peer:<n>:<generation>]");
            }

            link.SubmitWorldMutation(mutation: build(args[0].ToString(), member, context.ActingPrincipal()));

            return CommandResult.None;
        }
    }

    private static CommandResult Usage(string verb, string form) => CommandResult.Error(output: $"[{verb}: expected {form}]");

    // The read-back: kinds, then every live group row (id-filtered when requested), then ownership bindings. Omits a
    // group with no section at all (an OPTIONAL document that never declared `groups`).
    private static string Describe(WorldDefinition definition, string? filter) {
        var groupsSection = (definition.Groups ?? WorldGroupsSection.Empty);

        if ((groupsSection.Kinds.Count == 0) && (groupsSection.Groups.Count == 0) && (groupsSection.Ownership.Count == 0)) {
            return "[world.groups: (no groups section)]";
        }

        var builder = new System.Text.StringBuilder();

        _ = builder.Append(value: "[world.groups:");

        if (filter is null) {
            foreach (var kind in groupsSection.Kinds) {
                _ = builder.Append(value: " kind ").Append(value: kind.Name).Append(value: " roles=[");

                for (var index = 0; (index < kind.Roles.Count); index++) {
                    if (index > 0) {
                        _ = builder.Append(value: ',');
                    }

                    var role = kind.Roles[index];

                    _ = builder.Append(value: role.Name).Append(value: '=').Append(value: string.Join(separator: '+', values: role.Capabilities));
                }

                _ = builder.Append(value: "] ownership=").Append(value: kind.OwnershipPolicy)
                    .Append(value: " lifetime=").Append(value: kind.Lifetime)
                    .Append(value: " eviction=").Append(value: kind.EvictionPolicy)
                    .Append(value: " cap=").Append(value: kind.Capacity);

                if (kind.SharedStateScope is { } scope) {
                    _ = builder.Append(value: " sharedState=").Append(value: scope);
                }

                _ = builder.Append(value: " |");
            }
        }

        foreach (var group in groupsSection.Groups) {
            if ((filter is { } only) && !string.Equals(a: group.Id, b: only, comparisonType: System.StringComparison.Ordinal)) {
                continue;
            }

            _ = builder.Append(value: " group ").Append(value: group.Id).Append(value: " kind=").Append(value: group.KindName).Append(value: " members=[");
            _ = builder.Append(value: string.Join(separator: ',', values: DescribeMembers(members: group.Members)));
            _ = builder.Append(value: "] |");
        }

        if (filter is null) {
            foreach (var ownership in groupsSection.Ownership) {
                // Escrow prints ITS OWN shape (offerer/recipient/deadline) rather than a bare principal — the
                // read-back rule: an item's current owner INCLUDING escrow, never collapsed to "in transit".
                var owner = ownership.Owner.Kind switch {
                    OwnershipOwnerKind.Group => $"group:{ownership.Owner.GroupId}",
                    OwnershipOwnerKind.Escrow when (ownership.Owner.Escrow is { } escrow) =>
                        $"escrow(offerer={escrow.Offerer.Describe()},recipient={escrow.Recipient.Describe()},deadline={escrow.DeadlineTick})",
                    _ => (ownership.Owner.Principal?.Describe() ?? "?"),
                };

                _ = builder.Append(value: " ownership ").Append(value: ownership.Subject.Kind).Append(value: ':').Append(value: ownership.Subject.Id)
                    .Append(value: " -> ").Append(value: owner)
                    .Append(value: " |");
            }
        }

        return (builder.Append(value: ']').ToString());
    }
    private static IEnumerable<string> DescribeMembers(IReadOnlyList<WorldPrincipal> members) {
        foreach (var member in members) {
            yield return member.Describe();
        }
    }
}
