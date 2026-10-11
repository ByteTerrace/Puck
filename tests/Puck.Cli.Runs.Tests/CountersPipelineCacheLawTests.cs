using Puck.Cli.Counters;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>Laws for the driver pipeline cache counters legs start from: a leg's fresh state directory is seeded with
/// the backend's persisted files, and a completed leg's cache replaces them, pruned files included.</summary>
public sealed class CountersPipelineCacheLawTests {
    [Fact]
    public void ALegStartsFromItsBackendsPersistedCacheAndWritesItsOwnBack() {
        var scratch = Directory.CreateTempSubdirectory(prefix: "puck-counters-cache-law-");

        try {
            var root = Path.Combine(path1: scratch.FullName, path2: "persisted");
            var first = Path.Combine(path1: scratch.FullName, path2: "run-1", path3: "state-vulkan");
            var second = Path.Combine(path1: scratch.FullName, path2: "run-2", path3: "state-vulkan");

            Assert.Equal(expected: 0, actual: CountersPipelineCache.Seed(backend: "vulkan", root: root, stateDirectory: first));
            Write(path: Path.Combine(path1: first, path2: "pipeline-cache", path3: "device-a/kept.bin"), text: "kept");
            Write(path: Path.Combine(path1: first, path2: "pipeline-cache", path3: "device-a/pruned.bin"), text: "pruned");
            Write(path: Path.Combine(path1: first, path2: "pipeline-cache", path3: "device-a/partial.bin.tmp"), text: "partial");
            Assert.Equal(expected: 2, actual: CountersPipelineCache.Persist(backend: "vulkan", root: root, stateDirectory: first));

            Assert.Equal(expected: 2, actual: CountersPipelineCache.Seed(backend: "vulkan", root: root, stateDirectory: second));
            Assert.Equal(expected: "kept", actual: File.ReadAllText(path: Path.Combine(path1: second, path2: "pipeline-cache", path3: "device-a/kept.bin")));
            Assert.False(condition: File.Exists(path: Path.Combine(path1: second, path2: "pipeline-cache", path3: "device-a/partial.bin.tmp")));
            Assert.Equal(expected: 0, actual: CountersPipelineCache.Seed(backend: "directx", root: root, stateDirectory: second));

            File.Delete(path: Path.Combine(path1: second, path2: "pipeline-cache", path3: "device-a/pruned.bin"));
            Write(path: Path.Combine(path1: second, path2: "pipeline-cache", path3: "device-a/kept.bin"), text: "rewritten");
            Assert.Equal(expected: 1, actual: CountersPipelineCache.Persist(backend: "vulkan", root: root, stateDirectory: second));
            Assert.Equal(expected: "rewritten", actual: File.ReadAllText(path: Path.Combine(path1: root, path2: "vulkan", path3: "device-a/kept.bin")));
            Assert.False(condition: File.Exists(path: Path.Combine(path1: root, path2: "vulkan", path3: "device-a/pruned.bin")),
                userMessage: "A file the leg's World pruned must leave the persisted cache.");
        } finally {
            scratch.Delete(recursive: true);
        }
    }

    private static void Write(string path, string text) {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        File.WriteAllText(contents: text, path: path);
    }
}
