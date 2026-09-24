using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves the <c>greater</c> relation end to end: a manifest naming it loads strictly, and its leg holds only when the
/// left extracted value is strictly above the right one. Equal values, a smaller left value and a value that is not a
/// number each fail.
/// </summary>
public sealed class CanaryRelationLawTests : IDisposable {
    private readonly CanaryLeg m_leg;
    private readonly string m_root = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-cli-tests-canary-relation-{Guid.NewGuid():N}"
    );

    public CanaryRelationLawTests() {
        var directory = Path.Combine(
            path1: m_root,
            path2: "tests",
            path3: "Puck.World.Canaries",
            path4: "ordered"
        );

        Directory.CreateDirectory(path: directory);

        foreach (var file in ((string[])["world.json", "positive.script.txt", "discriminating.script.txt"])) {
            File.WriteAllText(
                contents: (file.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: ".json"
                ) ? "{}" : "wire.errors\n"),
                path: Path.Combine(
                    path1: directory,
                    path2: file
                )
            );
        }

        File.WriteAllText(
            contents: """
            {
              "id": "ordered",
              "title": "a synthetic manifest for the greater relation",
              "binding": "a synthetic manifest for the greater relation",
              "bootShape": "headless",
              "requirements": [],
              "timeoutSeconds": 10,
              "positive": {
                "world": "tests/Puck.World.Canaries/ordered/world.json",
                "script": "positive.script.txt",
                "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ],
                "expect": [
                  { "type": "response", "name": "earlier", "stream": "stdout", "verb": "probe", "occurrence": 1, "count": 2, "extract": [ { "name": "before", "field": "submission" } ] },
                  { "type": "response", "name": "later", "stream": "stdout", "verb": "probe", "occurrence": 2, "count": 2, "extract": [ { "name": "after", "field": "submission" } ] },
                  { "type": "relation", "name": "later-is-larger", "operator": "greater", "left": { "value": "after" }, "right": { "value": "before" } }
                ]
              },
              "discriminating": {
                "world": "tests/Puck.World.Canaries/ordered/world.json",
                "script": "discriminating.script.txt",
                "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ],
                "expect": [ { "type": "line", "name": "clean-wire", "stream": "stdout", "match": "contains", "text": "[wire.errors: 0 rejected]", "present": true } ]
              }
            }
            """,
            path: Path.Combine(
                path1: directory,
                path2: "canary.json"
            )
        );

        Assert.True(
            condition: CanaryManifestLoader.TryLoadAll(
                error: out var error,
                manifests: out var manifests,
                refused: out _,
                repositoryRoot: m_root,
                strict: true
            ),
            userMessage: error
        );
        m_leg = Assert.Single(collection: manifests).Positive;
    }

    private bool Holds(string before, string after) =>
        CanaryAssertions.Evaluate(
            leg: m_leg,
            primaryTranscript: new CanaryTranscript(
                RunDirectory: m_root,
                Stderr: [],
                Stdout: [$"[probe: work submission={before} revision=1]", $"[probe: work submission={after} revision=1]"]
            )
        ).Passed;

    public void Dispose() {
        try {
            Directory.Delete(
                path: m_root,
                recursive: true
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    [Fact]
    public void GreaterHoldsOnlyWhenTheLeftValueIsStrictlyAboveTheRight() {
        Assert.Equal(
            actual: (Above: Holds(after: "8", before: "7"), Equal: Holds(after: "7", before: "7"), Below: Holds(after: "6", before: "7"), NotANumber: Holds(after: "many", before: "7")),
            expected: (Above: true, Equal: false, Below: false, NotANumber: false)
        );
    }
}
