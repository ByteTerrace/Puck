using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The self-update document section's read-back verb — <c>world.update</c> echoes the authored <c>update</c>
/// section's OPERATIONAL fields (channel/cache root/check interval/keep-N-versions), or 'none' when the section is
/// absent (the app runs its own hardcoded defaults). A SEPARATE module from the mutation surface, matching the
/// storage module's own precedent. Authored data only: it says nothing about whether <c>Puck.Launcher.AddSelfUpdate</c>
/// is actually registered in this composition root, or what a live self-update check found — that is
/// <c>update.status</c>/<c>update.check</c>'s job, over the resolved <c>Puck.Launcher.Release.UpdateOptions</c> this
/// section feeds.
/// </summary>
public sealed class WorldUpdateCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.update",
            description: "Reads the update section back: the authored channel/cacheRoot/checkIntervalSeconds/keepVersions, or 'none' when the section is absent (the app runs its own hardcoded self-update defaults). Authored data only — see update.status for the resolved, live configuration.",
            handler: (context, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: $"[world.update: unrecognized '{args[0]}' — expected no arguments]");
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.update"
                )) {
                    return error;
                }

                var update = server.Definition.Update;

                if (update is null) {
                    return new CommandResult(Output: "[world.update: none]");
                }

                var channel = (update.Channel ?? "none");
                var cacheRoot = (update.CacheRoot ?? "none");
                var checkIntervalSeconds = ((update.CheckIntervalSeconds is { } seconds)
                    ? seconds.ToString()
                    : "none"
                );
                var keepVersions = ((update.KeepVersions is { } count)
                    ? count.ToString()
                    : "none"
                );

                return new CommandResult(Output: $"[world.update: channel {channel} cacheRoot {cacheRoot} checkIntervalSeconds {checkIntervalSeconds} keepVersions {keepVersions}]");
            }
        );
    }
}
