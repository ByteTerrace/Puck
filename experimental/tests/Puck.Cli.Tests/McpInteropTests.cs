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

    // Law: a real World launched with --control is attachable with no console line typed, and a console answer longer
    // than the transport once carried (the whole help listing) arrives through the real adapter completed and whole.
    // help narrows by prefix and lists names alone, and world.counters answers in full.
    [Fact]
    public async Task AWorldLaunchedWithControlAnswersHelpAndCountersInFull() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory();
        var world = new ProcessStartInfo(fileName: "dotnet") { CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), UseShellExecute = false, WorkingDirectory = directory.RootPath };

        foreach (var argument in new[] {
            Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: "src/Puck.World/bin/Release/net10.0/Puck.World.dll"),
            "--world", Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: "tests/Puck.World.Verdicts/phase-fixture.puck"),
            "--headless", "true",
            "--exit-after-seconds", "0",
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
                async Task<System.Text.Json.JsonElement> ExecAsync(string command) {
                    var result = await adapter.Client.CallToolAsync(
                        "puck_exec",
                        new Dictionary<string, object?> { ["command"] = command, ["timeoutMs"] = 120000 },
                        cancellationToken: Token
                    );

                    return result.StructuredContent!.Value;
                }

                var help = await ExecAsync(command: "help");
                var helpOutput = help.GetProperty(propertyName: "output").GetString()!;

                Assert.Equal("completed", help.GetProperty(propertyName: "status").GetString());
                Assert.False(condition: help.GetProperty(propertyName: "truncated").GetBoolean());
                Assert.True(condition: (helpOutput.Length > (64 * 1024)), userMessage: $"help answered {helpOutput.Length} characters");
                Assert.Contains(actualString: helpOutput, expectedSubstring: "\nworld.counters - ");

                var family = await ExecAsync(command: "help world.");

                Assert.Equal("completed", family.GetProperty(propertyName: "status").GetString());
                Assert.All(
                    family.GetProperty(propertyName: "output").GetString()!.Split(separator: '\n'),
                    line => Assert.StartsWith(actualString: line, expectedStartString: "world.")
                );

                var names = await ExecAsync(command: "help --names");

                Assert.Equal("completed", names.GetProperty(propertyName: "status").GetString());
                Assert.Contains("world.counters", names.GetProperty(propertyName: "output").GetString()!.Split(separator: '\n'));

                var counters = await ExecAsync(command: "world.counters");

                Assert.Equal("completed", counters.GetProperty(propertyName: "status").GetString());
                Assert.StartsWith("[world.counters: ", counters.GetProperty(propertyName: "output").GetString()!);

                var unmatched = await ExecAsync(command: "help world.sky.unregistered");

                Assert.Equal("refused", unmatched.GetProperty(propertyName: "status").GetString());
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
}
