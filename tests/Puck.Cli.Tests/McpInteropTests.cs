using System.Diagnostics;
using Puck.Hosting;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Xunit;

namespace Puck.Cli.Tests;

public sealed class McpInteropTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Cli => CliPaths.TryGetRepositoryRoot(out var root)
        ? Path.Combine(root, "src/Puck.Cli/bin/Release/net10.0/Puck.Cli.dll")
        : throw new DirectoryNotFoundException("Cannot locate this checkout's Puck.slnx.");

    [Fact]
    public async Task OfficialClientDiscoversExecutesAndReceivesImageFromRealCli() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(() => new FixtureSession());
        for (var reconnect = 0; reconnect < 2; reconnect++) {
            await using var client = await ConnectAsync(host.AttachmentPath, "2026-07-28");
            var tools = await client.ListToolsAsync(cancellationToken: Token);
            Assert.Equal(["puck_capture_frame", "puck_exec"], tools.Select(tool => tool.Name).Order());
            Assert.All(tools, tool => Assert.NotNull(tool.ProtocolTool.OutputSchema));
            var echo = await client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = "echo \tç" }, cancellationToken: Token);
            Assert.NotEqual(true, echo.IsError);
            Assert.Empty(echo.Content.OfType<ImageContentBlock>());
            Assert.Equal("echo \tç", echo.StructuredContent!.Value.GetProperty("output").GetString());
            using var text = System.Text.Json.JsonDocument.Parse(echo.Content.OfType<TextContentBlock>().Single().Text);
            Assert.True(System.Text.Json.JsonElement.DeepEquals(echo.StructuredContent.Value, text.RootElement));
            var image = await client.CallToolAsync("puck_capture_frame", cancellationToken: Token);
            Assert.Single(image.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.Content.OfType<ImageContentBlock>().Single().MimeType);
            Assert.Equal(FixtureSession.Png, image.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray());
            var invalid = await client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = "#ignored" }, cancellationToken: Token);
            Assert.True(invalid.IsError);
            Assert.Equal("refused", invalid.StructuredContent!.Value.GetProperty("status").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, invalid.StructuredContent.Value.GetProperty("requestId").ValueKind);
            var oversized = await client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = new string('ç', 8000) }, cancellationToken: Token);
            Assert.True(oversized.IsError);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, oversized.StructuredContent!.Value.GetProperty("requestId").ValueKind);
            Assert.True((await client.CallToolAsync("puck_capture_frame", new Dictionary<string, object?> { ["path"] = "forbidden.png" }, cancellationToken: Token)).IsError);
            await Assert.ThrowsAsync<McpProtocolException>(async () => await client.CallToolAsync("missing", cancellationToken: Token));
            var error = await client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = "fail" }, cancellationToken: Token);
            Assert.True(error.IsError);
            Assert.Empty(error.Content.OfType<ImageContentBlock>());
        }
    }

    [Fact]
    public async Task EscapedInvalidUnicodeIsInvalidParamsAndDoesNotCloseTheAttachment() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(() => new FixtureSession());
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Cli, "mcp", "--profile", "operator", "--attach", host.AttachmentPath }) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            const string Meta = """
                "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"raw-test","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}
                """;
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"puck_exec","arguments":{"command":"\ud800"},""" + Meta + "}}");
            using var invalid = System.Text.Json.JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(deadline.Token))!);
            Assert.Equal(-32602, invalid.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"puck_exec","arguments":{"command":"alive"},""" + Meta + "}}");
            using var alive = System.Text.Json.JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(deadline.Token))!);
            Assert.Equal("alive", alive.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("output").GetString());
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await errors);
        } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
    }

    [Fact]
    public async Task ExplicitSdkCancellationClosesOnlyTheAttachment() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var entered = new SemaphoreSlim(0);
        using var host = new LocalControlServer(() => new FixtureSession(entered));
        await using var client = await ConnectAsync(host.AttachmentPath, "2026-07-28");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var request = client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = "wait" }, cancellationToken: cancel.Token).AsTask();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5), Token));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await using var fresh = await ConnectAsync(host.AttachmentPath, "2026-07-28");
        Assert.NotEqual(true, (await fresh.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = "alive" }, cancellationToken: Token)).IsError);
    }

    [Fact]
    public async Task TimeoutClosesAttachmentAndDoesNotAutomaticallyRetry() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(() => new FixtureSession());
        await using var client = await ConnectAsync(host.AttachmentPath, "2026-07-28");
        var timeout = await client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = "wait", ["timeoutMs"] = 100 }, cancellationToken: Token);
        Assert.True(timeout.IsError);
        var closed = await client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = "echo" }, cancellationToken: Token);
        Assert.True(closed.IsError);
        Assert.Contains("Attachment closed", closed.Content.OfType<TextContentBlock>().Single().Text);
        await using var again = await ConnectAsync(host.AttachmentPath, "2026-07-28");
        Assert.NotEqual(true, (await again.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["command"] = "echo" }, cancellationToken: Token)).IsError);
    }

    [Fact]
    public async Task OversizedStdioAndEofExitWithoutStoppingHost() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(() => new FixtureSession());
        foreach (var oversized in new[] { false, true }) {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { Cli, "mcp", "--profile", "operator", "--attach", host.AttachmentPath }) { start.ArgumentList.Add(argument); }
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync(Token);
            var output = process.StandardOutput.ReadToEndAsync(Token);
            if (oversized) {
                try { await process.StandardInput.WriteAsync(new string(' ', 65537).AsMemory(), Token); await process.StandardInput.FlushAsync(Token); }
                catch (IOException) { }
            }
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            Assert.Empty(await output);
            Assert.Equal(oversized ? 1 : 0, process.ExitCode);
            if (oversized) { Assert.Contains("64 KiB", await errors); }
            else { Assert.Empty(await errors); }
        }
        using var attach = await LocalControlClient.ConnectAsync(host.AttachmentPath, Token);
        Assert.Equal("alive", (await attach.ExecuteAsync("exec", "alive", cancellationToken: Token)).Output);
    }

    private static Task<McpClient> ConnectAsync(string path, string revision) => McpClient.CreateAsync(new StdioClientTransport(new() {
        Command = "dotnet", Arguments = [Cli, "mcp", "--profile", "operator", "--attach", path], ShutdownTimeout = TimeSpan.FromSeconds(5),
    }), new() { ProtocolVersion = revision }, cancellationToken: Token);

    private sealed class FixtureSession(SemaphoreSlim? entered = null) : IControlSession {
        internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j5p8AAAAASUVORK5CYII=");
        public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            if (request.Command == "wait") { entered?.Release(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return request.Operation == "capture" ? new(request.Id, "completed", "frame", Png: Png) : new(request.Id, request.Command == "fail" ? "refused" : "completed", request.Command!, request.Command == "fail");
        }
        public void Dispose() { }
    }
}
