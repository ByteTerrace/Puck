using Puck.Cli.Registry;
using Puck.Cli.Schema;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Runs <c>puck schema --check</c> and <c>puck registry --check</c> in-process against the checked-in tree, so a
/// generated-ledger drift (a document field added without regenerating the schema or the name registry) fails the
/// unit test suite rather than surfacing only in CI or a manual verb run.
/// </summary>
public sealed class LedgerDriftTests {
    [Fact]
    public void SchemaCheckMatchesTheCheckedInFiles() {
        var (exitCode, output) = RunCapturingConsole(run: static () => SchemaCommand.Run(args: ["--check"]));

        Assert.True(condition: (exitCode == 0), userMessage: $"puck schema --check exited {exitCode}:\n{output}");
    }
    [Fact]
    public void RegistryCheckMatchesTheCheckedInFile() {
        var (exitCode, output) = RunCapturingConsole(run: static () => RegistryCommand.Run(args: ["--check"]));

        Assert.True(condition: (exitCode == 0), userMessage: $"puck registry --check exited {exitCode}:\n{output}");
    }
    private static (int ExitCode, string Output) RunCapturingConsole(Func<int> run) {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var writer = new StringWriter();

        Console.SetOut(newOut: writer);
        Console.SetError(newError: writer);

        try {
            return (run(), writer.ToString());
        } finally {
            Console.SetOut(newOut: originalOut);
            Console.SetError(newError: originalError);
        }
    }
}
