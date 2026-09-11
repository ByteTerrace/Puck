using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves that a console interrupt lets a verb finish shutdown work longer than System.CommandLine's two-second
/// default: a child test host runs a verb whose cleanup takes three seconds after cancellation, raises a real
/// Ctrl+C on its own private console, and records the order of events. The same child under the parser's default
/// configuration is the control that shows the invocation returning before the cleanup completes.
/// </summary>
public sealed partial class ProcessTerminationTests {
    private const string EvidenceVariable = "PUCK_CLI_TERMINATION_EVIDENCE";
    private const string ModeVariable = "PUCK_CLI_TERMINATION_MODE";

    [Fact]
    public async Task CancellationWaitsForCleanupLongerThanTheParserDefaultAsync() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "The console interrupt is raised with GenerateConsoleCtrlEvent."); return; }
        var puck = await RunChildAsync(mode: "puck");
        var control = await RunChildAsync(mode: "default");

        Assert.Contains(actualString: puck, expectedSubstring: "cleanup complete");
        Assert.True(condition: (puck.IndexOf(comparisonType: StringComparison.Ordinal, value: "cleanup complete") < puck.IndexOf(comparisonType: StringComparison.Ordinal, value: "invoke returned 0")), userMessage: puck);
        Assert.Contains(actualString: control, expectedSubstring: "invoke returned 130");
        Assert.DoesNotContain(actualString: control[..control.IndexOf(comparisonType: StringComparison.Ordinal, value: "invoke returned 130")], expectedSubstring: "cleanup complete");
    }
    [Fact]
    public async Task TerminationChildAsync() {
        var evidence = Environment.GetEnvironmentVariable(variable: EvidenceVariable);

        if (evidence is null) { Assert.Skip(reason: $"Runs only as the child of {nameof(CancellationWaitsForCleanupLongerThanTheParserDefaultAsync)}."); return; }
        var clock = Stopwatch.StartNew();

        void Log(string line) => File.AppendAllText(contents: $"{clock.ElapsedMilliseconds,6} ms {line}\n", path: evidence);
        var root = new RootCommand(description: "termination fixture");

        root.SetAction(action: async (_, cancellationToken) => {
            Log(line: "started");
            try { await Task.Delay(cancellationToken: cancellationToken, delay: Timeout.InfiniteTimeSpan); } catch (OperationCanceledException) { Log(line: "cancelled"); }
            // The cleanup under test must outlive the interrupt, so it deliberately takes no token.
#pragma warning disable xUnit1051
            await Task.Delay(delay: TimeSpan.FromSeconds(seconds: 3));
#pragma warning restore xUnit1051
            Log(line: "cleanup complete");
            return 0;
        });
        // A parent that ignores Ctrl+C hands that state down; the fixture must receive the interrupt it raises.
        SetConsoleCtrlHandler(add: false, handler: 0);
        _ = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: async () => {
            await Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TimeSpan.FromMilliseconds(milliseconds: 500));
            if (!GenerateConsoleCtrlEvent(controlEvent: 0, processGroupId: 0)) { Log(line: $"GenerateConsoleCtrlEvent failed {Marshal.GetLastWin32Error()}"); }
        });
        var code = ((Environment.GetEnvironmentVariable(variable: ModeVariable) == "default")
            ? await root.Parse(args: []).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken)
            : await PuckRootCommand.InvokeAsync(args: [], root: root));

        Log(line: $"invoke returned {code}");
    }

    private static async Task<string> RunChildAsync(string mode) {
        var evidence = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-termination-{mode}-{Guid.NewGuid():N}.txt");
        var info = new ProcessStartInfo(fileName: "dotnet") {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        info.ArgumentList.Add(item: typeof(ProcessTerminationTests).Assembly.Location);
        info.ArgumentList.Add(item: "-method");
        info.ArgumentList.Add(item: $"{typeof(ProcessTerminationTests).FullName}.{nameof(TerminationChildAsync)}");
        info.Environment[EvidenceVariable] = evidence;
        info.Environment[ModeVariable] = mode;
        try {
            using var child = (Process.Start(startInfo: info) ?? throw new InvalidOperationException(message: "The child test host did not start."));
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();

            using (var deadline = new CancellationTokenSource(delay: TimeSpan.FromMinutes(minutes: 2))) { await child.WaitForExitAsync(cancellationToken: deadline.Token); }
            var recorded = (File.Exists(path: evidence) ? File.ReadAllText(path: evidence) : "");

            Assert.True(condition: recorded.Contains(comparisonType: StringComparison.Ordinal, value: "cancelled"), userMessage: $"{mode}: the child never observed the interrupt.\n{recorded}\n{await output}\n{await errors}");
            return recorded;
        } finally { File.Delete(path: evidence); }
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
