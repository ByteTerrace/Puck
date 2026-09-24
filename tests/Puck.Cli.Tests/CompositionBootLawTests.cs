using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>Puck.World --world &lt;composition&gt;.puck</c> stages every world the source declares
/// and boots its declared <c>entry world</c>, or the declared world <c>--entry</c> names; an undeclared name, a
/// composition with neither, and an <c>--entry</c> beside a world that is not a composition refuse the boot by name.
/// Each boot runs the real executable headless out of the repository's own Release output.</summary>
public sealed class CompositionBootLawTests : IDisposable {
    // Every Boot call's own leg root, released together when the test instance is (a fresh instance per [Fact],
    // xUnit's default).
    private readonly List<IDisposable> m_scratch = [];

    private CliProcessResult Boot(string world, params string[] extra) {
        var leg = ScheduledWorldBoot.Leg();

        m_scratch.Add(item: leg);

        var legDirectory = leg.PathOf(name: "composition");

        _ = Directory.CreateDirectory(path: legDirectory);

        return CliProcess.RunCaptured(
            arguments: [
                Path.Combine(
                    path1: RepositoryPaths.RequireRoot(),
                    path2: "src/Puck.World/bin/Release/net10.0/Puck.World.dll"
                ),
                "--world", Path.Combine(
                    path1: RepositoryPaths.RequireRoot(),
                    path2: world
                ),
                "--headless", "true",
                "--exit-after-seconds", "0",
                "--state-dir", Path.Combine(
                    path1: legDirectory,
                    path2: "state"
                ),
                .. extra,
            ],
            cancellationToken: TestContext.Current.CancellationToken,
            fileName: "dotnet",
            input: "quit\n",
            timeout: Timeout.InfiniteTimeSpan
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        foreach (var disposable in m_scratch) {
            disposable.Dispose();
        }
    }
    [Fact]
    public void ACompositionBootsItsDeclaredEntry() {
        var run = Boot(world: "worlds/rulepush/rulepush.puck");

        Assert.True(
            condition: (run.ExitCode == 0),
            userMessage: run.Stderr
        );
        Assert.Contains(
            actualString: run.Stderr,
            expectedSubstring: "staged 4 worlds"
        );
        Assert.Contains(
            actualString: run.Stderr,
            expectedSubstring: "entry 'hub'"
        );
    }
    [Fact]
    public void EntryBootsTheDeclaredWorldItNames() {
        var run = Boot("worlds/rulepush/rulepush.puck", "--entry", "hedges");

        Assert.True(
            condition: (run.ExitCode == 0),
            userMessage: run.Stderr
        );
        Assert.Contains(
            actualString: run.Stderr,
            expectedSubstring: "entry 'hedges'"
        );
        Assert.Contains(
            actualString: run.Stderr,
            expectedSubstring: "rulepush.puck (--world)"
        );
    }
    [Fact]
    public void AnUndeclaredEntryIsRefusedNamingTheDeclaredWorlds() {
        var run = Boot("worlds/rulepush/rulepush.puck", "--entry", "garden");

        Assert.Equal(
            actual: run.ExitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: run.Stderr,
            expectedSubstring: "--entry 'garden' names no world"
        );
        Assert.Contains(
            actualString: run.Stderr,
            expectedSubstring: "it declares hub, hedges, rewrite, sink"
        );
    }
    [Fact]
    public void ACompositionWithoutAnEntryIsRefusedByName() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var source = File.ReadAllText(path: Path.Combine(
            path1: RepositoryPaths.RequireRoot(),
            path2: "tests/Puck.World.Verdicts/composition/composition.puck"
        ));

        Assert.Contains(
            actualString: source,
            expectedSubstring: "\nentry world north = "
        );

        _ = directory.WriteText(
            name: "plot.puck",
            text: File.ReadAllText(path: Path.Combine(
                path1: RepositoryPaths.RequireRoot(),
                path2: "tests/Puck.World.Verdicts/composition/plot.puck"
            ))
        );

        // Its root tests are cut away: a composed test without an entry is refused at compile time, before the boot.
        var tests = source.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: "\ntest \""
        );

        Assert.True(condition: (tests > 0));

        var path = directory.WriteText(
            name: "composition.puck",
            text: source[..(tests + 1)].Replace(
                newValue: "\nworld north = ",
                oldValue: "\nentry world north = "
            )
        );
        var run = Boot(world: path);

        Assert.Equal(
            actual: run.ExitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: run.Stderr,
            expectedSubstring: "and none is its entry"
        );
    }
    [Fact]
    public void EntryBesideAWorldThatIsNotACompositionIsRefusedByName() {
        var run = Boot("src/Puck.World/Assets/worlds/games/go.puck", "--entry", "hub");

        Assert.Equal(
            actual: run.ExitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: run.Stderr,
            expectedSubstring: "declares no worlds"
        );
    }
}
