using Puck.Cli.Parity;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Proves that a world whose <c>puck parity</c> capture schedule cannot be read is a named refusal rather than an
/// exception.
/// </summary>
public sealed class ParityBindingReferenceLawTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new(bestEffortDelete: true, prefix: "puck-cli-tests-parity-reference-");

    [Fact]
    public void AMalformedWorldIsRefusedByNameRatherThanThrown() {
        var path = Path.Combine(
            path1: m_directory.RootPath,
            path2: "malformed.world.json"
        );

        File.WriteAllText(
            contents: "{ \"captures\": ",
            path: path
        );

        Assert.False(condition: ParityCommand.TryReadSchedule(
            error: out var error,
            reconstructionTick: out _,
            waitTick: out _,
            worldPath: path
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "could not be read for its capture schedule"
        );
    }
    public void Dispose() => m_directory.Dispose();
}
