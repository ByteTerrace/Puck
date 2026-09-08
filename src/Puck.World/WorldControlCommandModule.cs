using Puck.Commands;
using Puck.Hosting;

namespace Puck.World;

// The module owns the endpoint's lifetime. Text source resolution is deferred to avoid the source -> registry ->
// modules -> source DI cycle; no worker starts until the human explicitly opts in through the console.
internal sealed class WorldControlCommandModule(Func<TextCommandSource> source, WorldRenderProbe? probe) : ICommandModule, IDisposable {
    private LocalControlServer? m_server;

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.control",
            description: "Local trusted Operator attachment: world.control start|stop|status. start prints a private attachment file for puck mcp --profile operator --attach <file>. The endpoint exposes the full Console registry and composed screenshots to this OS user; stop closes attachments while World keeps running.",
            bindability: CommandBindability.Unbindable,
            handler: (context, args) => {
                if (context.Principal != CommandPrincipal.Console) { return CommandResult.Error("Local control requires the host Console principal."); }
                if (args.Count != 1) { return CommandResult.Error("Expected world.control start|stop|status."); }
                if (args.Is(0, "stop")) { Dispose(); return new("[world.control: stopped]"); }
                if (args.Is(0, "start")) {
                    try {
                        m_server ??= new LocalControlServer(() => new ConsoleControlSession(source(), path =>
                            (probe?.Render ?? throw new InvalidOperationException("Capture requires an initialized offscreen or windowed renderer.")).RequestCapture(path)));
                    } catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or System.Net.Sockets.SocketException) {
                        return CommandResult.Error($"[world.control: {error.Message}]");
                    }
                } else if (!args.Is(0, "status")) { return CommandResult.Error("Expected world.control start|stop|status."); }
                return new(m_server is null ? "[world.control: stopped]" : $"[world.control: operator attachment {m_server.AttachmentPath}]");
            });
    }

    public void Dispose() { m_server?.Dispose(); m_server = null; }
}
