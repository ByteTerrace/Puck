using System.Globalization;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>
/// The silo's untagged console vocabulary — <c>silo.*</c> only. Every verb resolves its row through
/// <see cref="WorldSiloHost.TryResolveKey"/>; <c>silo.activate</c>/<c>.deactivate</c>/<c>.checkpoint</c> call the
/// GRAIN (<see cref="IGrainFactory"/>), never the host directly, and never block the tick thread on a grain call —
/// this module runs on the SAME thread the host's activation mailbox drains on, so waiting here for a grain turn
/// that itself waits for that mailbox would deadlock. Those three verbs fire the grain call and report the request
/// accepted; the outcome lands in <c>silo.grains</c>'/<c>silo.status</c>' next read-back.
/// </summary>
public sealed class SiloCommandModule(WorldSiloHost host, IGrainFactory grainFactory, SiloConsoleRouting routing) : ICommandModule {
    private static void FireAndForget(Task work, string verb, string key) {
        _ = work.ContinueWith(
            continuationAction: task => {
                if (task.IsFaulted) {
                    Console.Error.WriteLine(value: $"[{verb}: '{key}' failed — {(task.Exception!.InnerException?.Message ?? task.Exception.Message)}]");
                } else if ((task is Task<bool> boolTask) && !boolTask.Result) {
                    Console.Error.WriteLine(value: $"[{verb}: '{key}' did not complete]");
                } else {
                    Console.Error.WriteLine(value: $"[{verb}: '{key}' completed]");
                }
            },
            scheduler: TaskScheduler.Default
        );
    }
    private IWorldGrain GrainFor(WorldAuthorityIdentity identity) => grainFactory.GetGrain<IWorldGrain>(
        keyExtension: identity.World.Value,
        primaryKey: identity.Owner
    );

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "silo.status",
            description: "Reads back this silo's own identity: master cadence (Hz), declared world count, store target, and clustering.",
            handler: (context, args) => new CommandResult(Output: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"[silo.status: master={host.MasterRateHz}Hz worlds={host.Definition.Worlds.Count} pinned={host.Definition.Worlds.Count(predicate: static row => row.Pinned)} budget={host.Definition.Doors.Budget} store={host.Definition.Store.Kind} clustering={host.Definition.Clustering.Kind} admitted={host.Instances.Names.Count}]"
            ))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "silo.grains",
            description: "Reads back every currently admitted row: key, world id, rateHz, tick, elapsedEngineTicks, accumulator, behindTicks, held/awaiting-mirrors, door endpoint, paused, last checkpoint ordinal/tick/outcome, checkpointDeferred, pending/last journal append outcome, health.",
            handler: (context, args) => {
                var rows = host.DescribeRows();

                if (rows.Count == 0) {
                    return new CommandResult(Output: "[silo.grains: 0 rows]");
                }

                var lines = rows.Select(selector: static row => string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{row.Key} world={row.World} rateHz={row.RateHz} tick={row.Tick} elapsed={row.ElapsedEngineTicks} accumulator={row.ScheduleAccumulatorTicks} behindTicks={row.BehindTicks} awaitingMirrors={row.AwaitingMirrors} paused={row.Paused} door={(string.IsNullOrEmpty(value: row.DoorEndpoint) ? "unbound" : row.DoorEndpoint)} subject={row.FederationSubject} lastCheckpoint={((row.LastCheckpointOrdinal < 0) ? "none" : $"{row.LastCheckpointOrdinal}@{row.LastCheckpointTick}")} outcome={row.LastCheckpointOutcome} deferred={row.CheckpointDeferredCount} journalPending={row.PendingJournalAppends} journalOutcome={row.LastJournalOutcome}"
                ));

                return new CommandResult(Output: $"[silo.grains: {rows.Count} row(s)]{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: lines)}");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "silo.publish",
            description: "silo.publish <key> <path>: composes the local world document at <path> (basis folded, validated) and publishes it as the hosted, composed definition.json for the declared row named by <key> (owner/{oid}/{world} or the bare world id).",
            handler: (context, args) => {
                if (args.Count != 2) {
                    return CommandResult.Error(output: "[silo.publish: expected exactly two values — <key> <path>]");
                }

                if (!host.TryResolveKey(
                    identity: out var identity,
                    key: args[0].ToString(),
                    reason: out var keyReason
                )) {
                    return CommandResult.Error(output: $"[silo.publish: refused ({keyReason})]");
                }

                if (!WorldFileOrigin.TryResolveCanonicalPath(
                    path: args[1].ToString(),
                    resolved: out var resolvedPath
                )) {
                    return CommandResult.Error(output: $"[silo.publish: no file at '{args[1]}']");
                }

                var origin = new WorldFileOrigin(resolvedPath: resolvedPath);

                if (!origin.TryLoad(
                    definition: out var definition,
                    instanceIdentity: identity.World.Value,
                    reason: out var loadReason
                )) {
                    return CommandResult.Error(output: $"[silo.publish: '{args[0]}' refused ({loadReason})]");
                }

                var outcome = host.PublishDefinitionAsync(
                    ct: CancellationToken.None,
                    composed: definition!,
                    identity: identity
                ).GetAwaiter().GetResult();

                return (outcome.Ok
                    ? new CommandResult(Output: $"[silo.publish: '{args[0]}' published from '{resolvedPath}']")
                    : CommandResult.Error(output: $"[silo.publish: '{args[0]}' failed — {outcome.Detail}]")
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "silo.activate",
            description: "silo.activate <key>: requests activation of the declared row named by <key> through its grain. Requested, not synchronous — read silo.grains for the outcome.",
            handler: (context, args) => {
                if (args.Count != 1) {
                    return CommandResult.Error(output: "[silo.activate: expected exactly one value — <key>]");
                }

                if (!host.TryResolveKey(
                    identity: out var identity,
                    key: args[0].ToString(),
                    reason: out var reason
                )) {
                    return CommandResult.Error(output: $"[silo.activate: refused ({reason})]");
                }

                FireAndForget(
                    key: args[0].ToString(),
                    verb: "silo.activate",
                    work: GrainFor(identity: identity).ActivateAsync()
                );

                return new CommandResult(Output: $"[silo.activate: '{args[0]}' requested]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "silo.deactivate",
            description: "silo.deactivate <key>: requests deactivation (with a final checkpoint) of the admitted row named by <key> through its grain. Requested, not synchronous — read silo.grains for the outcome.",
            handler: (context, args) => {
                if (args.Count != 1) {
                    return CommandResult.Error(output: "[silo.deactivate: expected exactly one value — <key>]");
                }

                if (!host.TryResolveKey(
                    identity: out var identity,
                    key: args[0].ToString(),
                    reason: out var reason
                )) {
                    return CommandResult.Error(output: $"[silo.deactivate: refused ({reason})]");
                }

                FireAndForget(
                    key: args[0].ToString(),
                    verb: "silo.deactivate",
                    work: GrainFor(identity: identity).DeactivateAsync()
                );

                return new CommandResult(Output: $"[silo.deactivate: '{args[0]}' requested]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "silo.checkpoint",
            description: "silo.checkpoint [<key>]: requests an immediate checkpoint of the named row, or of every currently admitted row when <key> is omitted, through each row's own grain. Requested, not synchronous — read silo.grains for the outcome.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[silo.checkpoint: expected at most one value — [<key>]]");
                }

                if (args.Count == 1) {
                    if (!host.TryResolveKey(
                        identity: out var identity,
                        key: args[0].ToString(),
                        reason: out var reason
                    )) {
                        return CommandResult.Error(output: $"[silo.checkpoint: refused ({reason})]");
                    }

                    FireAndForget(
                        key: args[0].ToString(),
                        verb: "silo.checkpoint",
                        work: GrainFor(identity: identity).CheckpointNowAsync()
                    );

                    return new CommandResult(Output: $"[silo.checkpoint: '{args[0]}' requested]");
                }

                var admitted = host.Instances.Names;

                foreach (var worldId in admitted) {
                    if (host.TryResolveKey(
                        identity: out var rowIdentity,
                        key: worldId,
                        reason: out _
                    )) {
                        FireAndForget(
                            key: worldId,
                            verb: "silo.checkpoint",
                            work: GrainFor(identity: rowIdentity).CheckpointNowAsync()
                        );
                    }
                }

                return new CommandResult(Output: $"[silo.checkpoint: {admitted.Count} row(s) requested]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "silo.use",
            description: "silo.use <key>: selects the row an untagged console line (one with no leading '@<key> ') routes to. Never refuses admitted-row traffic addressed with an explicit tag.",
            handler: (context, args) => {
                if (args.Count != 1) {
                    return CommandResult.Error(output: "[silo.use: expected exactly one value — <key>]");
                }

                if (!host.TryResolveKey(
                    identity: out var identity,
                    key: args[0].ToString(),
                    reason: out var reason
                )) {
                    return CommandResult.Error(output: $"[silo.use: refused ({reason})]");
                }

                if (!routing.TryGetSession(
                    session: out _,
                    worldId: identity.World.Value
                )) {
                    return CommandResult.Error(output: $"[silo.use: '{args[0]}' is declared but not currently admitted]");
                }

                routing.SetDefault(worldId: identity.World.Value);

                return new CommandResult(Output: $"[silo.use: untagged lines now route to '{identity.World.Value}']");
            }
        );
    }
}
