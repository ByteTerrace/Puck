using Puck.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Puck.Hosting;

namespace Puck.Cli.Mcp;

internal static class McpCommand {
    internal static async Task<int> RunAsync(string[] args) {
        if (args is not ["--profile", "operator", "--attach", _] && args is not ["--silo", _, "--http", _]) {
            Console.Error.WriteLine("Usage: puck mcp --profile operator --attach <attachment file> | puck mcp --silo <silo.json> --http <configuration.json>");
            return 2;
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try {
            if (args is ["--silo", var silo, "--http", var remote]) {
                var options = await RemoteMcpServer.ReadOptionsAsync(remote, stop.Token).ConfigureAwait(false);
                return await Puck.World.Silo.WorldSiloApplication.RunAsync(["--silo", silo],
                    builder => builder.AddPuckMcp(options, remote, options.Services is null ? null :
                        sp => new AzureMcpHost(sp.GetRequiredService<IControlSessionHost>(), options)), stop.Token).ConfigureAwait(false);
            }
            await OperatorMcpServer.RunAsync(args[3], Console.OpenStandardInput(), Console.OpenStandardOutput(), stop.Token).ConfigureAwait(false);
            return 0;
        } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException or ArgumentException or InvalidOperationException) {
            Console.Error.WriteLine($"puck mcp: {error.Message}");
            return 1;
        } finally { Console.CancelKeyPress -= cancel; }
    }
}
