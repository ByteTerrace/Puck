using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.World;

// The module owns the endpoint's lifetime. Text source resolution is deferred to avoid the source -> registry ->
// modules -> source DI cycle; no worker starts until the operator explicitly opts in, through the console or the
// --control launch option.
internal sealed class WorldControlCommandModule(Func<TextCommandSource> source, WorldCaptureScheduler captureScheduler, WorldRenderProbe? probe) : ICommandModule, IAsyncDisposable, IDisposable {
    private readonly Lock m_gate = new();

    private LocalControlServer? m_server;

    private Task m_draining = Task.CompletedTask;

    // The stop verb only begins the stop, since it runs on the pump the endpoint's sessions wait for; the stopped
    // endpoint's completion is kept for the host's disposal to await.
    public void Dispose() {
        lock (m_gate) {
            if (m_server is { } server) {
                server.Dispose();
                m_draining = Task.WhenAll(
                    m_draining,
                    server.Completion
                );
                m_server = null;
            }
        }
    }
    public async ValueTask DisposeAsync() {
        Dispose();
        await m_draining.ConfigureAwait(continueOnCapturedContext: false);
    }
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.control",
            description: "Local trusted Operator attachment: world.control start|stop|status. start prints a private attachment file for puck mcp --profile operator --attach <file>, as the --control launch option does at boot. The endpoint exposes the full Console registry and composed screenshots to this OS user; stop closes attachments while World keeps running.",
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
                        _ = Start();
                    } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or NotSupportedException or System.Net.Sockets.SocketException)) {
                        return CommandResult.Error(output: $"[world.control: {error.Message}]");
                    }
                } else if (!args.Is(
                    index: 0,
                    value: "status"
                )) { return CommandResult.Error(output: "Expected world.control start|stop|status."); }
                return new(Describe());
            }
        );
    }

    // Starts the endpoint, or keeps the one already started, and returns its attachment file.
    internal string Start() {
        lock (m_gate) {
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

            return m_server.AttachmentPath;
        }
    }
    // The status line world.control start and status answer, and --control prints at boot.
    internal string Describe() {
        lock (m_gate) {
            return ((m_server is null)
                ? "[world.control: stopped]"
                : $"[world.control: operator attachment {m_server.AttachmentPath}]");
        }
    }
}
/// <summary>The <c>--control</c> launch option: starts the local Operator control endpoint as the host starts, exactly
/// as a first console line <c>world.control start</c> would, and announces its attachment file on standard error beside
/// the other boot lines. An endpoint that cannot start fails the boot.</summary>
internal sealed class WorldControlBootStart(WorldControlCommandModule control) : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) {
        _ = control.Start();
        Console.Error.WriteLine(value: control.Describe());

        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
