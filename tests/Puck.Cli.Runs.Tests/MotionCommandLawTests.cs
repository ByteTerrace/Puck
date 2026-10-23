using Puck.Cli.Motion;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck motion</c> answers before it boots a World. <c>--list</c> prints every scenario of the
/// shipped scenario file, so that file stays valid; a scenario file that is missing or does not parse, a scenario the file
/// lacks and an output directory whose path holds a space are refused by name with exit 2.
/// </summary>
public sealed class MotionCommandLawTests : IDisposable {
    private readonly DirectoryInfo m_directory = Directory.CreateTempSubdirectory(prefix: "puck-motion-command-");

    private static (int ExitCode, string Output, string Error) Invoke(params string[] arguments) => ConsoleCapture.RunSplit(run: () => MotionCommand.Create().Parse(args: arguments).Invoke());

    public void Dispose() => m_directory.Delete(recursive: true);
    [Fact]
    public void ListNamesEveryScenarioOfTheShippedFileAndBootsNothing() {
        var (exit, output, _) = Invoke("--list");

        Assert.Equal(actual: exit, expected: 0);

        var names = output.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Select(selector: static line => line[..line.IndexOf(value: ':')]).ToArray();

        Assert.Contains(collection: names, expected: "walk-start");
        Assert.Contains(collection: names, expected: "pilot-jump");
        Assert.Contains(collection: names, expected: "flight-landing");
        Assert.Equal(expected: names.Length, actual: names.Distinct().Count());
    }
    [Fact]
    public void AMissingOrMalformedScenarioFileIsRefused() {
        var missing = Invoke("--list", "--scenarios", Path.Combine(path1: m_directory.FullName, path2: "none.json"));

        Assert.Equal(actual: missing.ExitCode, expected: 2);
        Assert.Contains(actualString: missing.Error, expectedSubstring: "does not exist");

        var bad = Path.Combine(path1: m_directory.FullName, path2: "bad.json");

        File.WriteAllText(contents: """{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a"}]}""", path: bad);

        var malformed = Invoke("--list", "--scenarios", bad);

        Assert.Equal(actual: malformed.ExitCode, expected: 2);
        Assert.Contains(actualString: malformed.Error, expectedSubstring: "names no capture ticks");
    }
    [Fact]
    public void AScenarioTheFileLacksAndAnOutputPathWithASpaceAreRefused() {
        var unknown = Invoke("--scenario", "no-such-scenario");

        Assert.Equal(actual: unknown.ExitCode, expected: 2);
        Assert.Contains(actualString: unknown.Error, expectedSubstring: "has no such scenario");

        var spaced = Invoke("--scenario", "walk-start", "--output", Path.Combine(path1: m_directory.FullName, path2: "a b"));

        Assert.Equal(actual: spaced.ExitCode, expected: 2);
        Assert.Contains(actualString: spaced.Error, expectedSubstring: "holds no space");
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: m_directory.FullName, path2: "a b")));
    }
}
