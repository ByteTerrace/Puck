using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

// world.assign: the one row→entity assignment-sequence primitive over the kits and looks tables, submitted through the
// addressed instance's own link.
public sealed partial class WorldRowCommandModule {
    // The one r1/cycle assignment-sequence builder both kits and looks reduce to — they differ only in which
    // WorldMutation kind wraps the built WorldRowAssignment and r1's own additive offset (the two sequences must not
    // land on the same index for the same tick, so each table keeps its own authored offset).
    private CommandResult BuildAssignment(IServerLink link, in WireArgs args, int r1Offset, Func<Principal, WorldRowAssignment, WorldMutation> toMutation, Principal principal, string verb) {
        if (args.Is(
            index: 1,
            value: WorldSequence.R1
        )) {
            return link.Submit(
                mutation: toMutation(
                    principal,
                    new WorldRowAssignment(
                        Sequence: new WorldSequence(
                            Name: WorldSequence.R1,
                            Offset: r1Offset,
                            Step: 0f
                        ),
                        Rows: []
                    )
                ),
                echoes: echoes,
                verb: "world.assign"
            );
        }

        if (args.Is(
            index: 1,
            value: "cycle"
        )) {
            if (args.Count < 3) {
                return CommandResult.Error(output: $"[{verb}: cycle needs at least one name]");
            }

            return link.Submit(
                mutation: toMutation(
                    principal,
                    new WorldRowAssignment(
                        Sequence: new WorldSequence(
                            Name: WorldSequence.Index,
                            Offset: 0,
                            Step: 0f
                        ),
                        Rows: TailIdentifiers(
                            args: args,
                            start: 2
                        )
                    )
                ),
                echoes: echoes,
                verb: "world.assign"
            );
        }

        return CommandResult.Error(output: $"[{verb}: unknown sequence '{args[1].ToString()}' — r1 | cycle]");
    }
    private CommandResult HandleAssign(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return CommandResult.Usage(
                form: "kits|looks r1 | cycle <name> [<name>…]",
                verb: "world.assign"
            );
        }

        if (!authority.TryResolveInstance(
            context: context,
            error: out var error,
            instance: out var instance,
            verb: "world.assign"
        )) {
            return error;
        }

        var link = instance.SubmissionLink;
        var principal = context.Principal;
        var target = args[0].ToString();

        return target switch {
            "kits" => BuildAssignment(
            args: args,
            link: link,
            r1Offset: 1,
            toMutation: static (principal, assignment) => new WorldMutation.SetKitAssignment(
                Assignment: assignment,
                Principal: principal
            ),
            principal: principal,
            verb: "world.assign kits"
        ),
            "looks" => BuildAssignment(
            args: args,
            link: link,
            r1Offset: 129,
            toMutation: static (principal, assignment) => new WorldMutation.SetLookAssignment(
                Assignment: assignment,
                Principal: principal
            ),
            principal: principal,
            verb: "world.assign looks"
        ),
            _ => CommandResult.Error(output: $"[world.assign: unknown target '{target}' — kits|looks]"),
        };
    }
}
