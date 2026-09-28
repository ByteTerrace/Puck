using System.Diagnostics;
using System.Globalization;

using Puck.Testing;

namespace Puck.Hosting.Tests;

public sealed class ChildProcessDrainTests {
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(minutes: 2);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GraceExpiryBeforeReadersResumePreservesTheExitedChildsBufferedOutput() {
        var clock = new VirtualClock();
        using var release = new CancellationTokenSource();

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('tail'); [Console]::Error.Write('error')" })
            : ("/bin/sh", new[] { "-c", "printf tail; printf error >&2" }));
        using var process = ChildProcess.StartRedirected(fileName: executable, arguments: arguments);
        using var output = ChildProcess.OpenOutputReader(reader: process.StandardOutput, release: release.Token);
        using var errors = ChildProcess.OpenOutputReader(reader: process.StandardError, release: release.Token);

        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken: Token).WaitAsync(timeout: HangGuard, cancellationToken: Token);

        // Keep both readers unscheduled until the grace has expired. The child's bytes are still in the kernel.
        var resume = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> ReadAfterResume(StreamReader reader) {
            await resume.Task;
            return await reader.ReadToEndAsync(cancellationToken: Token);
        }
        var drain = ChildProcess.DrainAfterExitAsync(pumps: [ReadAfterResume(reader: output), ReadAfterResume(reader: errors)], release: release, clock: clock, cancellationToken: Token);

        try {
            await clock.ExpireAsync(dueTime: ChildProcess.ExitDrainGrace, pending: drain, ct: Token).WaitAsync(timeout: HangGuard, cancellationToken: Token);
            // CancelAsync can run its continuation on another thread; wait for its observable signal.
            var released = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = release.Token.Register(callback: () => released.TrySetResult());

            await released.Task.WaitAsync(timeout: HangGuard, cancellationToken: Token);
        } finally {
            resume.TrySetResult();
        }
        var result = await drain.WaitAsync(timeout: HangGuard, cancellationToken: Token);

        Assert.Equal(actual: result, expected: new[] { "tail", "error" });
        Assert.Equal(expected: ChildProcess.ExitDrainGrace, actual: clock.Elapsed);
        Assert.Equal(expected: 0, actual: clock.Armed(dueTime: ChildProcess.ExitDrainGrace));
    }
    [Fact]
    public async Task ReleasedLineReadersKeepLongUnterminatedLinesAndDisposeTheirPipes() {
        const int Length = (256 * 1024);
        var clock = new VirtualClock();
        using var release = new CancellationTokenSource();

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", $"$held = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 180' -NoNewWindow -PassThru; [Console]::Out.WriteLine($held.Id); [Console]::Out.Write('o' * {Length}); [Console]::Error.Write('e' * {Length})" })
            : ("/bin/sh", new[] { "-c", $"sleep 180 & printf '%s\\n' $!; head -c {Length} /dev/zero | tr '\\0' o; head -c {Length} /dev/zero | tr '\\0' e >&2" }));
        using var process = ChildProcess.StartRedirected(fileName: executable, arguments: arguments);
        using var output = ChildProcess.OpenOutputReader(reader: process.StandardOutput, release: release.Token);
        using var errors = ChildProcess.OpenOutputReader(reader: process.StandardError, release: release.Token);
        var outputPipe = process.StandardOutput.BaseStream;
        var errorPipe = process.StandardError.BaseStream;

        process.StandardInput.Close();
        var held = await output.ReadLineAsync(cancellationToken: Token).AsTask().WaitAsync(timeout: HangGuard, cancellationToken: Token);
        using var inheritor = Process.GetProcessById(processId: int.Parse(s: held!, provider: CultureInfo.InvariantCulture));
        var stdout = output.ReadLineAsync(cancellationToken: Token).AsTask();
        var stderr = errors.ReadLineAsync(cancellationToken: Token).AsTask();

        try {
            await process.WaitForExitAsync(cancellationToken: Token).WaitAsync(timeout: HangGuard, cancellationToken: Token);
            var drain = ChildProcess.DrainAfterExitAsync(pumps: [stdout, stderr], release: release, clock: clock, cancellationToken: Token);

            await clock.ExpireAsync(dueTime: ChildProcess.ExitDrainGrace, pending: drain, ct: Token).WaitAsync(timeout: HangGuard, cancellationToken: Token);
            var result = await drain.WaitAsync(timeout: HangGuard, cancellationToken: Token);

            Assert.Equal(expected: new string(c: 'o', count: Length), actual: result[0]);
            Assert.Equal(expected: new string(c: 'e', count: Length), actual: result[1]);
            output.Dispose();
            errors.Dispose();
            Assert.False(condition: outputPipe.CanRead);
            Assert.False(condition: errorPipe.CanRead);
        } finally {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            if (!inheritor.HasExited) { inheritor.Kill(); }
            await release.CancelAsync();
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(cancellationToken: Token);
            await inheritor.WaitForExitAsync(cancellationToken: Token);
        }
    }
}
