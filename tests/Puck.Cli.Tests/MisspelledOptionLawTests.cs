using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Beside a verb that forwards every token to another tool (<c>puck bench kernels</c>, held by
/// <c>BenchKernelsCommandLawTests</c>), an ordinary verb of the composed tool still refuses an option-shaped token the
/// parser would hand an open value slot.</summary>
public sealed class MisspelledOptionLawTests {
    // An open value slot on an ordinary verb still refuses the option-shaped token the parser would hand it.
    [Fact]
    public void AMisspelledOptionElsewhereIsStillRefused() {
        var (_, _, error) = ConsoleCapture.RunSplit(run: static () => ((CliRoot.Parse(
            args: ["format", "--chek"],
            root: PuckRootCommand.Create(clock: TimeProvider.System)
        ) is null)
            ? CliExit.Refused
            : CliExit.Success));

        Assert.Contains(
            actualString: error,
            expectedSubstring: "Unrecognized option '--chek'."
        );
    }
}
