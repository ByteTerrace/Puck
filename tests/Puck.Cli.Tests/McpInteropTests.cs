using System.Diagnostics;
using Puck.Hosting;
using Puck.Testing;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Xunit;

namespace Puck.Cli.Tests;

// The real `puck mcp` process. Only what needs the process lives here: the verb's arguments, stdio, exit codes and
// stderr, and the official client end to end. Attachment and closure laws run in process in McpAdversarialTests,
// where every deadline is event-driven. Each test's host publishes into a private directory that is the child's
// temporary directory, so `--attach latest` can only find that host.
[Collection("MCP process interop")]
public sealed class McpInteropTests {
    // Bounds a wait on the child process; it measures nothing.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(seconds: 30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Cli => CliPaths.Tool;

    // The handshake revisions are what an editor or an agent harness opens with; the last is per-request metadata.
    [InlineData("2025-06-18", false)]
    [InlineData("2025-11-25", false)]
    [InlineData("2026-07-28", true)]
    [Theory]
    public async Task OfficialClientDiscoversExecutesAndReceivesImageFromRealCli(string revision, bool latest) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory();
        using var host = new LocalControlServer(
            clock: new VirtualClock(),
            createSession: () => new FixtureSession(),
            directory: directory.RootPath
        );
        await using var adapter = await ConnectAsync(
            attach: (latest
                ? "latest"
                : host.AttachmentPath),
            directory: directory,
            revision: revision
        );
        var client = adapter.Client;
        var tools = await client.ListToolsAsync(cancellationToken: Token);

        Assert.Equal(
            ["puck_capture_frame", "puck_exec", "puck_state_vector_write"],
            tools.Select(selector: tool => tool.Name).Order()
        );
        Assert.All(
            tools,
            tool => Assert.NotNull(value: tool.ProtocolTool.OutputSchema)
        );
        var echo = await client.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["command"] = "echo \tç" },
            cancellationToken: Token
        );

        Assert.NotEqual(
            true,
            echo.IsError
        );
        Assert.Empty(collection: echo.Content.OfType<ImageContentBlock>());
        Assert.Equal(
            "echo \tç",
            echo.StructuredContent!.Value.GetProperty(propertyName: "output").GetString()
        );
        using var text = System.Text.Json.JsonDocument.Parse(echo.Content.OfType<TextContentBlock>().Single().Text);

        Assert.True(condition: System.Text.Json.JsonElement.DeepEquals(
            element1: echo.StructuredContent.Value,
            element2: text.RootElement
        ));
        var image = await client.CallToolAsync(
            "puck_capture_frame",
            cancellationToken: Token
        );

        Assert.Single(collection: image.Content.OfType<ImageContentBlock>());
        Assert.Equal(
            "image/png",
            image.Content.OfType<ImageContentBlock>().Single().MimeType
        );
        Assert.Equal(
            FixtureSession.Png,
            image.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray()
        );
        var invalid = await client.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["command"] = "#ignored" },
            cancellationToken: Token
        );

        Assert.True(condition: invalid.IsError);
        Assert.Equal(
            "refused",
            invalid.StructuredContent!.Value.GetProperty(propertyName: "status").GetString()
        );
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            invalid.StructuredContent.Value.GetProperty(propertyName: "requestId").ValueKind
        );
        var oversized = await client.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> {
                ["command"] = new string(
                c: 'ç',
                count: 8000
            ),
            },
            cancellationToken: Token
        );

        Assert.True(condition: oversized.IsError);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            oversized.StructuredContent!.Value.GetProperty(propertyName: "requestId").ValueKind
        );
        Assert.True(condition: (await client.CallToolAsync(
            "puck_capture_frame",
            new Dictionary<string, object?> { ["path"] = "forbidden.png" },
            cancellationToken: Token
        )).IsError);
        await Assert.ThrowsAsync<McpProtocolException>(testCode: async () => await client.CallToolAsync(
            "missing",
            cancellationToken: Token
        ));
        var error = await client.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["command"] = "fail" },
            cancellationToken: Token
        );

        Assert.True(condition: error.IsError);
        Assert.Empty(collection: error.Content.OfType<ImageContentBlock>());
    }
    // Law: stdin EOF exits the adapter cleanly and an oversized line exits it with a failure, neither stopping the
    // host, which still serves a new attachment.
    [Fact]
    public async Task OversizedStdioAndEofExitWithoutStoppingHost() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory();
        using var host = new LocalControlServer(
            clock: new VirtualClock(),
            createSession: () => new FixtureSession(),
            directory: directory.RootPath
        );

        foreach (var oversized in new[] { false, true }) {
            using var process = Start(
                attach: host.AttachmentPath,
                directory: directory
            );
            var errors = process.StandardError.ReadToEndAsync(cancellationToken: Token);
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken: Token);

            if (oversized) {
                try {
                    await process.StandardInput.WriteAsync(
                    buffer: new string(
                        c: ' ',
                        count: 65537
                    ).AsMemory(),
                    cancellationToken: Token
                ); await process.StandardInput.FlushAsync(cancellationToken: Token);
                } catch (IOException) { }
            }
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(cancellationToken: Token).WaitAsync(HangGuard, Token); } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            Assert.Empty(value: await output);
            Assert.Equal(
                (oversized
                ? 1
                : 0),
                process.ExitCode
            );
            if (oversized) {
                Assert.Contains(
                "64 KiB",
                await errors
            );
            } else { Assert.Empty(value: await errors); }
        }
        using var attach = await LocalControlClient.ConnectAsync(
            attachmentPath: host.AttachmentPath,
            cancellationToken: Token,
            clock: new VirtualClock()
        );

        Assert.Equal(
            "alive",
            (await attach.ExecuteAsync(
                "exec",
                "alive",
                cancellationToken: Token
            )).Output
        );
    }
    // Law: over one attachment to a real headless World booted from the debug world's .puck source, world.wait holds
    // its own call until its release tick and the next call runs at once, a wait the call's deadline cannot cover is
    // refused while the attachment survives, and world.reload after a source edit answers with its outcome and installs
    // the edit. The World runs from its own build output beside this suite's, on a copy of the source tree.
    [Fact]
    public async Task AWaitHoldsItsOwnCallAndAReloadAnswersOverOneAttachment() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory();
        var root = RepositoryPaths.RequireRoot();
        var worlds = directory.PathOf(name: "worlds");

        CopyTree(
            source: Path.Combine(path1: root, path2: "src/Puck.World/Assets/worlds"),
            target: worlds
        );
        var source = Path.Combine(path1: worlds, path2: "debug.puck");
        var output = new DirectoryInfo(path: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var world = new ProcessStartInfo(fileName: "dotnet") { CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), UseShellExecute = false, WorkingDirectory = directory.RootPath };

        foreach (var argument in new[] {
            Path.Combine(path1: root, path2: $"src/Puck.World/bin/{output.Parent!.Name}/{output.Name}/Puck.World.dll"),
            "--world", source,
            "--headless", "true",
            "--state-dir", directory.PathOf(name: "state"),
            "--control",
        }) { world.ArgumentList.Add(item: argument); }
        world.Environment["TEMP"] = directory.RootPath;
        world.Environment["TMP"] = directory.RootPath;
        using var host = Process.Start(startInfo: world)!;
        var announced = new TaskCompletionSource<string>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new System.Text.StringBuilder();

        host.ErrorDataReceived += (_, line) => {
            if (line.Data is not { } data) { _ = announced.TrySetException(exception: new InvalidOperationException(message: $"World closed standard error before announcing control:{Environment.NewLine}{stderr}")); return; }
            lock (stderr) { _ = stderr.AppendLine(value: data); }
            if (data.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.control: operator attachment ")) { _ = announced.TrySetResult(result: data); }
        };
        host.OutputDataReceived += static (_, _) => { };
        host.BeginErrorReadLine();
        host.BeginOutputReadLine();

        try {
            _ = await announced.Task.WaitAsync(timeout: TimeSpan.FromMinutes(minutes: 5), cancellationToken: Token);
            await using (var adapter = await ConnectAsync(
                attach: "latest",
                directory: directory,
                revision: "2026-07-28"
            )) {
                async Task<(string Status, string Output)> ExecAsync(string command, int timeoutMs) {
                    var result = (await adapter.Client.CallToolAsync(
                        "puck_exec",
                        new Dictionary<string, object?> { ["command"] = command, ["timeoutMs"] = timeoutMs },
                        cancellationToken: Token
                    )).StructuredContent!.Value;

                    return (result.GetProperty(propertyName: "status").GetString()!, result.GetProperty(propertyName: "output").GetString()!);
                }

                var wait = await ExecAsync(command: "world.wait 15", timeoutMs: 30000);
                var released = System.Text.RegularExpressions.Regex.Match(input: wait.Output, pattern: @"^\[world\.wait: 15 ticks from (\d+) — released at tick (\d+)\]$");

                Assert.True(condition: ((wait.Status == "completed") && released.Success), userMessage: $"{wait.Status}: {wait.Output}");
                Assert.True(condition: (ulong.Parse(s: released.Groups[2].Value, provider: System.Globalization.CultureInfo.InvariantCulture) >= (ulong.Parse(s: released.Groups[1].Value, provider: System.Globalization.CultureInfo.InvariantCulture) + 15UL)), userMessage: wait.Output);

                var refused = await ExecAsync(command: "world.wait 3000", timeoutMs: 30000);

                Assert.Equal(expected: "refused", actual: refused.Status);
                Assert.StartsWith(expectedStartString: "[world.wait: refused (3000 ticks at 30 Hz take 100 s", actualString: refused.Output);

                var before = await ExecAsync(command: "world.status", timeoutMs: 30000);

                Assert.Contains(expectedSubstring: " placements 8 ", actualString: before.Output);

                var text = File.ReadAllText(path: source);
                var edited = text.Replace(oldValue: "placements {\n", newValue: "placements {\n  placement \"mcp-reload\" {\n    prototype: \"cube-1m\"\n    position [0, 0.5, -6]\n  }\n\n", comparisonType: StringComparison.Ordinal);

                Assert.NotEqual(expected: text, actual: edited);
                File.WriteAllText(path: source, contents: edited);

                var reload = await ExecAsync(command: "world.reload", timeoutMs: 120000);

                Assert.Equal(expected: "completed", actual: reload.Status);
                Assert.StartsWith(expectedStartString: "[world.reload: world.reload applied", actualString: reload.Output);
                Assert.Contains(expectedSubstring: " placements 9 ", actualString: (await ExecAsync(command: "world.status", timeoutMs: 30000)).Output);
            }
            await host.StandardInput.WriteAsync(buffer: "quit\n".AsMemory(), cancellationToken: Token);
            await host.StandardInput.FlushAsync(cancellationToken: Token);
            host.StandardInput.Close();
            await host.WaitForExitAsync(cancellationToken: Token).WaitAsync(HangGuard, Token);
            Assert.Equal(0, host.ExitCode);
        } finally {
            if (!host.HasExited) { host.Kill(entireProcessTree: true); }
        }
    }
    private static void CopyTree(string source, string target) {
        foreach (var file in Directory.EnumerateFiles(path: source, searchPattern: "*", searchOption: SearchOption.AllDirectories)) {
            var copy = Path.Combine(path1: target, path2: Path.GetRelativePath(relativeTo: source, path: file));

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: copy)!);
            File.Copy(sourceFileName: file, destFileName: copy);
        }
    }
    private static Process Start(string attach, TemporaryDirectory directory) {
        // The adapter runs outside any checkout, so its revision check reads nothing and its standard error is its own.
        var start = new ProcessStartInfo(fileName: "dotnet") { CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), UseShellExecute = false, WorkingDirectory = directory.RootPath };

        foreach (var argument in new[] { Cli, "mcp", "--profile", "operator", "--attach", attach }) { start.ArgumentList.Add(item: argument); }
        start.Environment["TEMP"] = directory.RootPath;
        start.Environment["TMP"] = directory.RootPath;
        return Process.Start(startInfo: start)!;
    }
    private static async Task<OperatorAdapter> ConnectAsync(string attach, TemporaryDirectory directory, string revision) {
        var process = Start(
            attach: attach,
            directory: directory
        );
        var errors = process.StandardError.ReadToEndAsync(cancellationToken: Token);

        try {
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    serverInput: process.StandardInput.BaseStream,
                    serverOutput: process.StandardOutput.BaseStream
                ),
                // The revision is pinned, so the initialize fallback that the discover probe's timeout exists for
                // is refused anyway; the probe would only misreport a slow adapter startup as a handshake-only
                // server. InitializationTimeout still bounds the connect.
                new() { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan, ProtocolVersion = revision },
                cancellationToken: Token
            );

            return new(
                client: client,
                errors: errors,
                process: process
            );
        } catch {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    // The official client over the adapter's stdio, shut down as the MCP stdio transport specifies: close the
    // server's input, then wait for it to exit on its own. StdioClientTransport (ModelContextProtocol 2.2.0) never
    // closes that input, so it always kills the server once ShutdownTimeout expires
    // (https://github.com/modelcontextprotocol/csharp-sdk/issues/1836). Owning the process instead lets every
    // connection prove the adapter's graceful exit.
    private sealed class OperatorAdapter(McpClient client, Task<string> errors, Process process) : IAsyncDisposable {
        public McpClient Client { get; } = client;

        public async ValueTask DisposeAsync() {
            try {
                process.StandardInput.Close();
                try { await process.WaitForExitAsync(cancellationToken: Token).WaitAsync(HangGuard, Token); } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
                await Client.DisposeAsync();
                var stderr = await errors;

                Assert.True(
                    condition: (process.ExitCode == 0),
                    userMessage: $"The adapter did not exit cleanly on EOF (exit code {process.ExitCode}): {stderr}"
                );
            } finally { process.Dispose(); }
        }
    }
    private sealed class FixtureSession : IControlSession {
        internal static readonly byte[] Png = Convert.FromBase64String(s: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j5p8AAAAASUVORK5CYII=");

        public void Dispose() { }
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => Task.FromResult(result: ((request.Operation == "capture")
            ? new ControlResponse(
                request.Id,
                "completed",
                "frame",
                Png: Png
            )
            : new ControlResponse(
                request.Id,
                ((request.Command == "fail")
                ? "refused"
                : "completed"),
                request.Command!,
                (request.Command == "fail")
            )
        ));
    }
}
// Real process startup and bounded loopback authentication must not compete with this
// assembly's CPU-heavy Roslyn/packaging fixtures. The production deadlines stay unchanged.
[CollectionDefinition("MCP process interop", DisableParallelization = true)]
public sealed class McpProcessInteropCollection;
