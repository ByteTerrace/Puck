using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves <c>CanaryManifestLoader.TryLoadAll</c>'s strict/tolerant split against a synthetic repository tree — no
/// dependency on the real <c>tests/Puck.World.Canaries</c> corpus. One good manifest and one orphan directory (no
/// <c>canary.json</c>) sit side by side: the non-strict (running) shape must skip the orphan, name it, and still
/// load the good manifest; the strict (<c>--list</c>) shape must refuse the whole discovery on the orphan alone.
/// </summary>
public sealed class CanaryManifestLoaderLawTests : IDisposable {
    private readonly string m_root;

    public CanaryManifestLoaderLawTests() {
        m_root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-cli-tests-canary-loader-{Guid.NewGuid():N}");

        var canaryRoot = Path.Combine(path1: m_root, path2: "tests", path3: "Puck.World.Canaries");
        var goodDirectory = Path.Combine(path1: canaryRoot, path2: "good-one");
        var orphanDirectory = Path.Combine(path1: canaryRoot, path2: "orphan-one");

        Directory.CreateDirectory(path: goodDirectory);
        Directory.CreateDirectory(path: orphanDirectory);

        File.WriteAllText(path: Path.Combine(path1: goodDirectory, path2: "positive-world.json"), contents: "{}");
        File.WriteAllText(path: Path.Combine(path1: goodDirectory, path2: "discriminating-world.json"), contents: "{}");
        File.WriteAllText(path: Path.Combine(path1: goodDirectory, path2: "positive.script.txt"), contents: "wire.errors\n");
        File.WriteAllText(path: Path.Combine(path1: goodDirectory, path2: "discriminating.script.txt"), contents: "wire.errors\n");
        // world is resolved against the REPOSITORY root, not the canary directory — script is resolved against the
        // canary directory itself (CanaryManifestLoader.ReadLeg's own basePath split).
        File.WriteAllText(path: Path.Combine(path1: goodDirectory, path2: "canary.json"), contents: LegManifest(id: "good-one", worldPrefix: "tests/Puck.World.Canaries/good-one/"));
        // orphanDirectory deliberately gets no canary.json — the "has no canary.json" refusal.
    }
    public void Dispose() {
        try {
            Directory.Delete(path: m_root, recursive: true);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    [Fact]
    public void NonStrictLoadSkipsTheOrphanAndStillLoadsTheGoodManifest() {
        var loaded = CanaryManifestLoader.TryLoadAll(error: out var error, manifests: out var manifests, refused: out var refused, repositoryRoot: m_root, strict: false);

        Assert.True(condition: loaded, userMessage: error);
        Assert.Single(collection: manifests);
        Assert.Equal(expected: "good-one", actual: manifests[0].Id);
        Assert.Single(collection: refused);
        Assert.Equal(expected: "orphan-one", actual: refused[0].Directory);
        Assert.Contains(expectedSubstring: "no canary.json", actualString: refused[0].Reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void StrictLoadRefusesTheWholeDiscoveryOnTheOrphanAlone() {
        var loaded = CanaryManifestLoader.TryLoadAll(error: out var error, manifests: out _, refused: out _, repositoryRoot: m_root, strict: true);

        Assert.False(condition: loaded);
        Assert.Contains(expectedSubstring: "orphan-one", actualString: error, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "no canary.json", actualString: error, comparisonType: StringComparison.Ordinal);
    }
    private static string LegManifest(string id, string worldPrefix) =>
        $$"""
        {
          "id": "{{id}}",
          "title": "a synthetic manifest for the loader's own tolerance law",
          "binding": "a synthetic manifest for the loader's own tolerance law",
          "bootShape": "headless",
          "requirements": [],
          "seconds": 1,
          "timeoutSeconds": 10,
          "positive": {
            "world": "{{worldPrefix}}positive-world.json",
            "script": "positive.script.txt",
            "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ],
            "expect": [ { "type": "line", "name": "clean-wire", "stream": "stdout", "match": "contains", "text": "[wire.errors: 0 rejected]", "present": true } ]
          },
          "discriminating": {
            "world": "{{worldPrefix}}discriminating-world.json",
            "script": "discriminating.script.txt",
            "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ],
            "expect": [ { "type": "line", "name": "clean-wire", "stream": "stdout", "match": "contains", "text": "[wire.errors: 0 rejected]", "present": true } ]
          }
        }
        """;
}
