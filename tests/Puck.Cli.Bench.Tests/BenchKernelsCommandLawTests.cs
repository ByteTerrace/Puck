using Xunit;

namespace Puck.Cli.Bench.Tests;

/// <summary>
/// <c>puck bench kernels</c> declares none of BenchmarkDotNet's grammar: every token after the verb, option-shaped or
/// not, reaches the switcher unchanged through the same parse a real invocation takes, and a token past <c>--</c>
/// reaches it even when it spells puck's own help. The usage line it prints is balanced; the convention law checks
/// that for every verb.
/// </summary>
public sealed class BenchKernelsCommandLawTests {
    private static string[]? Forwarded(params string[] tokens) =>
        CliRoot.Parse(
            args: ["bench", "kernels", .. tokens],
            root: SuiteRoot.Create()
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
}
