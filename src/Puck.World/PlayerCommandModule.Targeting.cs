using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private CommandResult DesignateHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (2 or 3)) {
            return CommandResult.Error(output: "[player.designate: expected <register> <body:n|nearest|at:x,y,z> [player]]");
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
        var subject = default(GrantSubject);
        Puck.Maths.FixedVector3? point = null;

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
        } else if (TryParsePointToken(
            point: out var parsed,
            token: args[1].ToString()
        )) {
            point = parsed;
        } else if (
            !GrantSubject.TryParse(
            token: args[1],
            subject: out subject
        ) ||
            (subject.Kind != GrantSubjectKind.Body)
        ) {
            return CommandResult.Error(output: $"[player.designate: subject '{args[1].ToString()}' must be body:<n>, nearest, or at:x,y,z]");
        }

        m_link.SubmitDesignation(
            designation: new WorldDesignation(
                EntityIndex: (index - 1),
                Register: register,
                Subject: subject,
                Point: point
            ),
            principal: context.ActingPrincipal()
        );

        return TargetsResult(index: index);
    }
    // The at:x,y,z world-point token — three invariant-culture decimals quantized through the same FixedQ4816
    // conversion every authored document value takes.
    private static bool TryParsePointToken(string token, out Puck.Maths.FixedVector3 point) {
        point = default;
        if (!token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "at:"
        )) {
            return false;
        }

        var parts = token[3..].Split(separator: ',');

        if (
            (parts.Length != 3) ||
            !double.TryParse(
            result: out var x,
            provider: System.Globalization.CultureInfo.InvariantCulture,
            s: parts[0]
        ) ||
            !double.TryParse(
            result: out var y,
            provider: System.Globalization.CultureInfo.InvariantCulture,
            s: parts[1]
        ) ||
            !double.TryParse(
            result: out var z,
            provider: System.Globalization.CultureInfo.InvariantCulture,
            s: parts[2]
        ) ||
            !double.IsFinite(d: x) ||
            !double.IsFinite(d: y) ||
            !double.IsFinite(d: z)
        ) {
            return false;
        }

        point = new Puck.Maths.FixedVector3(
            X: Puck.Maths.FixedQ4816.FromDouble(value: x),
            Y: Puck.Maths.FixedQ4816.FromDouble(value: y),
            Z: Puck.Maths.FixedQ4816.FromDouble(value: z)
        );
        return true;
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
