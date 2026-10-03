using System.Diagnostics;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="CliGit"/> never lets git inherit the caller's standard input, and bounds
/// every call, refusing one that outlives its bound with the git command it ran.</summary>
public sealed class CliGitLawTests {
    // The object id of the empty blob: what git hashes from an empty standard input.
    private const string EmptyBlob = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";

    // A git command that reads standard input until it ends. Inherited from a caller whose own input is an open pipe (a
    // backgrounded shell, an agent harness), it would wait on that pipe for as long as the caller lives; run with
    // `sleep 3600 | Puck.Cli.Tests --filter-class Puck.Cli.Tests.CliGitLawTests` this law is exactly that caller.
    [Fact]
    public void GitReadsAnEmptyClosedInputWhenTheCallSuppliesNone() {
        using var checkout = new GitScratchCheckout();

        var run = CliGit.RunAsync(arguments: ["hash-object", "--stdin"], repository: checkout.Root, timeout: TimeSpan.FromSeconds(seconds: 30)).GetAwaiter().GetResult();

        Assert.Equal(expected: 0, actual: run.ExitCode);
        Assert.Equal(expected: EmptyBlob, actual: run.Stdout.Trim());
        Assert.Equal(expected: "8ab686eafeb1f44702738c8b0f24f2567c36da6d", actual: CliGit.RunAsync(arguments: ["hash-object", "--stdin"], input: "Hello, World!\n", repository: checkout.Root).GetAwaiter().GetResult().Stdout.Trim());
    }
    [Fact]
    public void AGitCallThatOutlivesItsBoundIsKilledAndRefusedWithItsCommand() {
        using var checkout = new GitScratchCheckout();
        var clock = Stopwatch.StartNew();

        // An alias runs through git's shell, so the sleep is git's own child: the bound kills the whole tree.
        var refused = Assert.Throws<TimeoutException>(testCode: () => CliGit.RunAsync(
            arguments: ["-c", "alias.wait=!sleep 30", "wait"],
            repository: checkout.Root,
            timeout: TimeSpan.FromSeconds(seconds: 2)
        ).GetAwaiter().GetResult());

        Assert.True(condition: (clock.Elapsed < TimeSpan.FromSeconds(seconds: 20)), userMessage: clock.Elapsed.ToString());
        Assert.Contains(expectedSubstring: $"git -C {checkout.Root} -c alias.wait=!sleep 30 wait", actualString: refused.Message);
        Assert.Contains(expectedSubstring: "did not exit within 00:00:02", actualString: refused.Message);
    }
}
