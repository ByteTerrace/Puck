using Puck.Hosting;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="CliGit"/> never lets git inherit the caller's standard input, and bounds
/// every call, refusing one that outlives its bound with the git command it ran.</summary>
public sealed class CliGitLawTests {
    // The object id of the empty blob: what git hashes from an empty standard input.
    private const string EmptyBlob = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";
    private const string OpenInput = "parent keeps standard input open";

    // The child is a real caller of CliGit whose operating-system stdin stays open throughout the verdict. An
    // ordinary test runner can start with stdin already closed, which would let the inherited-input defect pass.
    [Fact]
    public async Task GitReadsAnEmptyClosedInputWhenTheCallSuppliesNone() {
        using var child = ChildProcess.StartRedirected(fileName: "dotnet", arguments: [
            typeof(CliGitLawTests).Assembly.Location,
            .. CliTestRun.Containing(name: $"{typeof(CliGitLawTests).FullName}.{nameof(OpenInputChildAsync)}"),
            "--explicit", "only", .. CliTestRun.SomeTestExecutes(),
        ]);
        using var input = child.StandardInput;
        using var release = new CancellationTokenSource();
        using var output = ChildProcess.OpenOutputReader(reader: child.StandardOutput, release: release.Token);
        using var errors = ChildProcess.OpenOutputReader(reader: child.StandardError, release: release.Token);
        var pumps = new[] { output.ReadToEndAsync(), errors.ReadToEndAsync() };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);
        var completed = false;

        lifetime.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 30));
        try {
            await input.WriteLineAsync(buffer: OpenInput.AsMemory(), cancellationToken: lifetime.Token);
            await input.FlushAsync(cancellationToken: lifetime.Token);
            await child.WaitForExitAsync(cancellationToken: lifetime.Token);
            completed = true;
        } catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested) {
            // The parent owns the hang guard independently of the git boundary under test.
        } finally {
            try { if (!child.HasExited) { child.Kill(entireProcessTree: true); } } catch (InvalidOperationException) when (child.HasExited) { }
            await child.WaitForExitAsync(cancellationToken: CancellationToken.None);
        }
        var streams = await ChildProcess.DrainAfterExitAsync(
            pumps: pumps, release: release, clock: TimeProvider.System, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: completed, userMessage: $"The child did not finish while its stdin stayed open.\n{streams[0]}\n{streams[1]}");
        Assert.True(condition: (child.ExitCode == 0), userMessage: $"The open-input child failed.\n{streams[0]}\n{streams[1]}");
    }
    // Selected only by the parent law, as ProcessTerminationTests selects its own child test host.
    [Fact(Explicit = true)]
    public async Task OpenInputChildAsync() {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        lifetime.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 10));
        Assert.True(condition: Console.IsInputRedirected);
        Assert.Equal(expected: OpenInput, actual: await Console.In.ReadLineAsync(cancellationToken: lifetime.Token));
        using var checkout = new GitScratchCheckout();

        var run = await CliGit.RunAsync(arguments: ["hash-object", "--stdin"], repository: checkout.Root,
            cancellationToken: lifetime.Token, timeout: TimeSpan.FromSeconds(seconds: 5));

        Assert.Equal(expected: 0, actual: run.ExitCode);
        Assert.Equal(expected: EmptyBlob, actual: run.Stdout.Trim());
        Assert.Equal(expected: EmptyBlob, actual: (await CliGit.CaptureAsync(arguments: ["hash-object", "--stdin"],
            repository: checkout.Root, cancellationToken: lifetime.Token)).Trim());
        Assert.Equal(expected: "8ab686eafeb1f44702738c8b0f24f2567c36da6d", actual: (await CliGit.RunAsync(
            arguments: ["hash-object", "--stdin"], input: "Hello, World!\n", repository: checkout.Root, cancellationToken: lifetime.Token)).Stdout.Trim());
    }
    [Fact]
    public async Task AGitCallThatOutlivesItsBoundIsKilledAndRefusedWithItsCommand() {
        using var checkout = new GitScratchCheckout();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        lifetime.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 10));
        // An alias runs through git's shell, so the sleep is git's own child: the bound kills the whole tree.
        var refused = await Assert.ThrowsAsync<TimeoutException>(testCode: () => CliGit.RunAsync(
            arguments: ["-c", "alias.wait=!sleep 30", "wait"],
            repository: checkout.Root,
            cancellationToken: lifetime.Token,
            timeout: TimeSpan.FromSeconds(seconds: 2)
        ));

        Assert.Contains(expectedSubstring: $"git -C {checkout.Root} -c alias.wait=!sleep 30 wait", actualString: refused.Message);
        Assert.Contains(expectedSubstring: "did not exit within 00:00:02", actualString: refused.Message);
    }
}
