using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private CommandResult DesignateHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (2 or 3)) {
            return CommandResult.Error(output: "[player.designate: expected <register> <body:n|nearest> [player]]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 2,
            verb: "player.designate"
        );
        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var register = args[0].ToString();
        GrantSubject subject;

        if (args.Is(
            index: 1,
            value: "nearest"
        )) {
            if (!m_client.TryFindDesignationSubject(
                registerName: register,
                sourceBody: (index - 1),
                subject: out subject
            )) {
                return CommandResult.Error(output: $"[player.designate: no client-snapshot candidate lies inside register '{register}'s clamped cone]");
            }
        } else if (
            !GrantSubject.TryParse(
            token: args[1],
            subject: out subject
        ) ||
            (subject.Kind != GrantSubjectKind.Body)
        ) {
            return CommandResult.Error(output: $"[player.designate: subject '{args[1].ToString()}' must be body:<n> or nearest]");
        }

        m_link.SubmitDesignation(
            designation: new WorldDesignation(
                EntityIndex: (index - 1),
                Register: register,
                Subject: subject
            ),
            principal: context.ActingPrincipal()
        );

        return TargetsResult(index: index);
    }
    private CommandResult TargetsHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[player.targets: expected an optional player index]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.targets"
        );
        return ((player is null)
            ? CommandResult.Error(output: error!)
            : TargetsResult(index: index)
        );
    }
    private CommandResult TargetsResult(int index) {
        var result = default(CommandResult);

        m_link.Query(
            query: new WorldQuery.PlayerTargets(Index: index),
            completion: answer => {
                result = new CommandResult(Output: answer.Text) { IsError = answer.Refused };
            }
        );
        return result;
    }
}
