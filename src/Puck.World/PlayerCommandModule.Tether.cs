using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private static string DescribeTetherFixed(FixedQ4816 value) => ((double)value).ToString(
        format: "0.#####",
        provider: CultureInfo.InvariantCulture
    );
    // The body.tether read-back: whether attached, the anchor, and the rope length. Surface holds are body.hold's
    // to answer.
    private static string DescribeTether(int index, WorldBody body) {
        var attached = (body.TetherLength is not null);
        var anchor = (attached
            ? $"({DescribeTetherFixed(value: body.TetherAnchorPointOrLocalOffset.X)}, {DescribeTetherFixed(value: body.TetherAnchorPointOrLocalOffset.Y)}, {DescribeTetherFixed(value: body.TetherAnchorPointOrLocalOffset.Z)})"
            : "n/a"
        );
        var rope = ((body.TetherLength is { } length)
            ? DescribeTetherFixed(value: length)
            : "n/a"
        );

        return $"[body.tether: body:{index} attached={(attached ? "yes" : "no")} anchor={anchor} rope={rope}]";
    }
    // The body.hold read-back: which row of the kit's ordered hold list holds the body, the surface normal it holds
    // (n/a for a free hold), and what the row still has left to spend (n/a for a row that spends nothing).
    private static string DescribeHold(int index, WorldBody body) {
        if (body.HoldName is not { } name) {
            return $"[body.hold: body:{index} hold=none normal=n/a spend=n/a]";
        }

        var normal = body.HoldNormal;
        var spend = ((body.HoldSpendRemaining is { } remaining)
            ? DescribeTetherFixed(value: remaining)
            : "n/a"
        );

        return $"[body.hold: body:{index} hold={name} normal={((normal == Puck.Maths.FixedVector3.Zero) ? "n/a" : $"({DescribeTetherFixed(value: normal.X)}, {DescribeTetherFixed(value: normal.Y)}, {DescribeTetherFixed(value: normal.Z)})")} spend={spend}]";
    }
    // Shared front matter for body.attach/body.detach: resolve the target, refuse by name when the target's OWN kit
    // carries no tether facet or no channel for this lane (a kit authoring none keeps every body's tether inert),
    // then press the resolved ordinal for one host step — the same edge a bound pad chord would fire, scripted.
    private CommandResult PressTetherLane(CommandContext context, in WireArgs args, Func<WorldBody, int> ordinalOf, string laneName, string verb) {
        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: verb
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (!player.HasTetherFacet) {
            return CommandResult.Error(output: $"[{verb}: body:{index}'s kit authors no tether facet]");
        }

        var ordinal = ordinalOf(player);

        if (ordinal < 0) {
            return CommandResult.Error(output: $"[{verb}: body:{index}'s kit tether facet declares no {laneName} channel]");
        }

        if (ReplayDriveError(verb: verb) is { } driveError) {
            return driveError;
        }

        m_link.SubmitCommand(command: new WorldCommand.PressChannel(
            Principal: context.ActingPrincipal(),
            EntityIndex: index,
            ChannelOrdinal: ordinal,
            Value: FixedQ4816.One,
            HoldSeconds: null
        ));

        var refusal = m_population.PressRefusal(bodyIndex: index);

        if (refusal is { Length: > 0 }) {
            return new CommandResult(Output: $"[{verb}: body:{index} refused → {refusal}]");
        }

        return new CommandResult(Output: DescribeTether(
            body: player,
            index: index
        ));
    }
    private CommandResult AttachHandler(CommandContext context, WireArgs args) => PressTetherLane(
        args: in args,
        context: context,
        laneName: "attach",
        ordinalOf: static body => body.TetherAttachChannelOrdinal,
        verb: "body.attach"
    );
    private CommandResult DetachHandler(CommandContext context, WireArgs args) => PressTetherLane(
        args: in args,
        context: context,
        laneName: "detach",
        ordinalOf: static body => body.TetherDetachChannelOrdinal,
        verb: "body.detach"
    );
    // body.reel <rate> [holdSeconds] [body]: <rate> is bipolar (-1 reels in, +1 reels out, magnitude scales the
    // kit's tether facet lengthRate — see ProcessTetherReel). Meaningless while unattached; pressing it while
    // climbing or unattached is accepted (the channel value changes) but read by nothing, exactly like an unbound
    // composition channel today.
    private CommandResult ReelHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 3)) {
            return CommandResult.Error(output: "[body.reel: expected <rate> — plus an optional hold time and body index]");
        }

        if (!args.TryFloat(
            index: 0,
            value: out var rate
        )) {
            return CommandResult.Error(output: "[body.reel: could not parse <rate> as a number]");
        }

        if (
            !float.IsFinite(f: rate) ||
            (rate < -1f) ||
            (rate > 1f)
        ) {
            return CommandResult.Error(output: "[body.reel: <rate> must be within [-1, 1] — negative reels in, positive reels out]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 2,
            verb: "body.reel"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (!player.HasTetherFacet) {
            return CommandResult.Error(output: $"[body.reel: body:{index}'s kit authors no tether facet]");
        }

        var ordinal = player.TetherReelChannelOrdinal;

        if (ordinal < 0) {
            return CommandResult.Error(output: $"[body.reel: body:{index}'s kit tether facet declares no reel channel]");
        }

        float? holdSeconds = null;

        if (args.Count >= 2) {
            if (!args.TryFloat(
                index: 1,
                value: out var authoredHoldSeconds
            )) {
                return CommandResult.Error(output: "[body.reel: could not parse <holdSeconds> as a number]");
            }

            holdSeconds = authoredHoldSeconds;
        }

        if (ReplayDriveError(verb: "body.reel") is { } driveError) {
            return driveError;
        }

        m_link.SubmitCommand(command: new WorldCommand.PressChannel(
            Principal: context.ActingPrincipal(),
            EntityIndex: index,
            ChannelOrdinal: ordinal,
            Value: FixedQ4816.FromDouble(value: rate),
            HoldSeconds: holdSeconds
        ));

        var refusal = m_population.PressRefusal(bodyIndex: index);

        if (refusal is { Length: > 0 }) {
            return new CommandResult(Output: $"[body.reel: body:{index} refused → {refusal}]");
        }

        return new CommandResult(Output: DescribeTether(
            body: player,
            index: index
        ));
    }
    private CommandResult HoldHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[body.hold: expected at most 1 value — an optional body index]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "body.hold"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        return new CommandResult(Output: DescribeHold(
            body: player,
            index: index
        ));
    }
    private CommandResult TetherHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[body.tether: expected at most 1 value — an optional body index]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "body.tether"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        return new CommandResult(Output: DescribeTether(
            body: player,
            index: index
        ));
    }
    private IEnumerable<CommandDefinition> TetherVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.attach",
            description: "Attaches the tether: body.attach [body] presses the target's own kit tether facet's attachChannel for one host step — the body's facing tries an anchor within the facet's maxAnchorDistance/aimHalfAngleDegrees. A body already attached ignores it (body.detach first). Refuses by name when the target's kit authors no tether facet or no attachChannel. Echoes the body.tether read-back.",
            handler: AttachHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.detach",
            description: "Clears an active tether: body.detach [body] presses the target's own kit tether facet's detachChannel for one host step. Restores ordinary locomotion, carrying the released velocity scaled by the facet's releaseVelocityScale. A friendly no-op when unattached. Echoes the body.tether read-back.",
            handler: DetachHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.reel",
            description: "Holds the tether's reel channel: body.reel <rate> [holdSeconds] [body] — <rate> is bipolar [-1, 1] (negative reels in, positive reels out), scaling the target's own kit tether facet's lengthRate; [holdSeconds] how long it reads held (default one host step). Meaningless while unattached. Echoes the body.tether read-back.",
            handler: ReelHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "body.tether",
            description: "Echoes a body's tether state: body.tether [body] — [body.tether: body:<n> attached=<yes|no> anchor=(x, y, z) rope=<length|n/a>]. anchor and rope read n/a while unattached. Surface holds are body.hold's to echo.",
            handler: TetherHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "body.hold",
            description: "Echoes which of a body's authored holds holds it: body.hold [body] — [body.hold: body:<n> hold=<name|none> normal=(x, y, z) spend=<left|n/a>]. hold names the row of the kit's motion.holds list the body currently holds; normal is the held surface's outward normal, n/a for a free hold or none at all; spend is what the row's spend.state slot has left, n/a for a row that spends nothing.",
            handler: HoldHandler
        );
    }
}
