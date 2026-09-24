using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The <c>world.extensions</c> read-back of the addressed row's host-approved extension runtime
/// (<see cref="WorldInstance.Extensions"/>). The host Console sees operations, connections, observations, embeddings,
/// participants, and the last failure; any other principal sees only the operations granted to it. Credentials and
/// private settings are never printed. The installed provider types are the host's composed contributions, echoed by
/// <c>world.extensions.catalog</c>.</summary>
/// <param name="authority">Resolves the row an invocation addresses.</param>
public sealed class WorldExtensionsCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    private const string Verb = "world.extensions";

    private static string DescribeForConsole(WorldConfiguredExtensions runtime) => $"[{Verb}: operations={string.Join(
        separator: ", ",
        values: runtime.OperationNames
    )}; connections={string.Join(
        separator: "; ",
        values: runtime.Connections.Select(selector: static connection =>
            $"{connection.Name}: {connection.Client} {connection.Requests} -> {connection.Operation} -> {connection.Status}")
    )}; observations={string.Join(
        separator: "; ",
        values: runtime.Observations.Select(selector: static source =>
            $"{source.Name}: kind={source.Kind} items={source.Items} tick={(source.ObservedTick?.ToString() ?? "never")} reading={source.Reading} applied={source.Applied} submissions={source.Submissions} failure={(source.Failure ?? "none")} lastRefusedField={(source.LastRefusedField ?? "none")}")
    )}; embeddings={string.Join(
        separator: "; ",
        values: runtime.Embeddings.Select(selector: static embedding =>
            $"{embedding.Name}: space={embedding.Space} model={embedding.Identity.Model} revision={embedding.Identity.Revision} dimensions={embedding.Identity.Dimensions} selected={embedding.Selected} cached={embedding.Cached} submitted={embedding.Submitted} failed={embedding.Failed} lastCallTick={(embedding.LastCallTick?.ToString() ?? "never")} inFlight={embedding.InFlight} failure={(embedding.LastFailure ?? "none")}")
    )}; participants={string.Join(
        separator: "; ",
        values: runtime.Participants
    )}; failure={(runtime.LastFailure ?? "none")}]";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            description: "Reads the addressed world's configured service operations for the acting principal; the host console also sees connections, observation freshness and admission, embeddings, participants, and worker failures. Credentials and private configuration are never printed.",
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(
                    args: args,
                    verb: Verb
                ) is { } refusal) { return refusal; }
                if (!authority.TryResolve(
                    context: context,
                    instance: out var instance,
                    refusal: out var unresolved
                )) {
                    return CommandResult.Error(output: $"[{Verb}: refused ({unresolved})]");
                }
                if (instance.Extensions is not { } runtime) { return new CommandResult(Output: $"[{Verb}: disabled; no operator configuration]"); }
                var principal = context.Principal;

                if (principal == Principal.Console) { return new CommandResult(Output: DescribeForConsole(runtime: runtime)); }
                try {
                    return new CommandResult(Output: $"[{Verb}: {string.Join(
                        separator: ", ",
                        values: runtime.Client(principal: principal).Discover().Select(selector: static operation => operation.Name)
                    )}]");
                } catch (UnauthorizedAccessException) { return CommandResult.Error(output: $"[{Verb}: no service grants for this principal]"); }
            },
            name: Verb
        );
    }
}
