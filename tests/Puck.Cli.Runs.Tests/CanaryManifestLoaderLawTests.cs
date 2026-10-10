using Puck.Cli.Canary;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Proves <c>CanaryManifestLoader.TryLoadAll</c>'s strict/tolerant split against a synthetic repository tree — no
/// dependency on the real <c>tests/Puck.World.Canaries</c> corpus. One good manifest and one orphan directory (no
/// <c>canary.json</c>) sit side by side: the non-strict (running) shape must skip the orphan, name it, and still
/// load the good manifest; the strict (<c>--list</c>) shape must refuse the whole discovery on the orphan alone.
/// Headless legs admit up to 90 seconds of simulation work while GPU legs retain their 60-second ceiling.
/// </summary>
public sealed class CanaryManifestLoaderLawTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new(bestEffortDelete: true, prefix: "puck-cli-tests-canary-loader-");

    public CanaryManifestLoaderLawTests() {
        var canaryRoot = Path.Combine(
            path1: m_directory.RootPath,
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

    private static string LegManifest(string id, string worldPrefix, string relaunch = "", string root = "") =>
        $$"""
        {
          "id": "{{id}}",
          "title": "a synthetic manifest for the loader's own tolerance law",
          "binding": "a synthetic manifest for the loader's own tolerance law",
          "bootShape": "headless",
          "requirements": [],
          "timeoutSeconds": 10,{{root}}
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

    [InlineData("", false)]
    [InlineData("\n\"exclusive\": true,", true)]
    [InlineData("\n\"exclusive\": false,", null)]
    [InlineData("\n\"exclusive\": \"yes\",", null)]
    [Theory]
    public void AnExclusiveProofSaysSoAndEveryOtherLeavesTheMemberOut(string member, bool? exclusive) {
        var directory = Path.Combine(path1: m_directory.RootPath, path2: "tests", path3: "Puck.World.Canaries", path4: "good-one");

        File.WriteAllText(path: Path.Combine(path1: directory, path2: "canary.json"), contents: LegManifest(
            id: "good-one", root: member,
            worldPrefix: "tests/Puck.World.Canaries/good-one/"));
        var loaded = CanaryManifestLoader.TryLoadAll(error: out _, manifests: out var manifests,
            refused: out var refused, repositoryRoot: m_directory.RootPath, strict: false);

        Assert.Equal(expected: exclusive.HasValue, actual: loaded);
        if (exclusive is { } expected) {
            Assert.Equal(expected: expected, actual: manifests[0].Exclusive);
        } else {
            Assert.Empty(collection: manifests);
            Assert.Contains(collection: refused, filter: refusal => refusal.Reason.Contains(comparisonType: StringComparison.Ordinal, value: "exclusive must be true when present"));
        }
    }
    [Fact]
    public void ANamedStrictListLoadsOnlyItsManifestAndRefusesUnknownIds() {
        Assert.True(condition: CanaryManifestLoader.TryLoadAll(repositoryRoot: m_directory.RootPath, strict: true,
            only: new HashSet<string>(collection: ["good-one"]), manifests: out var manifests, refused: out var refused, error: out var error), userMessage: error);
        Assert.Equal(expected: "good-one", actual: Assert.Single(collection: manifests).Id);
        Assert.Empty(collection: refused);
        Assert.False(condition: CanaryManifestLoader.TryLoadAll(repositoryRoot: m_directory.RootPath, strict: true,
            only: new HashSet<string>(collection: ["missing"]), manifests: out _, refused: out _, error: out error));
        Assert.Contains(actualString: error, expectedSubstring: "unknown canary id(s): missing");
        Assert.False(condition: CanaryManifestLoader.TryLoadAll(repositoryRoot: m_directory.RootPath, strict: true,
            only: new HashSet<string>(collection: ["orphan-one"]), manifests: out _, refused: out _, error: out error));
        Assert.Contains(actualString: error, expectedSubstring: "has no canary.json");
    }
    [InlineData("title")]
    [InlineData("binding")]
    [Theory]
    public void ANamedStrictListStillRefusesInvalidProse(string field) {
        var path = Path.Combine(path1: m_directory.RootPath, path2: "tests/Puck.World.Canaries/good-one/canary.json");
        var text = File.ReadAllText(path: path);

        File.WriteAllText(path: path, contents: text.Replace(comparisonType: StringComparison.Ordinal, newValue: $"\"{field}\": \"\"", oldValue: $"\"{field}\": \"a synthetic manifest for the loader's own tolerance law\""));
        Assert.False(condition: CanaryManifestLoader.TryLoadAll(repositoryRoot: m_directory.RootPath, strict: true,
            only: new HashSet<string>(collection: ["good-one"]), manifests: out _, refused: out _, error: out var error));
        Assert.Contains(actualString: error, expectedSubstring: field);
    }
    [InlineData("--list", "good-one", true)]
    [InlineData("--all", "good-one", false)]
    [InlineData("--list", "--all", false)]
    [InlineData("--list", "--merge", false)]
    [InlineData("--list", "--plan", false)]
    [Theory]
    public void AScopedListIsOneSelectionForm(string first, string second, bool accepted) {
        var parsed = CanaryCommand.Create().Parse(args: [first, second]);

        Assert.Equal(expected: accepted, actual: (parsed.Errors.Count == 0));
    }
    [InlineData("headless", 0, false, 90)]
    [InlineData("headless", 1, true, 90)]
    [InlineData("headless", 60, true, 90)]
    [InlineData("headless", 61, true, 90)]
    [InlineData("headless", 90, true, 90)]
    [InlineData("headless", 91, false, 90)]
    [InlineData("windowed", 60, true, 60)]
    [InlineData("windowed", 61, false, 60)]
    [Theory]
    public void AHeadlessLegHasABoundedSimulationBudgetWithoutWideningTheGpuBudget(string bootShape, int seconds, bool accepted, int ceiling) {
        var directory = Path.Combine(path1: m_directory.RootPath, path2: "tests", path3: "Puck.World.Canaries", path4: "good-one");

        File.WriteAllText(path: Path.Combine(path1: directory, path2: "canary.json"), contents: LegManifest(
            id: "good-one", worldPrefix: "tests/Puck.World.Canaries/good-one/").Replace(
                newValue: $"\"bootShape\": \"{bootShape}\"", oldValue: "\"bootShape\": \"headless\"").Replace(
                newValue: $"\"timeoutSeconds\": {seconds}", oldValue: "\"timeoutSeconds\": 10"));
        var loaded = CanaryManifestLoader.TryLoadAll(error: out _, manifests: out var manifests,
            refused: out var refused, repositoryRoot: m_directory.RootPath, strict: false);

        Assert.Equal(actual: loaded, expected: accepted);
        if (accepted) {
            Assert.Equal(expected: seconds, actual: Assert.Single(collection: manifests).TimeoutSeconds);
        } else {
            Assert.Empty(collection: manifests);
            Assert.Contains(collection: refused, filter: refusal => refusal.Reason.Contains(
                comparisonType: StringComparison.Ordinal, value: $"timeoutSeconds must be in 1..{ceiling}"));
        }
    }
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("1", null)]
    [Theory]
    public void AScheduledLegIsAnExplicitBooleanAndOrdinaryLegsStayUnarmed(string spelling, bool? armed) {
        var directory = Path.Combine(path1: m_directory.RootPath, path2: "tests", path3: "Puck.World.Canaries", path4: "good-one");

        File.WriteAllText(path: Path.Combine(path1: directory, path2: "canary.json"), contents: LegManifest(
            id: "good-one", relaunch: $"\n\"runSchedule\": {spelling},",
            worldPrefix: "tests/Puck.World.Canaries/good-one/"));
        var loaded = CanaryManifestLoader.TryLoadAll(error: out _, manifests: out var manifests,
            refused: out var refused, repositoryRoot: m_directory.RootPath, strict: false);

        Assert.Equal(expected: armed.HasValue, actual: loaded);
        if (armed is { } expected) {
            Assert.Equal(expected: expected, actual: manifests[0].Positive.RunSchedule);
            Assert.False(condition: manifests[0].Discriminating.RunSchedule);
        } else {
            Assert.Empty(collection: manifests);
            Assert.Contains(collection: refused, filter: refusal => refusal.Reason.Contains(comparisonType: StringComparison.Ordinal, value: "runSchedule must be true or false"));
        }
    }
    [Fact]
    public void AScheduledRelaunchCanReadARepositoryFixture() {
        var directory = Path.Combine(path1: m_directory.RootPath, path2: "tests/Puck.World.Canaries/good-one");

        File.WriteAllText(path: Path.Combine(path1: directory, path2: "canary.json"), contents: LegManifest(
            id: "good-one", worldPrefix: "tests/Puck.World.Canaries/good-one/", relaunch: """
            "runSchedule": true,
            "relaunch": { "sourceWorld": "tests/Puck.World.Canaries/good-one/discriminating-world.json", "script": "positive.script.txt", "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ] },
            """));
        Assert.True(condition: CanaryManifestLoader.TryLoadAll(error: out var error, manifests: out var manifests,
            refused: out _, repositoryRoot: m_directory.RootPath, strict: false), userMessage: error);
        Assert.True(condition: manifests[0].Positive.RunSchedule);
        Assert.Equal(expected: Path.GetFullPath(path: Path.Combine(path1: directory, path2: "discriminating-world.json")).Replace(newChar: '/', oldChar: '\\'),
            actual: manifests[0].Positive.Relaunch!.WorldSourcePath!.Replace(newChar: '/', oldChar: '\\'));
        Assert.Null(@object: manifests[0].Positive.Relaunch!.WorldFileName);
    }
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
    public void Dispose() => m_directory.Dispose();
    // A relaunch names the document the first boot writes by its bare file name in the run directory, or names none and
    // boots the leg's own world again; a path, which could reach outside that directory, is refused by name.
    [InlineData("saved.world.json", true)]
    [InlineData(null, true)]
    [InlineData("../saved.world.json", false)]
    [InlineData("nested/saved.world.json", false)]
    [InlineData("saved.world.json", true, true)]
    [InlineData(null, true, true)]
    [Theory]
    public void ARelaunchBootsABareNamedSavedDocumentOrItsOwnWorld(string? world, bool loads, bool scheduled = false) {
        var goodDirectory = Path.Combine(
            path1: m_directory.RootPath,
            path2: "tests",
            path3: "Puck.World.Canaries",
            path4: "good-one"
        );

        Directory.Delete(
            path: Path.Combine(
                path1: m_directory.RootPath,
                path2: "tests",
                path3: "Puck.World.Canaries",
                path4: "orphan-one"
            )
        );
        File.WriteAllText(
            contents: LegManifest(
                id: "good-one",
                relaunch: $$"""

                "runSchedule": {{(scheduled ? "true" : "false")}},
                "relaunch": { {{((world is null) ? string.Empty : $"\"world\": \"{world}\", ")}}"script": "positive.script.txt", "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ] },
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
            repositoryRoot: m_directory.RootPath,
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
            path1: m_directory.RootPath,
            path2: "tests",
            path3: "Puck.World.Canaries",
            path4: "good-one"
        );

        Directory.Delete(
            path: Path.Combine(
                path1: m_directory.RootPath,
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
            repositoryRoot: m_directory.RootPath,
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
    // A declared fixture may sit in a subdirectory of its canary, a tree a script names by directory; a file there the
    // manifest does not declare, or a directory holding no file, is refused by name.
    [InlineData("tree/passes/kernel.hlsl", null, null)]
    [InlineData("tree/passes/kernel.hlsl", "tree/passes/extra.hlsl", "orphan file 'tree/passes/extra.hlsl'")]
    [InlineData("tree/passes/kernel.hlsl", "empty/", "holds no file")]
    [Theory]
    public void AFixtureTreeHoldsOnlyDeclaredFixtures(string fixture, string? extra, string? refusal) {
        var goodDirectory = Path.Combine(
            path1: m_directory.RootPath,
            path2: "tests",
            path3: "Puck.World.Canaries",
            path4: "good-one"
        );

        Directory.Delete(
            path: Path.Combine(
                path1: m_directory.RootPath,
                path2: "tests",
                path3: "Puck.World.Canaries",
                path4: "orphan-one"
            )
        );

        foreach (var path in ((string?[])[fixture, extra]).OfType<string>()) {
            var full = Path.Combine(
                path1: goodDirectory,
                path2: path
            );

            if (path.EndsWith(value: '/')) {
                Directory.CreateDirectory(path: full);
            } else {
                Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
                File.WriteAllText(
                    contents: "// fixture",
                    path: full
                );
            }
        }

        File.WriteAllText(
            contents: LegManifest(
                id: "good-one",
                worldPrefix: "tests/Puck.World.Canaries/good-one/"
            ).Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: $"\"timeoutSeconds\": 10,\n  \"fixtures\": [\"{fixture}\"],",
                oldValue: "\"timeoutSeconds\": 10,"
            ),
            path: Path.Combine(
                path1: goodDirectory,
                path2: "canary.json"
            )
        );

        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out _,
            refused: out _,
            repositoryRoot: m_directory.RootPath,
            strict: true
        );

        Assert.Equal(
            actual: loaded,
            expected: (refusal is null)
        );
        if (refusal is not null) {
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
            repositoryRoot: m_directory.RootPath,
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
            repositoryRoot: m_directory.RootPath,
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
