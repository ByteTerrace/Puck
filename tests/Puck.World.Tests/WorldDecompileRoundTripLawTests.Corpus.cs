using Puck.Hosting;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldDecompileRoundTripLawTests {
    [Fact]
    public async Task TheDocumentCorpusIncludesOnlyTrackedUnpairedWorlds() {
        using var directory = new TemporaryDirectory(prefix: "puck-world-roundtrip-corpus-");

        directory.WriteText(name: ".gitignore", text: "artifacts/\n");
        directory.WriteText(name: "world.world.json", text: "{}");
        directory.WriteText(name: "paired.world.json", text: "{}");
        directory.WriteText(name: "paired.puck", text: "");
        directory.WriteText(name: "experimental/old.world.json", text: "{}");
        directory.WriteText(name: "artifacts/old.world.json", text: "{}");
        directory.WriteText(name: "scratch.world.json", text: "{}");
        try {
            foreach (var arguments in new string[][] {
                ["init", "--quiet"],
                ["config", "maintenance.auto", "false"],
                ["config", "gc.auto", "0"],
                ["add", "--", ".gitignore", "world.world.json", "paired.world.json", "paired.puck", "experimental/old.world.json"],
            }) {
                var result = await ChildProcess.RunAsync(
                    arguments: ["-C", directory.RootPath, .. arguments],
                    cancellationToken: TestContext.Current.CancellationToken,
                    fileName: "git",
                    input: string.Empty,
                    timeout: TestLiveness.Bound
                );

                Assert.True(condition: (result.ExitCode == 0), userMessage: result.Stderr);
            }

            Assert.Equal(
                expected: [directory.PathOf(name: "world.world.json").Replace(newChar: '/', oldChar: '\\')],
                actual: Documents(root: directory.RootPath)
            );
        } finally {
            // Git writes its objects read-only; clear that attribute before the directory checks teardown.
            var gitDirectory = directory.PathOf(name: ".git");

            if (Directory.Exists(path: gitDirectory)) {
                foreach (var file in Directory.EnumerateFiles(path: gitDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
                    File.SetAttributes(path: file, fileAttributes: File.GetAttributes(path: file) & ~FileAttributes.ReadOnly);
                }
            }
        }
    }
}
