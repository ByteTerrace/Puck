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

        Assert.Equal(expected: 0, actual: exit);

        var names = output.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries).Select(selector: static line => line[..line.IndexOf(value: ':')]).ToArray();

        Assert.Contains(expected: "walk-start", collection: names);
        Assert.Contains(expected: "pilot-jump", collection: names);
        Assert.Contains(expected: "flight-landing", collection: names);
        Assert.Equal(expected: names.Length, actual: names.Distinct().Count());
    }
    [Fact]
    public void AMissingOrMalformedScenarioFileIsRefused() {
        var missing = Invoke("--list", "--scenarios", Path.Combine(path1: m_directory.FullName, path2: "none.json"));

        Assert.Equal(expected: 2, actual: missing.ExitCode);
        Assert.Contains(expectedSubstring: "does not exist", actualString: missing.Error);

        var bad = Path.Combine(path1: m_directory.FullName, path2: "bad.json");

        File.WriteAllText(contents: """{"schema":"puck.motion.scenarios.v1","scenarios":[{"name":"a"}]}""", path: bad);

        var malformed = Invoke("--list", "--scenarios", bad);

        Assert.Equal(expected: 2, actual: malformed.ExitCode);
        Assert.Contains(expectedSubstring: "names no capture ticks", actualString: malformed.Error);
    }
    [Fact]
    public void AScenarioTheFileLacksAndAnOutputPathWithASpaceAreRefused() {
        var unknown = Invoke("--scenario", "no-such-scenario");

        Assert.Equal(expected: 2, actual: unknown.ExitCode);
        Assert.Contains(expectedSubstring: "has no such scenario", actualString: unknown.Error);

        var spaced = Invoke("--scenario", "walk-start", "--output", Path.Combine(path1: m_directory.FullName, path2: "a b"));

        Assert.Equal(expected: 2, actual: spaced.ExitCode);
        Assert.Contains(expectedSubstring: "holds no space", actualString: spaced.Error);
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: m_directory.FullName, path2: "a b")));
    }
}
