using System.Diagnostics;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a refused <c>puck world release</c> operation, run as the real <c>puck</c> process,
/// exits refused with a one-line diagnostic naming its reason and no unhandled-exception trace
/// (<c>WorldReleaseRollbackTests</c> holds the rollback rules themselves).</summary>
public sealed class WorldReleaseRefusalLawTests {
    [InlineData("status", "missing")]
    [InlineData("exercise", "Host configuration directory does not exist")]
    [Theory]
    public async Task OperatorRefusalsReportTheReasonWithoutAnUnhandledException(string verb, string reason) {
        using var temporary = new TemporaryDirectory(prefix: "puck-release-refusal-");

        var start = new ProcessStartInfo(fileName: "dotnet") {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = temporary.RootPath,
        };

        foreach (var argument in new[] { CliPaths.Tool, "world", "release", verb, Path.Combine(
            path1: temporary.RootPath,
            path2: "missing"
        ) }) {
            start.ArgumentList.Add(item: argument);
        }
        using var process = Process.Start(startInfo: start)!;
        var token = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken: token);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken: token);

        await process.WaitForExitAsync(cancellationToken: token);
        Assert.Equal(
            CliExit.Refused,
            process.ExitCode
        );
        Assert.Empty(value: await output);
        var diagnostic = await errors;

        Assert.StartsWith(
            actualString: diagnostic,
            expectedStartString: $"puck world release {verb}: "
        );
        Assert.Contains(
            actualString: diagnostic,
            expectedSubstring: reason
        );
        Assert.DoesNotContain(
            actualString: diagnostic,
            expectedSubstring: "Unhandled exception"
        );
        Assert.DoesNotContain(
            actualString: diagnostic,
            expectedSubstring: "   at "
        );
    }
}
