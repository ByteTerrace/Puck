using System.Text.Json;

using Puck.Cli.Official;

using Xunit;

namespace Puck.Cli.Tests.Official;

/// <summary>Builds a real puck.official.v1 tree once, from this checkout's own worlds and the read-only browser-wasm
/// AppBundle in the current checkout, so every test in <see cref="OfficialBuildCommandTests"/> exercises the actual
/// verb end to end rather than a synthetic fixture.</summary>
public sealed class OfficialBuildFixture : IDisposable {
    // Build the browser in this checkout before running the official-content integration tests.
    public static string AppBundlePath {
        get {
            Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root), userMessage: "Cannot locate this checkout's Puck.slnx.");

            return Path.Combine(path1: root!, path2: "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle");
        }
    }

    public const string Channel = "dev";

    public int ExitCode { get; }
    public string OutRoot { get; }
    public string StdErr { get; }
    public string StdOut { get; }

    public OfficialBuildFixture() {
        Assert.True(condition: Directory.Exists(path: AppBundlePath), userMessage: $"the read-only AppBundle at {AppBundlePath} does not exist — build the browser first.");

        OutRoot = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-official-tests-{Guid.NewGuid():n}");
        (ExitCode, StdOut, StdErr) = RunCapturingConsole(run: () => OfficialBuildCommand.Create().Parse(args: [
            "--out", OutRoot,
            "--channel", Channel,
            "--engine", AppBundlePath,
            "--allow-dirty",
        ]).Invoke());
    }

    public void Dispose() {
        try {
            if (Directory.Exists(path: OutRoot)) {
                Directory.Delete(path: OutRoot, recursive: true);
            }
        } catch (IOException) {
            // Best-effort cleanup — a locked file from a still-draining stream never fails the suite.
        }
    }

    internal static (int ExitCode, string StdOut, string StdErr) RunCapturingConsole(Func<int> run) {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outWriter = new StringWriter();
        using var errorWriter = new StringWriter();

        Console.SetOut(newOut: outWriter);
        Console.SetError(newError: errorWriter);

        try {
            var exitCode = run();

            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        } finally {
            Console.SetOut(newOut: originalOut);
            Console.SetError(newError: originalError);
        }
    }
}
/// <summary>Exercises <c>puck official build</c> and <c>puck official verify</c> end to end against a real tree, and
/// <see cref="OfficialBuildCommand.IsDirtyPorcelainOutput"/> as a pure unit in isolation (a dirty-tree refusal
/// cannot be exercised against this checkout's own git status — it is shared, concurrently-mutated state this suite
/// does not own).</summary>
public sealed class OfficialBuildCommandTests(OfficialBuildFixture fixture) : IClassFixture<OfficialBuildFixture> {
    [Fact]
    public void Build_Succeeds() {
        Assert.True(condition: (fixture.ExitCode == 0), userMessage: $"official build exited {fixture.ExitCode}:\nSTDOUT:\n{fixture.StdOut}\nSTDERR:\n{fixture.StdErr}");
    }
    [Fact]
    public void Build_WritesChannelAndBuildsManifests() {
        var channelManifestPath = Path.Combine(path1: fixture.OutRoot, path2: OfficialBuildFixture.Channel, path3: "manifest.json");

        Assert.True(condition: File.Exists(path: channelManifestPath));

        using var document = JsonDocument.Parse(json: File.ReadAllText(path: channelManifestPath));
        var root = document.RootElement;

        Assert.Equal(expected: "puck.official.v1", actual: root.GetProperty(propertyName: "schema").GetString());
        Assert.Equal(expected: "dev", actual: root.GetProperty(propertyName: "channel").GetString());

        var commit = root.GetProperty(propertyName: "build").GetProperty(propertyName: "commit").GetString()!;
        var buildsManifestPath = Path.Combine(path1: fixture.OutRoot, path2: "builds", path3: commit, path4: "manifest.json");

        Assert.True(condition: File.Exists(path: buildsManifestPath));
        Assert.Equal(expected: File.ReadAllBytes(path: channelManifestPath), actual: File.ReadAllBytes(path: buildsManifestPath));

        var composed = root.GetProperty(propertyName: "composed");

        Assert.True(condition: (composed.GetArrayLength() > 0));
        Assert.Equal(expected: "puck", actual: composed[0].GetProperty(propertyName: "documentId").GetString());
        Assert.NotNull(@object: composed[0].GetProperty(propertyName: "identity").GetString());

        var engineFiles = root.GetProperty(propertyName: "engine").GetProperty(propertyName: "files");

        Assert.True(condition: (engineFiles.GetArrayLength() > 0));
    }
    [Fact]
    public void Verify_PassesOnTheFreshTree() {
        var (exitCode, stdOut, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialVerifyCommand.Create().Parse(args: [
            "--base", fixture.OutRoot,
            "--channel", OfficialBuildFixture.Channel,
        ]).Invoke());

        Assert.True(condition: (exitCode == 0), userMessage: $"official verify exited {exitCode}:\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}");
    }
    [Fact]
    public void SecondBuild_RewritesNoObjectsAndReproducesTheSameManifestBytes() {
        var objectPaths = Directory.EnumerateFiles(path: Path.Combine(path1: fixture.OutRoot, path2: "objects"), searchOption: SearchOption.AllDirectories, searchPattern: "*").ToList();
        var mtimesBefore = objectPaths.ToDictionary(elementSelector: File.GetLastWriteTimeUtc, keySelector: path => path);
        var channelManifestPath = Path.Combine(path1: fixture.OutRoot, path2: OfficialBuildFixture.Channel, path3: "manifest.json");
        var bytesBefore = File.ReadAllBytes(path: channelManifestPath);

        var (exitCode, stdOut, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialBuildCommand.Create().Parse(args: [
            "--out", fixture.OutRoot,
            "--channel", OfficialBuildFixture.Channel,
            "--engine", OfficialBuildFixture.AppBundlePath,
            "--allow-dirty",
        ]).Invoke());

        Assert.True(condition: (exitCode == 0), userMessage: $"official build exited {exitCode}:\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}");

        foreach (var path in objectPaths) {
            Assert.Equal(expected: mtimesBefore[path], actual: File.GetLastWriteTimeUtc(path: path));
        }

        Assert.Equal(expected: bytesBefore, actual: File.ReadAllBytes(path: channelManifestPath));
    }
    [Fact]
    public void TwoIndependentBuilds_ProduceByteIdenticalCommitManifest() {
        var secondRoot = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-official-tests-{Guid.NewGuid():n}");

        try {
            var (exitCode, stdOut, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialBuildCommand.Create().Parse(args: [
                "--out", secondRoot,
                "--channel", OfficialBuildFixture.Channel,
                "--engine", OfficialBuildFixture.AppBundlePath,
                "--allow-dirty",
            ]).Invoke());

            Assert.True(condition: (exitCode == 0), userMessage: $"official build exited {exitCode}:\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}");

            using var firstManifest = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(path1: fixture.OutRoot, path2: OfficialBuildFixture.Channel, path3: "manifest.json")));
            var commit = firstManifest.RootElement.GetProperty(propertyName: "build").GetProperty(propertyName: "commit").GetString()!;
            var firstBuildsManifest = Path.Combine(path1: fixture.OutRoot, path2: "builds", path3: commit, path4: "manifest.json");
            var secondBuildsManifest = Path.Combine(path1: secondRoot, path2: "builds", path3: commit, path4: "manifest.json");

            Assert.Equal(expected: File.ReadAllBytes(path: firstBuildsManifest), actual: File.ReadAllBytes(path: secondBuildsManifest));
        } finally {
            if (Directory.Exists(path: secondRoot)) {
                Directory.Delete(path: secondRoot, recursive: true);
            }
        }
    }
    [Fact]
    public void TamperedObject_MakesVerifyFailNamingIt() {
        using var manifestDocument = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(path1: fixture.OutRoot, path2: OfficialBuildFixture.Channel, path3: "manifest.json")));
        var relativeObjectPath = manifestDocument.RootElement.GetProperty(propertyName: "worldSchemaBundle").GetProperty(propertyName: "path").GetString()!;
        var fullObjectPath = Path.Combine(path1: fixture.OutRoot, path2: relativeObjectPath);
        var original = File.ReadAllBytes(path: fullObjectPath);

        try {
            File.WriteAllBytes(bytes: [.. original, ((byte)'!')], path: fullObjectPath);

            var (exitCode, _, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialVerifyCommand.Create().Parse(args: [
                "--base", fixture.OutRoot,
                "--channel", OfficialBuildFixture.Channel,
            ]).Invoke());

            Assert.Equal(actual: exitCode, expected: 1);
            Assert.Contains(actualString: stdErr, expectedSubstring: relativeObjectPath);
        } finally {
            File.WriteAllBytes(bytes: original, path: fullObjectPath);
        }
    }
    [InlineData("", false)]
    [InlineData("   \n  ", false)]
    [InlineData(" M src/Puck.Cli/Program.cs\n", true)]
    [InlineData("?? new-file.txt\n", true)]
    [Theory]
    public void IsDirtyPorcelainOutput_ReadsGitPorcelainCorrectly(string porcelain, bool expectedDirty) {
        Assert.Equal(expected: expectedDirty, actual: OfficialBuildCommand.IsDirtyPorcelainOutput(porcelainStdout: porcelain));
    }
}
