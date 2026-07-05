using System.Text;
using System.Text.Json;
using Puck.Scene;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A7. The scene document funnel: <c>Puck.Scene</c>'s parse→validate front door is the engine's
/// one-and-only data-driven scene path, so the POST proves it four ways. (1) Every checked-in example document
/// (<c>docs/examples/*.json</c>) parses and validates — the examples are the funnel's contract corpus. (2) A fixed
/// list of malformed documents is rejected, each with its source-attributed error. (3) Every example round-trips
/// serialize→parse→serialize bit-stable. (4) The committed <c>schema/run.schema.json</c> matches the generated
/// schema — editor IntelliSense cannot drift from the types that actually bind.
/// </summary>
internal sealed class RunDocumentStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "run-document";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var repositoryRoot = FindRepositoryRoot();

        if (repositoryRoot is null) {
            return PostStageOutcome.Fail(detail: "could not locate the repository root (a directory containing docs\\examples and schema\\run.schema.json) above the base or current directory");
        }

        var examplePaths = Directory.GetFiles(path: Path.Combine(repositoryRoot, "docs", "examples"), searchPattern: "*.json");

        if (0 == examplePaths.Length) {
            return PostStageOutcome.Fail(detail: "docs/examples contains no example documents; the funnel has no contract corpus");
        }

        // (1) Every example parses and validates; (3) and round-trips bit-stable. Examples named "-bad-" are the
        // corpus's checked-in NEGATIVES (they demonstrate validator output) — those must be rejected instead.
        var goodCount = 0;
        var badCount = 0;

        foreach (var examplePath in examplePaths) {
            var exampleName = Path.GetFileName(path: examplePath);
            PuckRunDocument document;

            if (exampleName.Contains(value: "-bad-", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                try {
                    _ = RunDocument.Load(path: examplePath);

                    return PostStageOutcome.Fail(detail: $"deliberately-invalid example {exampleName} was accepted by the validator");
                } catch (Exception exception) when (exception is JsonException or NotSupportedException or RunDocumentValidationException) {
                    badCount++;

                    continue;
                }
            }

            try {
                document = RunDocument.Load(path: examplePath);
                goodCount++;
            } catch (Exception exception) when (exception is JsonException or NotSupportedException or RunDocumentValidationException) {
                return PostStageOutcome.Fail(detail: $"known-good example {exampleName} was rejected: {Summarize(exception: exception)}");
            }

            try {
                var once = RunDocument.Serialize(document: document);
                var twice = RunDocument.Serialize(document: RunDocument.Parse(utf8Json: Encoding.UTF8.GetBytes(s: once)));

                if (!string.Equals(a: once, b: twice, comparisonType: StringComparison.Ordinal)) {
                    return PostStageOutcome.Fail(detail: $"{exampleName} does not round-trip bit-stable (serialize→parse→serialize diverged)");
                }
            } catch (Exception exception) when (exception is JsonException or NotSupportedException or RunDocumentValidationException) {
                return PostStageOutcome.Fail(detail: $"{exampleName} failed to re-parse its own serialization: {Summarize(exception: exception)}");
            }
        }

        // (2) The malformed corpus is rejected, each with its source-attributed error.
        foreach (var (caseName, json, expectedFragment) in MalformedDocuments()) {
            try {
                _ = RunDocument.Parse(utf8Json: Encoding.UTF8.GetBytes(s: json));

                return PostStageOutcome.Fail(detail: $"malformed document '{caseName}' was accepted by the validator");
            } catch (Exception exception) when (exception is JsonException or NotSupportedException or RunDocumentValidationException) {
                if (!Summarize(exception: exception).Contains(value: expectedFragment, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return PostStageOutcome.Fail(detail: $"malformed document '{caseName}' was rejected, but without the expected error \"{expectedFragment}\" (got: {Summarize(exception: exception)})");
                }
            }
        }

        // (4) The committed schema matches the generated one (normalized line endings; the writer appends a newline).
        var committedSchema = File.ReadAllText(path: Path.Combine(repositoryRoot, "schema", "run.schema.json")).ReplaceLineEndings(replacementText: "\n").TrimEnd('\n');
        var generatedSchema = RunDocumentSchema.Export().ReplaceLineEndings(replacementText: "\n").TrimEnd('\n');

        if (!string.Equals(a: committedSchema, b: generatedSchema, comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Fail(detail: "schema/run.schema.json has drifted from the generated schema (regenerate it via the tools schema verb)");
        }

        return PostStageOutcome.Pass(detail: $"{goodCount} examples parse+validate and round-trip bit-stable | {badCount} checked-in negatives + {MalformedDocuments().Count} malformed documents rejected with attributed errors | committed schema in sync");
    }

    // The negative corpus: each document violates exactly one funnel invariant this stage pins. Extra collected errors
    // are fine — the assertion is that the EXPECTED one is reported, source-attributed.
    private static IReadOnlyList<(string CaseName, string Json, string ExpectedFragment)> MalformedDocuments() {
        return [
            ("wrong-version", /*lang=json,strict*/ """{"version":"puck.run.v0","validation":{"gate":"world"}}""", "expected \"puck.run.v1\""),
            ("unknown-top-level-key", /*lang=json,strict*/ """{"version":"puck.run.v1","validation":{"gate":"world"},"scenes":{}}""", "unknown top-level member 'scenes'"),
            ("no-root-intent", /*lang=json,strict*/ """{"version":"puck.run.v1"}""", "a run requires"),
            ("two-root-intents", /*lang=json,strict*/ """{"version":"puck.run.v1","graph":{"$type":"world"},"validation":{"gate":"world"}}""", "mutually exclusive"),
            ("directx-offscreen-host", /*lang=json,strict*/ """{"version":"puck.run.v1","validation":{"gate":"world"},"host":{"backend":"directx"}}""", "host.backend"),
            ("truncated-json", """{"version":"puck.run.v1",""", "reached end of data"),
        ];
    }

    // Walks up from the app base and the working directory to the checkout root — the POST is a dev-box tool that runs
    // from the repository (dotnet run), so both anchors normally resolve; failing to find it is a loud stage fail, not
    // a skip.
    private static string? FindRepositoryRoot() {
        foreach (var anchor in (string?[])[AppContext.BaseDirectory, Environment.CurrentDirectory]) {
            for (var directory = anchor; (directory is not null); directory = Path.GetDirectoryName(path: directory)) {
                if (Directory.Exists(path: Path.Combine(directory, "docs", "examples")) && File.Exists(path: Path.Combine(directory, "schema", "run.schema.json"))) {
                    return directory;
                }
            }
        }

        return null;
    }

    // JsonException.Message carries the position; RunDocumentValidationException carries the collected, path-attributed
    // errors. Flatten either to one line for the outcome detail.
    private static string Summarize(Exception exception) =>
        exception.Message.ReplaceLineEndings(replacementText: " | ");
}
