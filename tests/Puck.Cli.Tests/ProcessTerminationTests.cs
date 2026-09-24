using System.CommandLine;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves that a console interrupt lets a verb finish shutdown work however long it takes, where System.CommandLine's
/// default configuration gives up on it. Two child test hosts each run a verb whose cleanup, after cancellation, is
/// held until this test releases it over a pipe, and each raises a real Ctrl+C on its own private console when told
/// to. The default-configured control is observed returning 130 while its cleanup is still held; only then is the
/// Puck-configured child, interrupted first, released, and it must report its cleanup complete before its invocation
/// returns 0. Every ordering is an event this test observes, never a duration it waits out. A child learns its mode and
/// its pipe from the first line of its standard input.
/// </summary>
public sealed partial class ProcessTerminationTests {
    [Fact]
    public void PuckInvocationGivesTheVerbNoTerminationDeadline() => Assert.Equal(
        actual: PuckRootCommand.Invocation().ProcessTerminationTimeout,
        expected: Timeout.InfiniteTimeSpan
    );
    [Fact]
    public async Task CancellationWaitsForCleanupTheParserDefaultAbandonsAsync() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "The console interrupt is raised with GenerateConsoleCtrlEvent."); return; }
        await using var puck = TerminationChild.Start(mode: "puck");
        await using var control = TerminationChild.Start(mode: "default");

        await Task.WhenAll(
            puck.ExpectAsync(line: "armed"),
            control.ExpectAsync(line: "armed")
        );
        // The Puck child is interrupted strictly before the control, so the control's parser-default timeout, once
        // observed to have expired, has also run its full length since the Puck child's own interrupt.
        await puck.SendAsync(line: "raise");
        await puck.ExpectAsync(line: "cancelled");
        await control.SendAsync(line: "raise");
        await control.ExpectAsync(line: "cancelled");
        await control.ExpectAsync(line: "invoke returned 130");
        await puck.SendAsync(line: "release");
        await puck.ExpectAsync(line: "cleanup complete");
        await puck.ExpectAsync(line: "invoke returned 0");
    }
    // Runs only as the child of CancellationWaitsForCleanupTheParserDefaultAbandonsAsync, which selects it explicitly.
    [Fact(Explicit = true)]
    public async Task TerminationChildAsync() {
        var cue = (Console.In.ReadLine() ?? string.Empty).Split(separator: ' ');

        if (cue.Length != 2) { Assert.Fail(message: "The child expects '<mode> <pipe>' on its standard input."); return; }
        var (mode, pipeName) = (cue[0], cue[1]);
        await using var pipe = new NamedPipeClientStream(
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous,
            pipeName: pipeName,
            serverName: "."
        );

        await pipe.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        var reader = new StreamReader(stream: pipe);
        var writer = new StreamWriter(stream: pipe) { AutoFlush = true };
        // The interrupt cancels the verb's token, and the pipe work it gates must outlive that, so none of it takes a token.
        Task SayAsync(string line) => writer.WriteLineAsync(buffer: line.AsMemory(), cancellationToken: CancellationToken.None);
        var root = new RootCommand(description: "termination fixture");

        root.SetAction(action: async (parseResult, cancellationToken) => {
            try {
                await Task.Delay(
                    cancellationToken: cancellationToken,
                    delay: Timeout.InfiniteTimeSpan
                );
            } catch (OperationCanceledException) { await SayAsync(line: "cancelled"); }
            _ = await reader.ReadLineAsync(cancellationToken: CancellationToken.None);
            await SayAsync(line: "cleanup complete");
            return 0;
        });
        // A parent that ignores Ctrl+C hands that state down; the fixture must receive the interrupt it raises.
        SetConsoleCtrlHandler(
            add: false,
            handler: 0
        );
        var invocation = ((mode == "default")
            ? root.Parse(args: []).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken)
            : PuckRootCommand.InvokeAsync(
                args: [],
                root: root
            )
        );

        // An invocation registers its interrupt handler after starting the action and before first returning to its
        // caller, so an interrupt raised from here on reaches the handler under test and never precedes it.
        await SayAsync(line: "armed");
        _ = await reader.ReadLineAsync(cancellationToken: CancellationToken.None);
        if (!GenerateConsoleCtrlEvent(
            controlEvent: 0,
            processGroupId: 0
        )) { await SayAsync(line: $"GenerateConsoleCtrlEvent failed {Marshal.GetLastWin32Error()}"); }
        var code = await invocation;

        await SayAsync(line: $"invoke returned {code}");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    // One child test host and the duplex pipe it reports its events on and takes its cues from.
    private sealed class TerminationChild : IAsyncDisposable {
        private readonly Task m_connected;
        private readonly Task<string> m_errors;
        private readonly Task<string> m_output;
        private readonly NamedPipeServerStream m_pipe;
        private readonly Process m_process;

        private StreamReader? m_reader;
        private StreamWriter? m_writer;

        public string Mode { get; }

        private TerminationChild(string mode) {
            var name = $"puck-termination-{Guid.NewGuid():N}";
            var info = new ProcessStartInfo(fileName: "dotnet") {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            Mode = mode;
            m_pipe = new NamedPipeServerStream(
                direction: PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                options: PipeOptions.Asynchronous,
                pipeName: name,
                transmissionMode: PipeTransmissionMode.Byte
            );
            m_connected = m_pipe.WaitForConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);
            info.ArgumentList.Add(item: typeof(ProcessTerminationTests).Assembly.Location);
            info.ArgumentList.Add(item: "-method");
            info.ArgumentList.Add(item: $"{typeof(ProcessTerminationTests).FullName}.{nameof(TerminationChildAsync)}");
            info.ArgumentList.Add(item: "-explicit");
            info.ArgumentList.Add(item: "only");
            m_process = (Process.Start(startInfo: info) ?? throw new InvalidOperationException(message: "The child test host did not start."));
            m_process.StandardInput.WriteLine(value: $"{mode} {name}");
            m_process.StandardInput.Flush();
            m_output = m_process.StandardOutput.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
            m_errors = m_process.StandardError.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        public static TerminationChild Start(string mode) => new(mode: mode);
        public async ValueTask DisposeAsync() {
            // A child the verdict no longer needs, or one that never answered, cannot outlive the test.
            try { m_process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await m_process.WaitForExitAsync(cancellationToken: CancellationToken.None);
            m_process.Dispose();
            await m_pipe.DisposeAsync();
        }
        /// <summary>Reads the child's next reported event and requires it to be <paramref name="line"/>.</summary>
        /// <param name="line">The event the protocol expects next.</param>
        /// <returns>A task that completes once the event has been read.</returns>
        public async Task ExpectAsync(string line) {
            var observed = await (await ConnectedAsync()).Reader.ReadLineAsync(cancellationToken: TestContext.Current.CancellationToken);

            if (observed != line) {
                var transcript = ((observed is null)
                    ? $"{Environment.NewLine}{await m_output}{Environment.NewLine}{await m_errors}"
                    : ""
                );

                Assert.Fail(message: $"{Mode}: expected '{line}', observed '{(observed ?? "<end of stream>")}'.{transcript}");
            }
        }
        /// <summary>Sends the child its next cue.</summary>
        /// <param name="line">The cue.</param>
        /// <returns>A task that completes once the cue has been written.</returns>
        public async Task SendAsync(string line) {
            var writer = (await ConnectedAsync()).Writer;

            try {
                await writer.WriteLineAsync(
                    buffer: line.AsMemory(),
                    cancellationToken: TestContext.Current.CancellationToken
                );
            } catch (IOException) {
                // A child that already exited broke the pipe; the events it reported first are still buffered, and
                // the next expectation reads them.
            }
        }

        // The child connects before its first event, so an exit without connecting fails the wait with its own output.
        private async Task<(StreamReader Reader, StreamWriter Writer)> ConnectedAsync() {
            if (await Task.WhenAny(
                task1: m_connected,
                task2: m_process.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken)
            ) != m_connected) {
                Assert.Fail(message: $"{Mode}: the child exited before connecting.{Environment.NewLine}{await m_output}{Environment.NewLine}{await m_errors}");
            }

            await m_connected;
            m_reader ??= new StreamReader(stream: m_pipe);
            m_writer ??= new StreamWriter(stream: m_pipe) { AutoFlush = true };
            return (m_reader, m_writer);
        }
    }
}
