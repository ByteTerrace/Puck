using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.World;

// The module owns the endpoint's lifetime. Text source resolution is deferred to avoid the source -> registry ->
// modules -> source DI cycle; no worker starts until the human explicitly opts in through the console.
internal sealed class WorldControlCommandModule(Func<TextCommandSource> source, WorldCaptureScheduler captureScheduler, WorldRenderProbe? probe) : ICommandModule, IAsyncDisposable, IDisposable {
    private LocalControlServer? m_server;
    private Task m_draining = Task.CompletedTask;

    // The stop verb only begins the stop, since it runs on the pump the endpoint's sessions wait for; the stopped
    // endpoint's completion is kept for the host's disposal to await.
    public void Dispose() {
        if (m_server is { } server) {
            server.Dispose();
            m_draining = Task.WhenAll(
                m_draining,
                server.Completion
            );
            m_server = null;
        }
    }
    public async ValueTask DisposeAsync() {
        Dispose();
        await m_draining.ConfigureAwait(continueOnCapturedContext: false);
    }
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.control",
            description: "Local trusted Operator attachment: world.control start|stop|status. start prints a private attachment file for puck mcp --profile operator --attach <file>. The endpoint exposes the full Console registry and composed screenshots to this OS user; stop closes attachments while World keeps running.",
            bindability: CommandBindability.Unbindable,
            handler: (context, args) => {
                if (context.Principal != Principal.Console) { return CommandResult.Error(output: "Local control requires the host Console principal."); }
                if (args.Count != 1) { return CommandResult.Error(output: "Expected world.control start|stop|status."); }
                if (args.Is(
                    index: 0,
                    value: "stop"
                )) { Dispose(); return new("[world.control: stopped]"); }
                if (args.Is(
                    index: 0,
                    value: "start"
                )) {
                    try {
                        m_server ??= new LocalControlServer(createSession: () => new ConsoleControlSession(
                            source(),
                            path => {
                                var request = new FrameCaptureRequest(path: path);

                                captureScheduler.ArmUnscheduled(
                                    request: request,
                                    target: (probe?.Root ?? throw new InvalidOperationException(message: "Capture requires an initialized offscreen or windowed renderer."))
                                );

                                return request;
                            }
                        ));
                    } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or NotSupportedException or System.Net.Sockets.SocketException)) {
                        return CommandResult.Error(output: $"[world.control: {error.Message}]");
                    }
                } else if (!args.Is(
                    index: 0,
                    value: "status"
                )) { return CommandResult.Error(output: "Expected world.control start|stop|status."); }
                return new(((m_server is null)
                    ? "[world.control: stopped]"
                    : $"[world.control: operator attachment {m_server.AttachmentPath}]"));
            }
        );
    }
}
