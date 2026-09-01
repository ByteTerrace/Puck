using System.Globalization;
using System.Text;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Names each <see cref="WorldReplayEntry"/> kind and its salient payload in one compact fragment for
/// <c>replay.inspect</c>'s per-tick line — the kind first, then the target and the values an operator would need to
/// recognize the line they typed (<c>press p1 forward=1 hold=2s by console</c>), never the whole leaf.</summary>
public static class WorldReplayEntryDescriber {
    /// <summary>Describes one authority/server-event entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="channels">The recorded world's channel table, for naming a channel ordinal.</param>
    /// <returns>The fragment.</returns>
    public static string Describe(WorldReplayEntry entry, WorldChannelTable channels) {
        ArgumentNullException.ThrowIfNull(argument: entry);
        ArgumentNullException.ThrowIfNull(argument: channels);

        return entry switch {
            WorldReplayEntry.Command command => DescribeCommand(
                channels: channels,
                command: command.Value
            ),
            WorldReplayEntry.Grant grant => $"grant {DescribeGrant(grant: grant.Value)} by {grant.Actor.Describe()}",
            WorldReplayEntry.Revoke revoke => $"revoke {DescribeGrant(grant: revoke.Value)} by {revoke.Actor.Describe()}",
            WorldReplayEntry.Session session => $"{DescribeSession(request: session.Value)} by {session.Value.Principal.Describe()}",
            WorldReplayEntry.Designation designation => $"designate {WorldReplayInspector.DescribeEntity(index: designation.Value.EntityIndex)} {designation.Value.Register}={designation.Value.Subject.Describe()} by {designation.Actor.Describe()}",
            WorldReplayEntry.Mutation mutation => $"mutation {mutation.Value.GetType().Name} by {mutation.Actor.Describe()} {(mutation.Outcome
                ? "accepted"
                : "refused")}",
            WorldReplayEntry.Undo undo => $"undo {undo.Count} by {undo.Actor.Describe()}",
            WorldReplayEntry.Composition composition => $"{DescribeComposition(composition: composition.Value)} by {composition.Actor.Describe()}",
            WorldReplayEntry.Query query => $"query {query.Value.GetType().Name} by {query.Actor.Describe()}",
            WorldReplayEntry.PeerAdmitted admitted => $"peerAdmitted {DescribePeers(entries: admitted.Value.Entries)} grants={admitted.Value.MintedGrants.Count}",
            WorldReplayEntry.PeerDisconnected disconnected => $"peerDisconnected {DescribePeers(entries: disconnected.Value.Entries)} revoked={disconnected.Value.RevokedGrants.Count}",
            WorldReplayEntry.Rebuild rebuild => DescribeRebuild(rebuild: rebuild),
            WorldReplayEntry.ScreenOp screenOp => DescribeScreenOp(screenOp: screenOp),
            WorldReplayEntry.RateLever lever => (lever.Paused
                ? "rate paused"
                : "rate resumed"),
            WorldReplayEntry.Transfer transfer => $"transfer #{transfer.TransferId} -> '{transfer.DestinationName}' scope={transfer.ScopeKey} generation={transfer.GenerationId} {transfer.Outcome} departed=[{string.Join(
                separator: ",",
                values: transfer.DepartedBootSlots
            )}]",
            WorldReplayEntry.LinkDelivery link => $"link '{link.Adjacency}' delivered",
            _ => entry.GetType().Name,
        };
    }
    private static string DescribeCommand(WorldCommand command, WorldChannelTable channels) {
        var entity = WorldReplayInspector.DescribeEntity(index: command.EntityIndex);
        var body = command switch {
            WorldCommand.SnapPose pose => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"pose {entity} pos=({pose.Position.X:0.##}, {pose.Position.Y:0.##}, {pose.Position.Z:0.##}) yaw={pose.YawRadians:0.###} pitch={pose.PitchRadians:0.###} roll={pose.RollRadians:0.###}"
            ),
            WorldCommand.EnqueueSegment segment => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"segment {entity} {segment.Seconds:0.##}s{DescribeIntent(channels: channels, intent: segment.Intent)}"
            ),
            WorldCommand.PressChannel press => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"press {entity} {(channels.Name(ordinal: press.ChannelOrdinal) ?? $"ch{press.ChannelOrdinal}")}={WorldReplayInspector.DescribeValue(value: press.Value)}{((press.HoldSeconds is { } hold)
                    ? $" hold={hold:0.##}s"
                    : "")}"
            ),
            WorldCommand.SetBodyMotion motion => $"motion {entity} '{motion.BodyMotionProgram}'",
            WorldCommand.SetControl control => $"control {entity} {control.Source.ToString().ToLowerInvariant()}",
            WorldCommand.Reconcile reconcile => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"reconcile {entity} pos=({reconcile.X:0.##}, {reconcile.Z:0.##}) yaw={reconcile.YawRadians:0.###} over {reconcile.Seconds:0.##}s"
            ),
            WorldCommand.Stop => $"stop {entity}",
            WorldCommand.ComposeControl compose => $"engage {entity} -> {compose.Target.Describe()}{(compose.Exclusive
                ? " exclusive"
                : "")} as {compose.TargetPrincipal.Describe()}",
            WorldCommand.DissolveControl dissolve => $"disengage {entity} as {dissolve.TargetPrincipal.Describe()}",
            WorldCommand.LoadDurableState durable => $"durable {entity} tick={durable.Tick} values={durable.Values.Count}",
            _ => $"{command.GetType().Name} {entity}",
        };

        return $"{body} by {command.Principal.Describe()}";
    }
    private static string DescribeComposition(WorldComposition composition) => composition switch {
        WorldComposition.SetActiveLayout layout => $"composition layout={(layout.Name ?? "auto")}",
        WorldComposition.SelectCamera camera => $"composition camera={(camera.Name ?? "auto")}",
        _ => $"composition {composition.GetType().Name}",
    };
    private static string DescribeGrant(WorldGrant grant) => $"{grant.Capability.ToString().ToLowerInvariant()} {grant.Subject.Describe()} -> {grant.Principal.Describe()}{(grant.Exclusive
        ? " exclusive"
        : "")}";
    // Every non-zero lane of a segment's held intent, named — the vector as the operator typed it.
    private static string DescribeIntent(PlayerIntent intent, WorldChannelTable channels) {
        var text = new StringBuilder();

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (intent[ordinal] == FixedQ4816.Zero) {
                continue;
            }

            text.Append(value: ' ');
            text.Append(value: (channels.Name(ordinal: ordinal) ?? $"ch{ordinal}"));
            text.Append(value: '=');
            text.Append(value: WorldReplayInspector.DescribeValue(value: intent[ordinal]));
        }

        return text.ToString();
    }
    private static string DescribePeers(IReadOnlyList<WorldPeerEventEntry> entries) {
        var text = new StringBuilder();

        text.Append(value: '[');

        for (var index = 0; (index < entries.Count); index++) {
            if (index > 0) {
                text.Append(value: ',');
            }

            text.Append(value: entries[index].Identity.Describe());
        }

        text.Append(value: ']');

        return text.ToString();
    }
    private static string DescribeRebuild(WorldReplayEntry.Rebuild rebuild) => $"rebuild {rebuild.Kind.ToString().ToLowerInvariant()}{((rebuild.PathHint is { } path)
        ? $" '{path}'"
        : "")} {rebuild.ContentHash}{(rebuild.Force
        ? " force"
        : "")} by {rebuild.Actor.Describe()}";
    private static string DescribeScreenOp(WorldReplayEntry.ScreenOp screenOp) {
        var body = screenOp.Value switch {
            WorldScreenOp.Insert insert => $"screen.insert {insert.Index} '{insert.ContentPath}'{((insert.EngineId is { } engine)
                ? $" engine={engine}"
                : "")}{((insert.Options is { } options)
                ? $" options='{options}'"
                : "")}",
            WorldScreenOp.Eject eject => $"screen.eject {eject.Index}",
            WorldScreenOp.Select select => $"screen.select {select.Index} entry={select.Entry}",
            WorldScreenOp.SetOptions setOptions => $"screen.options {setOptions.Index} '{(setOptions.Options ?? "")}'",
            WorldScreenOp.Link link => $"screen.link '{link.Name}' [{string.Join(
                separator: ",",
                values: link.Members
            )}]",
            WorldScreenOp.Unlink unlink => $"screen.unlink '{unlink.Name}'",
            _ => $"screen.{screenOp.Value.GetType().Name.ToLowerInvariant()}",
        };

        return $"{body}{((screenOp.ContentHash is { } hash)
            ? $" content={hash}"
            : "")} by {screenOp.Actor.Describe()}";
    }
    private static string DescribeSession(SessionRequest request) => request switch {
        SessionRequest.Join join => $"session join slot={join.Slot} identity={(join.IdentityName ?? "pending")}",
        SessionRequest.Leave leave => $"session leave slot={leave.Slot}",
        SessionRequest.SetIdentity identity => $"session identity slot={identity.Slot} '{identity.IdentityName}'",
        SessionRequest.SetPopulation population => $"session population {population.Count}",
        SessionRequest.SetPeerSource source => $"session peerSource {source.Source.ToString().ToLowerInvariant()}",
        SessionRequest.RememberPreferredController remember => $"session remember device={remember.Device} identity='{remember.IdentityName}'",
        _ => $"session {request.GetType().Name}",
    };
}
