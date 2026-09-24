using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// <c>puck bench kernels</c> declares none of BenchmarkDotNet's grammar: every token after the verb, option-shaped or
/// not, reaches the switcher unchanged through the same parse a real invocation takes, and a token past <c>--</c>
/// reaches it even when it spells puck's own help. The usage line it prints is balanced; the convention law checks
/// that for every verb.
/// </summary>
public sealed class BenchKernelsCommandLawTests {
    private static string[]? Forwarded(params string[] tokens) =>
        PuckRootCommand.Parse(
            args: ["bench", "kernels", .. tokens],
            root: PuckRootCommand.Create(clock: TimeProvider.System)
        )?.GetValue<string[]>(name: "benchmark-arguments");

    [Fact]
    public void EveryTokenReachesTheSwitcherVerbatim() {
        string[][] cases = [
            [],
            ["--filter", "*Norm*", "--job", "short"],
            ["--list", "flat"],
            ["--runtimes", "net10.0", "--hide", "Error", "StdDev"],
        ];

        foreach (var tokens in cases) {
            Assert.Equal(
                actual: Forwarded(tokens: tokens),
                expected: tokens
            );
        }
    }
    [Fact]
    public void HelpPastTheSeparatorIsTheSwitchersOwn() =>
        Assert.Equal(
            actual: Forwarded("--", "--help"),
            expected: ["--help"]
        );
    // An open value slot on an ordinary verb still refuses the option-shaped token the parser would hand it.
    [Fact]
    public void AMisspelledOptionElsewhereIsStillRefused() {
        var (_, _, error) = ConsoleCapture.RunSplit(run: static () => ((PuckRootCommand.Parse(
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
