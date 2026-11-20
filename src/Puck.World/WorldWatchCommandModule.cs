using Puck.Commands;

namespace Puck.World;

/// <summary>Opens the local builder's authenticated text ingress for watched source reloads.</summary>
internal sealed class WorldWatchCommandModule(WorldSourceWatch watch, Func<TextCommandSource> source, Func<InputRouter> router) : ICommandModule, IDisposable {
    private static readonly CommandForms WorldWatchForms = CommandForms.Of(verb: "world.watch", "on|off");
    private TextCommandSession? m_session;

    public void Dispose() {
        watch.Stop();
        m_session?.Dispose();
        m_session = null;
    }
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Local,
            name: "world.watch",
            bindability: CommandBindability.Unbindable,
            description: $"Watches the current .puck source and every file fact its compile read: {WorldWatchForms.Spelling}. After 150 ms without another change, queues ordinary world.reload under the enabling local console or seat principal. Reload refusals retain the running world, show their diagnostic, and keep watching. A later world.load follows the new source. An already submitted reload may finish after off.",
            forms: WorldWatchForms,
            handler: (context, args) => {
                if ((args.Count != 1) || !(args.Is(index: 0, value: "on") || args.Is(index: 0, value: "off"))) {
                    return WorldWatchForms.Refuse();
                }
                if (args.Is(index: 0, value: "on")) { return Start(context.Principal); }
                Dispose();
                return new CommandResult(Output: "[world.watch: off]");
            });
    }

    internal CommandResult Start(Principal principal, Puck.Assets.CompileInputLog? bootReads = null) {
        Dispose();
        TextCommandSession? session = null;

        void Settled(string line, CommandResult result) {
            if (ReferenceEquals(m_session, session)) { watch.Complete(result); }
        }
        try {
            session = (principal.Kind == PrincipalKind.Seat)
                ? source().CreateSeatSession(router(), principal.Index, dueNextTick: true, onSettled: Settled)
                : source().CreateSession(principal, dueNextTick: true, onSettled: Settled);
            m_session = session;
            watch.Start(submitReload: () => session.Enqueue(line: "world.reload"), bootReads: bootReads);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            Dispose();
            return CommandResult.Error(output: $"[world.watch: {error.Message}]");
        }
        return new CommandResult(Output: "[world.watch: on]");
    }
}
