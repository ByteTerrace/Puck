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
        m_root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-cli-tests-canary-loader-{Guid.NewGuid():N}"
        );

        var canaryRoot = Path.Combine(
            path1: m_root,
            path2: "tests",
            path3: "Puck.World.Canaries"
        );
        var goodDirectory = Path.Combine(
            path1: canaryRoot,
            path2: "good-one"
        );
        var orphanDirectory = Path.Combine(
            path1: canaryRoot,
            path2: "orphan-one"
        );

        Directory.CreateDirectory(path: goodDirectory);
        Directory.CreateDirectory(path: orphanDirectory);

        File.WriteAllText(
            path: Path.Combine(
                path1: goodDirectory,
                path2: "positive-world.json"
            ),
            contents: "{}"
        );
        File.WriteAllText(
            path: Path.Combine(
                path1: goodDirectory,
                path2: "discriminating-world.json"
            ),
            contents: "{}"
        );
        File.WriteAllText(
            path: Path.Combine(
                path1: goodDirectory,
                path2: "positive.script.txt"
            ),
            contents: "wire.errors\n"
        );
        File.WriteAllText(
            path: Path.Combine(
                path1: goodDirectory,
                path2: "discriminating.script.txt"
            ),
            contents: "wire.errors\n"
        );
        // world is resolved against the REPOSITORY root, not the canary directory — script is resolved against the
        // canary directory itself (CanaryManifestLoader.ReadLeg's own basePath split).
        File.WriteAllText(
            path: Path.Combine(
                path1: goodDirectory,
                path2: "canary.json"
            ),
            contents: LegManifest(
                id: "good-one",
                worldPrefix: "tests/Puck.World.Canaries/good-one/"
            )
        );
        // orphanDirectory deliberately gets no canary.json — the "has no canary.json" refusal.
    }

    private static string LegManifest(string id, string worldPrefix, string relaunch = "") =>
        $$"""
        {
          "id": "{{id}}",
          "title": "a synthetic manifest for the loader's own tolerance law",
          "binding": "a synthetic manifest for the loader's own tolerance law",
          "bootShape": "headless",
          "requirements": [],
          "timeoutSeconds": 10,
          "positive": {
            "world": "{{worldPrefix}}positive-world.json",
            "script": "positive.script.txt",
            "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ],{{relaunch}}
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

    [Fact]
    public void ASkippedManifestFailsAWholeSuiteRunAndLeavesANamedSelectionAlone() {
        // Skipping keeps the other proofs running; it must not also make the suite report green with one of its
        // proofs unread. A selection that named its proofs is answered on those alone.
        Assert.Equal(
            expected: 1,
            actual: CanaryCommand.SuiteExit(
                kind: CanaryCommand.CanarySelectionKind.Automatic,
                refusedCount: 1,
                runExit: 0
            )
        );
        Assert.Equal(
            expected: 1,
            actual: CanaryCommand.SuiteExit(
                kind: CanaryCommand.CanarySelectionKind.All,
                refusedCount: 1,
                runExit: 0
            )
        );
        Assert.Equal(
            expected: 1,
            actual: CanaryCommand.SuiteExit(
                kind: CanaryCommand.CanarySelectionKind.Capability,
                refusedCount: 1,
                runExit: 0
            )
        );
        Assert.Equal(
            expected: 0,
            actual: CanaryCommand.SuiteExit(
                kind: CanaryCommand.CanarySelectionKind.Ids,
                refusedCount: 1,
                runExit: 0
            )
        );
        Assert.Equal(
            expected: 0,
            actual: CanaryCommand.SuiteExit(
                kind: CanaryCommand.CanarySelectionKind.All,
                refusedCount: 0,
                runExit: 0
            )
        );
        // A real failure never softens into a skip report.
        Assert.Equal(
            expected: 2,
            actual: CanaryCommand.SuiteExit(
                kind: CanaryCommand.CanarySelectionKind.All,
                refusedCount: 1,
                runExit: 2
            )
        );
    }
    public void Dispose() {
        try {
            Directory.Delete(
                path: m_root,
                recursive: true
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    // A relaunch names the document the first boot writes by its bare file name in the run directory; a path, which
    // could reach outside that directory, is refused by name.
    [InlineData("saved.world.json", true)]
    [InlineData("../saved.world.json", false)]
    [InlineData("nested/saved.world.json", false)]
    [Theory]
    public void ARelaunchNamesItsSavedDocumentByABareFileName(string world, bool loads) {
        var goodDirectory = Path.Combine(
            path1: m_root,
            path2: "tests",
            path3: "Puck.World.Canaries",
            path4: "good-one"
        );

        Directory.Delete(
            path: Path.Combine(
                path1: m_root,
                path2: "tests",
                path3: "Puck.World.Canaries",
                path4: "orphan-one"
            )
        );
        File.WriteAllText(
            contents: LegManifest(
                id: "good-one",
                relaunch: $$"""

                "relaunch": { "world": "{{world}}", "script": "positive.script.txt", "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ] },
                """,
                worldPrefix: "tests/Puck.World.Canaries/good-one/"
            ),
            path: Path.Combine(
                path1: goodDirectory,
                path2: "canary.json"
            )
        );

        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: m_root,
            strict: true
        );

        Assert.Equal(
            actual: loaded,
            expected: loads
        );
        if (loads) {
            Assert.Equal(
                actual: manifests[0].Positive.Relaunch?.WorldFileName,
                expected: world
            );
            Assert.Null(@object: manifests[0].Discriminating.Relaunch);
        } else {
            Assert.Contains(
                actualString: error,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "must be a bare file name"
            );
        }
    }
    // A package is prepared in the run directory under a bare directory name the runner owns, from a canary file, and
    // what it alters is a logical path inside the package; anything that could reach elsewhere is refused by name.
    [InlineData("\"output\": \"tint\"", null)]
    [InlineData("\"output\": \"tint\", \"alter\": \"lib/tint.hlsl\"", null)]
    [InlineData("\"output\": \"../tint\"", "must be a bare directory name")]
    [InlineData("\"output\": \"state\"", "must be a bare directory name")]
    [InlineData("\"output\": \"tint\", \"alter\": \"../tint.hlsl\"", "alter must be a logical path")]
    [InlineData("\"output\": \"tint\", \"alter\": \"/tint.hlsl\"", "alter must be a logical path")]
    [Theory]
    public void APackageIsPreparedUnderABareNameAndAltersOnlyItsOwnFiles(string members, string? refusal) {
        var goodDirectory = Path.Combine(
            path1: m_root,
            path2: "tests",
            path3: "Puck.World.Canaries",
            path4: "good-one"
        );

        Directory.Delete(
            path: Path.Combine(
                path1: m_root,
                path2: "tests",
                path3: "Puck.World.Canaries",
                path4: "orphan-one"
            )
        );
        File.WriteAllText(
            contents: LegManifest(
                id: "good-one",
                relaunch: $$"""

                "package": { "source": "positive.script.txt", {{members}} },
                """,
                worldPrefix: "tests/Puck.World.Canaries/good-one/"
            ),
            path: Path.Combine(
                path1: goodDirectory,
                path2: "canary.json"
            )
        );

        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: m_root,
            strict: true
        );

        Assert.Equal(
            actual: loaded,
            expected: (refusal is null)
        );
        if (refusal is null) {
            Assert.Equal(
                actual: manifests[0].Positive.Package?.OutputName,
                expected: "tint"
            );
            Assert.Null(@object: manifests[0].Discriminating.Package);
        } else {
            Assert.Contains(
                actualString: error,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: refusal
            );
        }
    }
    [Fact]
    public void NonStrictLoadSkipsTheOrphanAndStillLoadsTheGoodManifest() {
        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out var refused,
            repositoryRoot: m_root,
            strict: false
        );

        Assert.True(
            condition: loaded,
            userMessage: error
        );
        Assert.Single(collection: manifests);
        Assert.Equal(
            expected: "good-one",
            actual: manifests[0].Id
        );
        Assert.Single(collection: refused);
        Assert.Equal(
            expected: "orphan-one",
            actual: refused[0].Directory
        );
        Assert.Contains(
            expectedSubstring: "no canary.json",
            actualString: refused[0].Reason,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void StrictLoadRefusesTheWholeDiscoveryOnTheOrphanAlone() {
        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out _,
            refused: out _,
            repositoryRoot: m_root,
            strict: true
        );

        Assert.False(condition: loaded);
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "orphan-one"
        );
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "no canary.json"
        );
    }
}
