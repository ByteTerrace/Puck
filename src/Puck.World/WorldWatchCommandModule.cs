using Puck.Commands;

namespace Puck.World;

/// <summary>Opens the local builder's authenticated text ingress for watched source reloads.</summary>
internal sealed class WorldWatchCommandModule(WorldSourceWatch watch, Func<TextCommandSource> source, Func<InputRouter> router) : ICommandModule, IDisposable {
    private TextCommandSession? m_session;

    public void Dispose() {
        watch.Stop();
        m_session?.Dispose();
        m_session = null;
    }
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.watch",
            bindability: CommandBindability.Unbindable,
            description: "Watches the current .puck source and every file fact its compile read: world.watch on|off. After 150 ms without another change, queues ordinary world.reload under the enabling local console or seat principal. Reload refusals retain the running world, show their diagnostic, and keep watching. A later world.load follows the new source. An already submitted reload may finish after off.",
            handler: (context, args) => {
                if ((args.Count != 1) || !(args.Is(index: 0, value: "on") || args.Is(index: 0, value: "off"))) {
                    return CommandResult.Usage(form: "on|off", verb: "world.watch");
                }
                if (context.Principal.Kind is not (PrincipalKind.Console or PrincipalKind.Seat)) {
                    return CommandResult.Error(output: "[world.watch: requires a local console or seat ingress]");
                }
                Dispose();
                if (args.Is(index: 0, value: "off")) { return new CommandResult(Output: "[world.watch: off]"); }
                TextCommandSession? session = null;

                void Settled(string line, CommandResult result) {
                    if (ReferenceEquals(objA: m_session, objB: session)) {
                        watch.Complete(result: result);
                    }
                }
                try {
                    session = ((context.Principal.Kind == PrincipalKind.Seat)
                        ? source().CreateSeatSession(router(), context.Principal.Index, dueNextTick: true, onSettled: Settled)
                        : source().CreateSession(context.Principal, dueNextTick: true, onSettled: Settled));
                    m_session = session;
                    watch.Start(submitReload: () => session.Enqueue(line: "world.reload"));
                } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
                    Dispose();
                    return CommandResult.Error(output: $"[world.watch: {error.Message}]");
                }
                return new CommandResult(Output: "[world.watch: on]");
            });
    }
}
