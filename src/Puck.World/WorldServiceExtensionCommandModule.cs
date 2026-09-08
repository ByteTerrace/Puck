using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Reads installed provider types and the authenticated caller's configured service surface.</summary>
internal sealed class WorldServiceExtensionCommandModule(Func<WorldServiceExtensions> extensions) : ICommandModule {
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(name: "world.extensions.catalog",
            description: "Lists installed service provider types. Configuration may select these keys; it cannot load executable paths.",
            bindability: CommandBindability.Unbindable,
            handler: (_, args) => CommandResult.RequireNoArguments(args, "world.extensions.catalog") ??
                new CommandResult(Output: "[world.extensions.catalog: " + string.Join(", ", WorldServiceExtensions.Types.Keys) + "]"));
        yield return CommandDefinition.WithWireArgs(name: "world.extensions",
            description: "Reads configured service operations for the acting principal; the host console also sees connections, observation freshness and admission, and worker failures. Credentials and private configuration are never printed.",
            bindability: CommandBindability.Unbindable,
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args, "world.extensions") is { } refusal) { return refusal; }
                if (extensions().Runtime is not { } runtime) { return new CommandResult(Output: "[world.extensions: disabled; no operator configuration]"); }
                var principal = context.ActingPrincipal();
                if (principal == WorldPrincipal.Console) {
                    return new CommandResult(Output: "[world.extensions: operations=" + string.Join(", ", runtime.OperationNames) + "; connections=" + string.Join("; ", runtime.Connections.Select(connection =>
                        $"{connection.Name}: {connection.Client} {connection.Requests} -> {connection.Operation} -> {connection.Status}")) +
                        "; observations=" + string.Join("; ", runtime.Observations.Select(source =>
                            $"{source.Name}: kind={source.Kind} items={source.Items} tick={source.ObservedTick?.ToString() ?? "never"} reading={source.Reading} applied={source.Applied} submissions={source.Submissions} failure={source.Failure ?? "none"} lastRefusedField={source.LastRefusedField ?? "none"}")) +
                        $"; failure={runtime.LastFailure ?? "none"}]");
                }
                try { return new CommandResult(Output: "[world.extensions: " + string.Join(", ", runtime.Client(principal).Discover().Select(operation => operation.Name)) + "]"); }
                catch (UnauthorizedAccessException) { return CommandResult.Error("[world.extensions: no service grants for this principal]"); }
            });
    }
}
