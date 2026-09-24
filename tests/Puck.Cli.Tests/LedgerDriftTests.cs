using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Runs <c>puck schema --check</c>, <c>puck registry --check</c>, and <c>puck architecture --check</c> in-process
/// against the checked-in tree, so a generated-ledger drift (a document field added without regenerating the schema
/// or the name registry, or a project declaration changed without regenerating the layering block) fails the unit
/// test suite rather than surfacing only in CI or a manual verb run.
/// </summary>
public sealed class LedgerDriftTests {
    [Fact]
    public void ArchitectureCheckMatchesTheCheckedInLayeringBlock() {
        var (exitCode, output) = ConsoleCapture.Run(run: static () => PuckRootCommand.Invoke(args: ["architecture", "--check"]));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: $"puck architecture --check exited {exitCode}:\n{output}"
        );
    }
    [Fact]
    public void RegistryCheckMatchesTheCheckedInFile() {
        var (exitCode, output) = ConsoleCapture.Run(run: static () => PuckRootCommand.Invoke(args: ["registry", "--check"]));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: $"puck registry --check exited {exitCode}:\n{output}"
        );
    }
    [Fact]
    public void SchemaCheckMatchesTheCheckedInFiles() {
        var (exitCode, output) = ConsoleCapture.Run(run: static () => PuckRootCommand.Invoke(args: ["schema", "--check"]));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: $"puck schema --check exited {exitCode}:\n{output}"
        );
    }
}
