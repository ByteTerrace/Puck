using System.Text.Json.Nodes;
using Puck.Cli.Automation;
using Xunit;

namespace Puck.Cli.Tests.Automation;

public sealed class BundleCommandTests {
    [Fact]
    public void ManifestIncludesHiddenFilesAndRejectsTampering() {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-bundle-{Guid.NewGuid():N}");
        var commit = new string(c: 'a', count: 40);

        try {
            Directory.CreateDirectory(path: Path.Combine(path1: directory, path2: "functions/.azurefunctions"));
            File.WriteAllText(path: Path.Combine(path1: directory, path2: "functions/host.json"), contents: "{}");
            File.WriteAllText(path: Path.Combine(path1: directory, path2: "functions/.azurefunctions/worker.json"), contents: "{}");
            Assert.Equal(expected: 0, actual: BundleCommand.Create(directory: directory, commit: commit));
            var path = Path.Combine(path1: directory, path2: "release.json");
            var original = File.ReadAllText(path: path);
            var manifest = JsonNode.Parse(json: original)!;

            Assert.Equal(expected: 2, actual: manifest["files"]!.AsArray().Count);
            manifest["files"]![0]!["path"] = ((string)manifest["files"]![0]!["path"]!).Replace(newChar: '\\', oldChar: '/');
            File.WriteAllText(path: path, contents: manifest.ToJsonString());
            Assert.Equal(expected: 0, actual: BundleCommand.Verify(directory: directory, commit: commit));
            foreach (var candidate in new[] { "../outside", "functions/../host.json", "release.json", "/outside" }) {
                manifest = JsonNode.Parse(json: original)!;
                manifest["files"]![0]!["path"] = candidate;
                File.WriteAllText(path: path, contents: manifest.ToJsonString());
                Assert.Throws<InvalidDataException>(() => BundleCommand.Verify(directory: directory, commit: commit));
            }
            manifest = JsonNode.Parse(json: original)!;
            manifest["files"]![0]!["sha256"] = new string(c: '0', count: 64);
            File.WriteAllText(path: path, contents: manifest.ToJsonString());
            Assert.Throws<InvalidDataException>(() => BundleCommand.Verify(directory: directory, commit: commit));
            File.WriteAllText(contents: original, path: path);
            Assert.Throws<InvalidDataException>(() => BundleCommand.Verify(directory: directory, commit: new string(c: 'b', count: 40)));
            File.WriteAllText(path: Path.Combine(path1: directory, path2: "unlisted"), contents: "extra");
            Assert.Throws<InvalidDataException>(() => BundleCommand.Verify(directory: directory, commit: commit));
        } finally { Directory.Delete(path: directory, recursive: true); }
    }
    [InlineData("nuget")]
    [InlineData("docs")]
    [InlineData("world")]
    [InlineData("wasm")]
    [InlineData("bundle")]
    [Theory]
    public async Task HelpDoesNotRunOperationsAsync(string command) {
        var result = ((command == "nuget") ? await Puck.Cli.NuGet.NuGetCommand.RunAsync(arguments: ["--help"])
            : await AutomationCommand.RunAsync(command: command, args: ["--help"]));

        Assert.Equal(expected: 0, actual: result);
    }
}
