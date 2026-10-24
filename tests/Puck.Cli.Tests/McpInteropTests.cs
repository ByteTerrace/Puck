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

    // The harness launcher leaves the adapter reading a real, open MCP stdin pipe. A different checkout forces the
    // Git query even on CI with no linked worktrees; a foreign-checkout refusal proves Git finished without booting.
    [Fact]
    public async Task HookLaunchListsWorktreesWhileMcpInputRemainsOpen() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "The session launcher uses PowerShell on Windows."); return; }
        using var directory = new TemporaryDirectory();
        await using var adapter = await ConnectAsync(attach: "latest", directory: directory, revision: "2025-11-25", hook: true);
        var result = await adapter.Client.CallToolAsync(
            "puck_world_start",
            new Dictionary<string, object?> { ["checkout"] = directory.RootPath, ["windowed"] = false },
            cancellationToken: Token
        );

        Assert.True(condition: result.IsError);
        Assert.Contains(expectedSubstring: "is not a checkout of this repository", actualString: result.StructuredContent!.Value.GetProperty(propertyName: "output").GetString());
        Assert.Equal(expected: System.Text.Json.JsonValueKind.Null, actual: result.StructuredContent.Value.GetProperty(propertyName: "processId").ValueKind);
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task WorktreeDiscoveryReportsStartFailureOrGitsExitAndStderr(bool failedStart) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows process interop is required."); return; }
        using var directory = new TemporaryDirectory();
        await using var adapter = await ConnectAsync(
            attach: "latest",
            directory: directory,
            revision: "2025-11-25",
            configure: start => {
                if (failedStart) { start.Environment["PATH"] = directory.RootPath; } else { start.Environment["GIT_CONFIG_COUNT"] = "invalid"; }
            }
        );
        var result = await adapter.Client.CallToolAsync(
            "puck_world_start",
            new Dictionary<string, object?> { ["checkout"] = directory.RootPath },
            cancellationToken: Token
        );
        var output = result.StructuredContent!.Value.GetProperty(propertyName: "output").GetString();

        Assert.True(condition: result.IsError);
        if (failedStart) {
            Assert.Contains(actualString: output, expectedSubstring: "git failed to start");
        } else {
            Assert.Contains(actualString: output, expectedSubstring: "(exit 128)");
            Assert.Contains(actualString: output, expectedSubstring: "bogus count in GIT_CONFIG_COUNT");
        }
    }
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
            ["puck_capture_frame", "puck_exec", "puck_state_vector_write", "puck_world_start", "puck_world_stop", "puck_worlds"],
            tools.Select(selector: tool => tool.Name).Order()
        );
        Assert.All(
            tools,
            tool => Assert.NotNull(value: tool.ProtocolTool.OutputSchema)
        );
        Assert.All(
            tools.Where(predicate: tool => (tool.Name is not ("puck_worlds" or "puck_world_start" or "puck_world_stop"))),
            tool => Assert.True(condition: tool.ProtocolTool.InputSchema.GetProperty(propertyName: "properties").TryGetProperty(propertyName: "world", value: out _))
        );
        var worlds = (await client.CallToolAsync(
            "puck_worlds",
            cancellationToken: Token
        )).StructuredContent!.Value.GetProperty(propertyName: "worlds");

        Assert.Equal(
            expected: host.Info.Id,
            actual: worlds.EnumerateArray().Single().GetProperty(propertyName: "attachmentId").GetString()
        );
        Assert.Equal(
            expected: Environment.ProcessId,
            actual: worlds.EnumerateArray().Single().GetProperty(propertyName: "processId").GetInt32()
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
        Assert.Equal(
            host.Info.Id,
            echo.StructuredContent.Value.GetProperty(propertyName: "world").GetProperty(propertyName: "attachmentId").GetString()
        );
        var named = await client.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["command"] = "echo named", ["world"] = host.Info.Id },
            cancellationToken: Token
        );

        Assert.Equal(
            "echo named",
            named.StructuredContent!.Value.GetProperty(propertyName: "output").GetString()
        );
        var absent = await client.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["command"] = "echo lost", ["world"] = "1" },
            cancellationToken: Token
        );

        Assert.True(condition: absent.IsError);
        Assert.Equal(
            "refused",
            absent.StructuredContent!.Value.GetProperty(propertyName: "status").GetString()
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
        var source = StageWorld(directory: directory, name: "worlds");
        using var host = LaunchWorld(announced: out var announced, directory: directory, source: source, stateName: "state");

        try {
            await announced.WaitAsync(timeout: TimeSpan.FromMinutes(minutes: 5), cancellationToken: Token);
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

                Assert.Equal(actual: refused.Status, expected: "refused");
                Assert.StartsWith(actualString: refused.Output, expectedStartString: "[world.wait: refused (3000 ticks at 30 Hz take 100 s");

                var before = await ExecAsync(command: "world.status", timeoutMs: 30000);

                var baseline = Placements(status: before.Output);

                var text = File.ReadAllText(path: source);
                var edited = text.Replace(comparisonType: StringComparison.Ordinal, newValue: "placements {\n  placement \"mcp-reload\" {\n    prototype: \"cube-1m\"\n    position [0, 0.5, -6]\n  }\n\n", oldValue: "placements {\n");

                Assert.NotEqual(actual: edited, expected: text);
                File.WriteAllText(contents: edited, path: source);

                var reload = await ExecAsync(command: "world.reload", timeoutMs: 120000);

                Assert.Equal(actual: reload.Status, expected: "completed");
                Assert.StartsWith(actualString: reload.Output, expectedStartString: "[world.reload: world.reload applied");
                Assert.Equal(expected: (baseline + 1), actual: Placements(status: (await ExecAsync(command: "world.status", timeoutMs: 30000)).Output));
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
    // Law: two real headless Worlds run beside each other under one adapter. puck_worlds lists both with their process,
    // document and start time, and marks none current before any call attached. A call naming a World by attachment id
    // or by process id reaches that World whichever one the adapter is on, an unnamed call says which World it reached,
    // and naming a World never moves the current one. A killed World leaves its capability file behind; it is no longer
    // listed, and a call naming it is refused by name without falling back to the World that is still running.
    [Fact]
    public async Task ACallNamingAWorldReachesExactlyThatWorld() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory();
        var first = StageWorld(directory: directory, name: "worlds-a");
        var second = StageWorld(directory: directory, name: "worlds-b");
        var text = File.ReadAllText(path: second);
        var edited = text.Replace(comparisonType: StringComparison.Ordinal, newValue: "placements {\n  placement \"mcp-second\" {\n    prototype: \"cube-1m\"\n    position [0, 0.5, -6]\n  }\n\n", oldValue: "placements {\n");

        Assert.NotEqual(actual: edited, expected: text);
        File.WriteAllText(contents: edited, path: second);
        using var a = LaunchWorld(announced: out var announcedA, directory: directory, source: first, stateName: "state-a");
        using var b = LaunchWorld(announced: out var announcedB, directory: directory, source: second, stateName: "state-b");

        try {
            await Task.WhenAll(announcedA, announcedB).WaitAsync(timeout: TimeSpan.FromMinutes(minutes: 5), cancellationToken: Token);
            await using var adapter = await ConnectAsync(
                attach: "latest",
                directory: directory,
                revision: "2026-07-28"
            );

            async Task<System.Text.Json.JsonElement> CallAsync(string tool, string? world = null, string? command = null) {
                var arguments = new Dictionary<string, object?>();

                if (world is not null) { arguments["world"] = world; }
                if (command is not null) { arguments["command"] = command; }

                return (await adapter.Client.CallToolAsync(tool, arguments, cancellationToken: Token)).StructuredContent!.Value.Clone();
            }
            async Task<System.Text.Json.JsonElement[]> ListAsync() => [.. (await CallAsync(tool: "puck_worlds")).GetProperty(propertyName: "worlds").EnumerateArray()];
            async Task<(string Status, string Output, System.Text.Json.JsonElement World)> StatusAsync(string? world) {
                var result = await CallAsync(command: "world.status", tool: "puck_exec", world: world);

                return (result.GetProperty(propertyName: "status").GetString()!, result.GetProperty(propertyName: "output").GetString()!, result.GetProperty(propertyName: "world"));
            }

            var listed = await ListAsync();

            Assert.Equal(
                expected: new[] { a.Id, b.Id }.Order(),
                actual: listed.Select(selector: world => world.GetProperty(propertyName: "processId").GetInt32()).Order()
            );
            Assert.All(collection: listed, action: world => Assert.False(condition: world.GetProperty(propertyName: "current").GetBoolean()));
            Assert.All(collection: listed, action: world => Assert.True(condition: (DateTimeOffset.Parse(input: world.GetProperty(propertyName: "startedUtc").GetString()!, formatProvider: System.Globalization.CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow)));

            var idOf = listed.ToDictionary(keySelector: world => world.GetProperty(propertyName: "processId").GetInt32(), elementSelector: world => world.GetProperty(propertyName: "attachmentId").GetString()!);

            Assert.Equal(
                expected: Path.GetFullPath(path: first),
                actual: listed.Single(predicate: world => (world.GetProperty(propertyName: "processId").GetInt32() == a.Id)).GetProperty(propertyName: "document").GetString(),
                ignoreCase: true
            );
            Assert.Equal(
                expected: Path.GetFullPath(path: second),
                actual: listed.Single(predicate: world => (world.GetProperty(propertyName: "processId").GetInt32() == b.Id)).GetProperty(propertyName: "document").GetString(),
                ignoreCase: true
            );

            // The second World's document holds one more placement than the first's, which tells them apart.
            var baseline = Placements(status: (await StatusAsync(world: idOf[a.Id])).Output);

            foreach (var (selector, process, placements) in new[] {
                (idOf[a.Id], a.Id, baseline),
                (b.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), b.Id, (baseline + 1)),
                (a.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), a.Id, baseline),
                (idOf[b.Id], b.Id, (baseline + 1)),
            }) {
                var named = await StatusAsync(world: selector);

                Assert.True(condition: ((named.Status == "completed") && (Placements(status: named.Output) == placements)), userMessage: $"{selector}: {named.Status}: {named.Output}");
                Assert.Equal(expected: process, actual: named.World.GetProperty(propertyName: "processId").GetInt32());
                Assert.Equal(expected: idOf[process], actual: named.World.GetProperty(propertyName: "attachmentId").GetString());
            }

            var unnamed = await StatusAsync(world: null);
            var current = unnamed.World.GetProperty(propertyName: "processId").GetInt32();

            Assert.Equal(expected: ((current == a.Id) ? baseline : (baseline + 1)), actual: Placements(status: unnamed.Output));
            Assert.Equal(
                expected: [current],
                actual: (await ListAsync()).Where(predicate: world => world.GetProperty(propertyName: "current").GetBoolean()).Select(selector: world => world.GetProperty(propertyName: "processId").GetInt32())
            );

            var other = ((current == a.Id) ? b.Id : a.Id);

            Assert.Equal(expected: other, actual: (await StatusAsync(world: other.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))).World.GetProperty(propertyName: "processId").GetInt32());
            Assert.Equal(expected: current, actual: (await StatusAsync(world: null)).World.GetProperty(propertyName: "processId").GetInt32());

            // A killed World leaves its capability file behind.
            b.Kill(entireProcessTree: true);
            await b.WaitForExitAsync(cancellationToken: Token).WaitAsync(HangGuard, Token);
            Assert.NotEmpty(collection: Directory.GetFiles(path: directory.RootPath, searchPattern: "puck-control-*.json"));
            Assert.Equal(expected: [a.Id], actual: (await ListAsync()).Select(selector: world => world.GetProperty(propertyName: "processId").GetInt32()));

            foreach (var gone in new[] { idOf[b.Id], b.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) }) {
                var refused = await StatusAsync(world: gone);

                Assert.Equal(actual: refused.Status, expected: "refused");
                Assert.Equal(expected: System.Text.Json.JsonValueKind.Null, actual: refused.World.ValueKind);
                Assert.Contains(actualString: refused.Output, expectedSubstring: $"No running World matches '{gone}'");
            }

            var survivor = await StatusAsync(world: idOf[a.Id]);

            Assert.Equal(actual: survivor.Status, expected: "completed");
            Assert.Equal(expected: a.Id, actual: survivor.World.GetProperty(propertyName: "processId").GetInt32());
            Assert.Equal(expected: a.Id, actual: (await StatusAsync(world: null)).World.GetProperty(propertyName: "processId").GetInt32());
        } finally {
            foreach (var process in new[] { a, b }) {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            }
        }
    }
    // Law: puck_world_start boots the checkout's debug world headless from its Release build and answers once the World
    // accepts an attachment; host.log reads that World's stderr, its boot line saying no device is open and the line a
    // world.at pin wrote, anchored to the tick it ran at; puck_world_stop quits it and reports its exit. A World the
    // adapter did not start is listed as not started, its stop is refused and it keeps answering; a document outside
    // the checkout's worlds/ and a checkout outside the repository are refused before anything boots.
    [Fact]
    public async Task AStartedWorldAttachesLogsAndStopsWhileAForeignWorldIsNeverStopped() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory();
        using var foreign = LaunchWorld(announced: out var announced, directory: directory, source: StageWorld(directory: directory, name: "worlds"), stateName: "state");
        var startedProcess = 0;

        try {
            await announced.WaitAsync(timeout: TimeSpan.FromMinutes(minutes: 5), cancellationToken: Token);
            await using var adapter = await ConnectAsync(
                attach: "latest",
                directory: directory,
                revision: "2026-07-28"
            );

            async Task<System.Text.Json.JsonElement> CallAsync(string tool, Dictionary<string, object?> arguments) =>
                (await adapter.Client.CallToolAsync(tool, arguments, cancellationToken: Token)).StructuredContent!.Value.Clone();
            async Task<string> ExecAsync(int world, string command, int timeoutMs = 30000) {
                var result = await CallAsync(tool: "puck_exec", arguments: new() { ["command"] = command, ["timeoutMs"] = timeoutMs, ["world"] = world.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) });

                Assert.True(condition: (result.GetProperty(propertyName: "status").GetString() == "completed"), userMessage: $"{command}: {result}");

                return result.GetProperty(propertyName: "output").GetString()!;
            }

            foreach (var refused in new Dictionary<string, object?>[] { new() { ["document"] = "src" }, new() { ["checkout"] = directory.RootPath } }) {
                var refusal = await CallAsync(arguments: refused, tool: "puck_world_start");

                Assert.Equal(expected: "refused", actual: refusal.GetProperty(propertyName: "status").GetString());
                Assert.Equal(expected: System.Text.Json.JsonValueKind.Null, actual: refusal.GetProperty(propertyName: "processId").ValueKind);
            }

            var start = await CallAsync(tool: "puck_world_start", arguments: new() { ["timeoutSeconds"] = 300 });

            Assert.True(condition: (start.GetProperty(propertyName: "status").GetString() == "started"), userMessage: start.ToString());
            startedProcess = start.GetProperty(propertyName: "processId").GetInt32();
            Assert.Equal(expected: startedProcess, actual: start.GetProperty(propertyName: "world").GetProperty(propertyName: "processId").GetInt32());
            Assert.Equal(
                expected: Path.GetFullPath(path: Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: "worlds/debug/debug.puck")),
                actual: start.GetProperty(propertyName: "world").GetProperty(propertyName: "document").GetString(),
                ignoreCase: true
            );

            var listed = (await CallAsync(arguments: [], tool: "puck_worlds")).GetProperty(propertyName: "worlds").EnumerateArray().ToDictionary(
                elementSelector: static world => world.GetProperty(propertyName: "started").GetBoolean(),
                keySelector: static world => world.GetProperty(propertyName: "processId").GetInt32()
            );

            Assert.True(condition: listed[startedProcess]);
            Assert.False(condition: listed[foreign.Id]);

            // A pinned line writes "[world.at] tick <T> ran: …" on stderr right after tick T completes.
            var armed = System.Text.RegularExpressions.Regex.Match(input: await ExecAsync(command: "world.at +30 world.status", world: startedProcess), pattern: @"armed at tick (\d+)");

            Assert.True(condition: armed.Success);
            var tick = armed.Groups[1].Value;

            _ = await ExecAsync(command: $"world.wait until {tick}", timeoutMs: 60000, world: startedProcess);

            var pinned = await ExecAsync(command: "host.log category world.at", world: startedProcess);

            Assert.Matches(actualString: pinned, expectedRegexPattern: $@"(?m)^#\d+ t{tick} err \[world\.at\] tick {tick} ran: world\.status");
            Assert.Contains(expectedSubstring: "err [world.host] physical input devices are off (--devices none)", actualString: await ExecAsync(command: "host.log text \"physical input devices\" stream err", world: startedProcess));

            var foreignStop = await CallAsync(tool: "puck_world_stop", arguments: new() { ["world"] = foreign.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) });

            Assert.Equal(expected: "refused", actual: foreignStop.GetProperty(propertyName: "status").GetString());
            Assert.False(condition: foreign.HasExited);
            _ = await ExecAsync(command: "world.status", world: foreign.Id);

            var stop = await CallAsync(tool: "puck_world_stop", arguments: new() { ["world"] = start.GetProperty(propertyName: "world").GetProperty(propertyName: "attachmentId").GetString() });

            Assert.True(condition: (stop.GetProperty(propertyName: "status").GetString() == "stopped"), userMessage: stop.ToString());
            Assert.Equal(expected: 0, actual: stop.GetProperty(propertyName: "exitCode").GetInt32());
            Assert.False(condition: stop.GetProperty(propertyName: "killed").GetBoolean());
            Assert.False(condition: stop.GetProperty(propertyName: "leaseReleased").GetBoolean());
            Assert.Contains(expectedSubstring: "[world.host] physical input devices are off", actualString: File.ReadAllText(path: stop.GetProperty(propertyName: "stderrLog").GetString()!));
            Assert.DoesNotContain(
                collection: (await CallAsync(arguments: [], tool: "puck_worlds")).GetProperty(propertyName: "worlds").EnumerateArray(),
                filter: world => (world.GetProperty(propertyName: "processId").GetInt32() == startedProcess)
            );
            Assert.Equal(expected: "refused", actual: (await CallAsync(tool: "puck_world_stop", arguments: new() { ["world"] = startedProcess.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) })).GetProperty(propertyName: "status").GetString());
            startedProcess = 0;
        } finally {
            if (!foreign.HasExited) { foreign.Kill(entireProcessTree: true); }
            if (startedProcess != 0) {
                try {
                    using var started = Process.GetProcessById(processId: startedProcess);

                    started.Kill(entireProcessTree: true);
                } catch (ArgumentException) { }
            }
        }
    }

    private static int Placements(string status) => int.Parse(
        provider: System.Globalization.CultureInfo.InvariantCulture,
        s: System.Text.RegularExpressions.Regex.Match(input: status, pattern: @" placements (\d+) ").Groups[1].Value
    );
    // Copies the debug world's package into the directory and returns its entry source's path.
    private static string StageWorld(TemporaryDirectory directory, string name) {
        var worlds = directory.PathOf(name: name);

        CopyTree(
            source: Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: "worlds/debug"),
            target: worlds
        );

        return Path.Combine(path1: worlds, path2: "debug.puck");
    }
    // Starts a headless World from its own build output beside this suite's, publishing its control endpoint in the
    // directory. The task completes when the World announces it, and faults if its standard error closes first.
    private static Process LaunchWorld(out Task announced, TemporaryDirectory directory, string source, string stateName) {
        var output = new DirectoryInfo(path: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var world = new ProcessStartInfo(fileName: "dotnet") { CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), UseShellExecute = false, WorkingDirectory = directory.RootPath };

        foreach (var argument in new[] {
            Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: $"src/Puck.World/bin/{output.Parent!.Name}/{output.Name}/Puck.World.dll"),
            "--world", source,
            "--headless", "true",
            "--devices", "none",
            "--state-dir", directory.PathOf(name: stateName),
            "--control",
        }) { world.ArgumentList.Add(item: argument); }
        world.Environment["TEMP"] = directory.RootPath;
        world.Environment["TMP"] = directory.RootPath;
        var host = Process.Start(startInfo: world)!;
        var control = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new System.Text.StringBuilder();

        host.ErrorDataReceived += (_, line) => {
            if (line.Data is not { } data) { _ = control.TrySetException(exception: new InvalidOperationException(message: $"World closed standard error before announcing control:{Environment.NewLine}{stderr}")); return; }
            lock (stderr) { _ = stderr.AppendLine(value: data); }
            if (data.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.control: operator attachment ")) { _ = control.TrySetResult(); }
        };
        host.OutputDataReceived += static (_, _) => { };
        host.BeginErrorReadLine();
        host.BeginOutputReadLine();
        announced = control.Task;

        return host;
    }
    private static void CopyTree(string source, string target) {
        foreach (var file in Directory.EnumerateFiles(path: source, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var copy = Path.Combine(path1: target, path2: Path.GetRelativePath(path: file, relativeTo: source));

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: copy)!);
            File.Copy(destFileName: copy, sourceFileName: file);
        }
    }
    private static Process Start(string attach, TemporaryDirectory directory, bool hook = false, Action<ProcessStartInfo>? configure = null) {
        // Direct launches run outside any checkout so their revision check reads nothing. The hook uses the session's
        // checkout and launch path, including its copied CLI and inherited MCP streams.
        var start = new ProcessStartInfo(fileName: "dotnet") { CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), UseShellExecute = false, WorkingDirectory = directory.RootPath };

        if (hook) {
            start.FileName = "pwsh";
            start.WorkingDirectory = RepositoryPaths.RequireRoot();
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-File", Path.Combine(path1: start.WorkingDirectory, path2: ".claude/hooks/puck-mcp.ps1") }) { start.ArgumentList.Add(item: argument); }
        } else {
            foreach (var argument in new[] { Cli, "mcp", "--profile", "operator", "--attach", attach }) { start.ArgumentList.Add(item: argument); }
        }
        start.Environment["TEMP"] = directory.RootPath;
        start.Environment["TMP"] = directory.RootPath;
        configure?.Invoke(obj: start);
        return Process.Start(startInfo: start)!;
    }
    private static async Task<OperatorAdapter> ConnectAsync(string attach, TemporaryDirectory directory, string revision, bool hook = false, Action<ProcessStartInfo>? configure = null) {
        var process = Start(
            attach: attach,
            configure: configure,
            directory: directory,
            hook: hook
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
