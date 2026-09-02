using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private static string DescribeAttachmentFixed(FixedQ4816 value) => ((double)value).ToString(
        format: "0.#####",
        provider: CultureInfo.InvariantCulture
    );
    private static string DescribeAttachmentMode(WorldBodyAttachmentMode mode) => (mode switch {
        WorldBodyAttachmentMode.Grapple => "grapple",
        _ => "none",
    });
    // The body.attachment read-back: mode, anchor, and rope length. Surface holds are body.hold's to answer.
    private static string DescribeAttachment(int index, WorldBody body) {
        var anchor = ((body.AttachmentAnchor is { } point)
            ? $"({DescribeAttachmentFixed(value: point.X)}, {DescribeAttachmentFixed(value: point.Y)}, {DescribeAttachmentFixed(value: point.Z)})"
            : "n/a"
        );
        var rope = ((body.AttachmentRopeLength is { } length)
            ? DescribeAttachmentFixed(value: length)
            : "n/a"
        );

        return $"[body.attachment: body:{index} mode={DescribeAttachmentMode(mode: body.AttachmentMode)} anchor={anchor} rope={rope}]";
    }
    // The body.hold read-back: which row of the kit's ordered hold list holds the body, the surface normal it holds
    // (n/a for a free hold), and what the row still has left to spend (n/a for a row that spends nothing).
    private static string DescribeHold(int index, WorldBody body) {
        if (body.HoldName is not { } name) {
            return $"[body.hold: body:{index} hold=none normal=n/a spend=n/a]";
        }

        var normal = body.HoldNormal;
        var spend = ((body.HoldSpendRemaining is { } remaining)
            ? DescribeAttachmentFixed(value: remaining)
            : "n/a"
        );

        return $"[body.hold: body:{index} hold={name} normal={((normal == Puck.Maths.FixedVector3.Zero) ? "n/a" : $"({DescribeAttachmentFixed(value: normal.X)}, {DescribeAttachmentFixed(value: normal.Y)}, {DescribeAttachmentFixed(value: normal.Z)})")} spend={spend}]";
    }
    // Shared front matter for body.attach/body.detach: resolve the target, refuse by name when the world authors no
    // attachment section or no channel for this lane (a world authoring nothing keeps today's behavior — see
    // WorldAttachmentSection.Absent), then press the resolved ordinal for one host step — the same edge a bound pad
    // chord would fire, scripted.
    private CommandResult PressAttachmentLane(CommandContext context, in WireArgs args, int ordinal, string laneName, string verb) {
        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: verb
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (!m_population.CompiledAttachment.Enabled) {
            return CommandResult.Error(output: $"[{verb}: the world authors no attachment section — see world.attach-policy]");
        }

        if (ordinal < 0) {
            return CommandResult.Error(output: $"[{verb}: the world's attachment section declares no {laneName} channel]");
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

        return new CommandResult(Output: DescribeAttachment(
            body: player,
            index: index
        ));
    }
    private CommandResult AttachHandler(CommandContext context, WireArgs args) => PressAttachmentLane(
        args: in args,
        context: context,
        laneName: "attach",
        ordinal: m_population.CompiledAttachment.AttachChannelOrdinal,
        verb: "body.attach"
    );
    private CommandResult DetachHandler(CommandContext context, WireArgs args) => PressAttachmentLane(
        args: in args,
        context: context,
        laneName: "detach",
        ordinal: m_population.CompiledAttachment.DetachChannelOrdinal,
        verb: "body.detach"
    );
    // body.reel <rate> [holdSeconds] [body]: <rate> is bipolar (-1 reels in, +1 reels out, magnitude scales
    // FixedWorldAttachment.ReelRate — see ProcessReel). Meaningless outside Grapple; pressing it while climbing or
    // unattached is accepted (the channel value changes) but read by nothing, exactly like an unbound composition
    // channel today.
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

        var ordinal = m_population.CompiledAttachment.ReelChannelOrdinal;

        if (!m_population.CompiledAttachment.Enabled) {
            return CommandResult.Error(output: "[body.reel: the world authors no attachment section — see world.attach-policy]");
        }

        if (ordinal < 0) {
            return CommandResult.Error(output: "[body.reel: the world's attachment section declares no reel channel]");
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

        return new CommandResult(Output: DescribeAttachment(
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
    private CommandResult AttachmentHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[body.attachment: expected at most 1 value — an optional body index]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "body.attachment"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        return new CommandResult(Output: DescribeAttachment(
            body: player,
            index: index
        ));
    }
    private IEnumerable<CommandDefinition> AttachmentVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.attach",
            description: "Throws the grapple: body.attach [body] presses the world's authored attachment.attachChannel for one host step — the body's facing tries an anchor within attachment.grappleMaxDistance/grappleAssistHalfAngleDegrees. A body already attached ignores it (body.detach first). Refuses by name when the world authors no attachment section or no attachChannel. Echoes the body.attachment read-back.",
            handler: AttachHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.detach",
            description: "Clears an active tether: body.detach [body] presses attachment.detachChannel for one host step. Restores ordinary locomotion, carrying the released velocity scaled by attachment.releaseMomentumScale. A friendly no-op when unattached. Echoes the body.attachment read-back.",
            handler: DetachHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.reel",
            description: "Holds the grapple rope's reel channel: body.reel <rate> [holdSeconds] [body] — <rate> is bipolar [-1, 1] (negative reels in, positive reels out), scaling attachment.reelRate; [holdSeconds] how long it reads held (default one host step). Meaningless outside Grapple mode. Echoes the body.attachment read-back.",
            handler: ReelHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "body.attachment",
            description: "Echoes a body's attachment state: body.attachment [body] — [body.attachment: body:<n> mode=<none|grapple> anchor=(x, y, z) rope=<length|n/a>]. anchor and rope read n/a while unattached. Surface holds are body.hold's to echo.",
            handler: AttachmentHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "body.hold",
            description: "Echoes which of a body's authored holds holds it: body.hold [body] — [body.hold: body:<n> hold=<name|none> normal=(x, y, z) spend=<left|n/a>]. hold names the row of the kit's motion.holds list the body currently holds; normal is the held surface's outward normal, n/a for a free hold or none at all; spend is what the row's spend.state slot has left, n/a for a row that spends nothing.",
            handler: HoldHandler
        );
    }
}
