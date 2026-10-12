using Puck.Cli.Automation;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Release.Tests.Automation;

/// <summary>
/// <c>puck artifacts coverage</c> merges every shard's Cobertura reports: a line is covered when any run hit it, a file
/// is named by its <c>src/</c> path whatever root the build spelled, generated sources and files outside <c>src/</c> are
/// left out, and the verdict fails on an uncovered line or an unloaded project outside the GPU-only list.
/// </summary>
public sealed class CoverageReportLawTests {
    private static string Report(TemporaryDirectory directory, string name, string fileName, params (int Number, int Hits)[] lines) =>
        directory.WriteText(name: name, text: $"""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage><packages><package name="P"><classes>
            <class name="C" filename="{fileName}"><lines>{string.Concat(values: lines.Select(selector: static line => $"<line number=\"{line.Number}\" hits=\"{line.Hits}\" />"))}</lines></class>
            <class name="G" filename="/_/src/Puck.A/obj/Release/G.cs"><lines><line number="1" hits="0" /></lines></class>
            <class name="T" filename="C:\checkout\tests\Puck.A.Tests\T.cs"><lines><line number="1" hits="0" /></lines></class>
            </classes></package></packages></coverage>
            """);

    [Fact]
    public void ARunsHitCoversTheLineWhateverRootTheBuildSpelled() {
        using var directory = new TemporaryDirectory();
        var files = CoverageReport.Merge(reports: [
            Report(directory: directory, fileName: @"C:\checkout\src\Puck.A\A.cs", lines: [(1, 0), (2, 3), (3, 0), (4, 0), (6, 0)], name: "0.cobertura.xml"),
            Report(directory: directory, fileName: "/_/src/Puck.A/A.cs", lines: [(1, 1), (2, 0)], name: "1.cobertura.xml"),
        ]);

        Assert.Equal(expected: ["src/Puck.A/A.cs"], actual: files.Keys);
        Assert.Equal(expected: [true, true, false, false, false], actual: files["src/Puck.A/A.cs"].Values);
        using var writer = new StringWriter();

        Assert.False(condition: CoverageReport.Judge(files: files, gpuOnly: [], projects: ["src/Puck.A", "src/Puck.B/"], writer: writer));
        Assert.Contains(expectedSubstring: "2 of 5 measured lines (40.00%)", actualString: writer.ToString());
        Assert.Contains(expectedSubstring: "src/Puck.A/A.cs: 3 of 5 lines uncovered: 3-4 6", actualString: writer.ToString());
        Assert.Contains(expectedSubstring: "  src/Puck.B/", actualString: writer.ToString());
    }
    [Fact]
    public void ACompactReportMergesAsItsCoberturaReportDid() {
        using var directory = new TemporaryDirectory();
        var report = Report(directory: directory, fileName: "/_/src/Puck.A/A.cs", lines: [(1, 2), (2, 0), (5, 1)], name: "0-A.cobertura.xml");
        var expected = CoverageReport.Merge(reports: [report]);
        var compact = CoverageReport.Compact(report: report);

        Assert.False(condition: File.Exists(path: report));
        Assert.Equal(expected: directory.PathOf(name: "0-A.coverage.tsv"), actual: compact);
        Assert.Equal(expected: $"src/Puck.A/A.cs\t1,5\t2{Environment.NewLine}", actual: File.ReadAllText(path: compact));
        var merged = CoverageReport.Merge(reports: [compact, Report(directory: directory, fileName: "src/Puck.A/A.cs", lines: [(2, 1)], name: "1-A.cobertura.xml")]);

        Assert.Equal(expected: expected.Keys, actual: merged.Keys);
        Assert.Equal(expected: [true, true, true], actual: merged["src/Puck.A/A.cs"].Values);
    }
    [Fact]
    public void TheGpuOnlyListLeavesItsLinesAndProjectsOut() {
        using var directory = new TemporaryDirectory();
        var files = CoverageReport.Merge(reports: [
            Report(directory: directory, fileName: "src/Puck.A/A.cs", lines: [(1, 1)], name: "0.cobertura.xml"),
            Report(directory: directory, fileName: "/_/src/Puck.A/Gpu/B.cs", lines: [(1, 0)], name: "1.cobertura.xml"),
        ]);
        var gpuOnly = CoverageReport.ParseGpuOnly(text: "# GPU only\r\nsrc/Puck.A/Gpu/ # the device path\n\nsrc\\Puck.B/\n");
        using var writer = new StringWriter();

        Assert.Equal(expected: ["src/Puck.A/Gpu/", "src/Puck.B/"], actual: gpuOnly);
        Assert.True(condition: CoverageReport.Judge(files: files, gpuOnly: gpuOnly, projects: ["src/Puck.A/", "src/Puck.B/"], writer: writer));
        Assert.Equal(expected: $"Line coverage: 1 of 1 measured lines (100.00%), GPU-only prefixes left out: 2.{Environment.NewLine}", actual: writer.ToString());
        Assert.Null(@object: CoverageReport.SourcePath(fileName: "lib/Puck.C/C.cs"));
    }
}
