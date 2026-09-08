using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Puck.GamingBricks.Post;

/// <summary>The machine-readable files a battery run leaves beside its table: <c>summary.json</c> (per-stage verdicts,
/// durations, and case counts, for a dashboard or a diff between two runs) and <c>results.junit.xml</c> (one JUnit
/// test case per stage case, or per stage when it has none, which every continuous-integration reporter reads).</summary>
public static partial class PostReportFiles {
    private sealed class SummaryDto {
        public required string Banner { get; init; }
        public required int ExitCode { get; init; }
        public required double DurationSeconds { get; init; }
        public required StageDto[] Stages { get; init; }
    }
    private sealed class StageDto {
        public required string Name { get; init; }
        public required string Tier { get; init; }
        public required string Verdict { get; init; }
        public required double DurationSeconds { get; init; }
        public required string Detail { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CaseCountsDto? Cases { get; init; }
    }
    private sealed class CaseCountsDto {
        public required int Pass { get; init; }
        public required int ExpectedFail { get; init; }
        public required int Skip { get; init; }
        public required int Mismatch { get; init; }
        public required int Error { get; init; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SummaryDto))]
    private sealed partial class SummaryJsonContext : JsonSerializerContext;

    /// <summary>Writes <c>summary.json</c>.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="report">The run's report.</param>
    public static void WriteSummary(string path, PostReport report) {
        ArgumentNullException.ThrowIfNull(argument: report);

        var summary = new SummaryDto {
            Banner = report.Banner,
            DurationSeconds = Math.Round(
                digits: 3,
                value: report.Duration.TotalSeconds
            ),
            ExitCode = report.ExitCode,
            Stages = report.Results
                .Select(selector: static result => new StageDto {
                    Cases = ((result.Outcome.Cases is null)
                        ? null
                        : new CaseCountsDto {
                            Error = Count(
                                cases: result.Outcome.Cases,
                                verdict: PostCaseVerdict.Error
                            ),
                            ExpectedFail = Count(
                                cases: result.Outcome.Cases,
                                verdict: PostCaseVerdict.ExpectedFail
                            ),
                            Mismatch = Count(
                                cases: result.Outcome.Cases,
                                verdict: PostCaseVerdict.Mismatch
                            ),
                            Pass = Count(
                                cases: result.Outcome.Cases,
                                verdict: PostCaseVerdict.Pass
                            ),
                            Skip = Count(
                                cases: result.Outcome.Cases,
                                verdict: PostCaseVerdict.Skip
                            ),
                        }),
                    Detail = result.Outcome.Detail,
                    DurationSeconds = Math.Round(
                        digits: 3,
                        value: result.Duration.TotalSeconds
                    ),
                    Name = result.Name,
                    Tier = result.Tier.ToString(),
                    Verdict = result.Outcome.Verdict.ToString(),
                })
                .ToArray(),
        };
        var json = JsonSerializer.Serialize(
            value: summary,
            jsonTypeInfo: SummaryJsonContext.Default.SummaryDto
        ).Replace(
            oldValue: "\r\n",
            newValue: "\n"
        );

        File.WriteAllText(
            contents: (json + "\n"),
            path: path
        );
    }
    /// <summary>Writes <c>results.junit.xml</c>: one <c>testsuite</c> per stage, one <c>testcase</c> per case row (or one
    /// for the stage itself when it has no rows). A <see cref="PostCaseVerdict.Mismatch"/> is a JUnit failure, a
    /// <see cref="PostCaseVerdict.Error"/> a JUnit error, a <see cref="PostCaseVerdict.Skip"/> a JUnit skip, and an
    /// <see cref="PostCaseVerdict.ExpectedFail"/> a passing case whose output carries the recorded reason.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="report">The run's report.</param>
    public static void WriteJUnit(string path, PostReport report) {
        ArgumentNullException.ThrowIfNull(argument: report);

        using var writer = XmlWriter.Create(
            outputFileName: path,
            settings: new XmlWriterSettings {
                Indent = true,
                NewLineChars = "\n",
            }
        );

        writer.WriteStartDocument();
        writer.WriteStartElement(localName: "testsuites");
        WriteAttribute(
            name: "name",
            value: report.Banner,
            writer: writer
        );
        WriteAttribute(
            name: "time",
            value: Seconds(duration: report.Duration),
            writer: writer
        );

        foreach (var result in report.Results) {
            WriteSuite(
                result: result,
                writer: writer
            );
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static int Count(IReadOnlyList<PostCaseResult> cases, PostCaseVerdict verdict) =>
        cases.Count(predicate: result => (result.Verdict == verdict));
    private static string Seconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString(
        format: "0.000",
        provider: CultureInfo.InvariantCulture
    );
    private static void WriteAttribute(XmlWriter writer, string name, string value) =>
        writer.WriteAttributeString(
            localName: name,
            value: value
        );
    private static void WriteCase(XmlWriter writer, string suite, string name, PostCaseVerdict verdict, string detail, TimeSpan duration) {
        writer.WriteStartElement(localName: "testcase");
        WriteAttribute(
            name: "classname",
            value: suite,
            writer: writer
        );
        WriteAttribute(
            name: "name",
            value: name,
            writer: writer
        );
        WriteAttribute(
            name: "time",
            value: Seconds(duration: duration),
            writer: writer
        );

        switch (verdict) {
            case PostCaseVerdict.Mismatch:
                writer.WriteStartElement(localName: "failure");
                WriteAttribute(
                    name: "message",
                    value: detail,
                    writer: writer
                );
                writer.WriteEndElement();

                break;
            case PostCaseVerdict.Error:
                writer.WriteStartElement(localName: "error");
                WriteAttribute(
                    name: "message",
                    value: detail,
                    writer: writer
                );
                writer.WriteEndElement();

                break;
            case PostCaseVerdict.Skip:
                writer.WriteStartElement(localName: "skipped");
                WriteAttribute(
                    name: "message",
                    value: detail,
                    writer: writer
                );
                writer.WriteEndElement();

                break;
            default:
                writer.WriteElementString(
                    localName: "system-out",
                    value: detail
                );

                break;
        }

        writer.WriteEndElement();
    }
    private static void WriteSuite(XmlWriter writer, PostStageResult result) {
        var cases = result.Outcome.Cases;

        writer.WriteStartElement(localName: "testsuite");
        WriteAttribute(
            name: "name",
            value: result.Name,
            writer: writer
        );
        WriteAttribute(
            name: "time",
            value: Seconds(duration: result.Duration),
            writer: writer
        );

        if (cases is null) {
            var verdict = result.Outcome.Verdict switch {
                PostVerdict.Pass => PostCaseVerdict.Pass,
                PostVerdict.Skip => PostCaseVerdict.Skip,
                PostVerdict.Fail => PostCaseVerdict.Mismatch,
                _ => PostCaseVerdict.Error,
            };

            WriteCounts(
                errors: ((verdict == PostCaseVerdict.Error) ? 1 : 0),
                failures: ((verdict == PostCaseVerdict.Mismatch) ? 1 : 0),
                skipped: ((verdict == PostCaseVerdict.Skip) ? 1 : 0),
                tests: 1,
                writer: writer
            );
            WriteCase(
                detail: result.Outcome.Detail,
                duration: result.Duration,
                name: result.Name,
                suite: result.Name,
                verdict: verdict,
                writer: writer
            );
        } else {
            WriteCounts(
                errors: Count(
                    cases: cases,
                    verdict: PostCaseVerdict.Error
                ),
                failures: Count(
                    cases: cases,
                    verdict: PostCaseVerdict.Mismatch
                ),
                skipped: Count(
                    cases: cases,
                    verdict: PostCaseVerdict.Skip
                ),
                tests: cases.Count,
                writer: writer
            );

            foreach (var item in cases) {
                WriteCase(
                    detail: item.Detail,
                    duration: item.Duration,
                    name: item.Name,
                    suite: result.Name,
                    verdict: item.Verdict,
                    writer: writer
                );
            }
        }

        writer.WriteEndElement();
    }
    private static void WriteCounts(XmlWriter writer, int tests, int failures, int errors, int skipped) {
        WriteAttribute(
            name: "tests",
            value: tests.ToString(provider: CultureInfo.InvariantCulture),
            writer: writer
        );
        WriteAttribute(
            name: "failures",
            value: failures.ToString(provider: CultureInfo.InvariantCulture),
            writer: writer
        );
        WriteAttribute(
            name: "errors",
            value: errors.ToString(provider: CultureInfo.InvariantCulture),
            writer: writer
        );
        WriteAttribute(
            name: "skipped",
            value: skipped.ToString(provider: CultureInfo.InvariantCulture),
            writer: writer
        );
    }
}
