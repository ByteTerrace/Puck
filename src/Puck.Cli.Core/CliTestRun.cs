namespace Puck.Cli;

/// <summary>
/// The one spelling of a test run's options. Every test project is an xUnit v3 executable on Microsoft.Testing.Platform:
/// <c>dotnet test</c> drives it, and <c>dotnet &lt;assembly&gt;.dll</c> runs it directly, and both take the same
/// options. Every verb that selects tests, asks for a report, or reads a run's exit code takes them from here.
/// </summary>
public static class CliTestRun {
    /// <summary>The exit code of a run in which at least one test failed; any other nonzero code means the run itself
    /// did not finish or selected nothing.</summary>
    public const int TestsFailed = 2;
    /// <summary>The <c>Category</c> trait of a law that reads the build tree itself: the <c>bin</c> and <c>obj</c> files a
    /// build of this checkout laid out, and how they share storage. Only a build's own checkout holds one; a restored
    /// archive of compiled outputs carries no <c>obj</c> and writes each file on its own.</summary>
    public const string BuildTreeCategory = "BuildTree";

    /// <summary>Leaves out every test that opens a hardware GPU device (<c>[Trait("Category", "Gpu")]</c>, which GPU001
    /// requires of each such class): the selection of every run beside a GPU leg or on a host without a GPU.</summary>
    public static readonly string[] CpuSelection = ["--filter-not-trait", "Category=Gpu"];
    /// <summary>Leaves out every law that reads the build tree (<see cref="BuildTreeCategory"/>): the selection of a run
    /// over a restored archive rather than a build.</summary>
    public static readonly string[] WithoutBuildTree = ["--filter-not-trait", $"Category={BuildTreeCategory}"];

    /// <summary>Selects every test whose fully qualified method name contains <paramref name="name"/>, such as a class
    /// (<c>Class</c>) or one of its methods (<c>Class.Method</c>).</summary>
    /// <param name="name">The dotted name to match anywhere in <c>Namespace.Class.Method</c>.</param>
    /// <returns>The options.</returns>
    public static string[] Containing(string name) => ["--filter-method", $"*{name}*"];
    /// <summary>Selects every test of one class.</summary>
    /// <param name="fullName">The class's fully qualified name.</param>
    /// <returns>The options.</returns>
    public static string[] Class(string fullName) => ["--filter-class", fullName];
    /// <summary>Fails a run in which no test executed, so a run whose every test skipped is refused as surely as one
    /// that discovered none.</summary>
    /// <returns>The options.</returns>
    public static string[] SomeTestExecutes() => ["--zero-tests-policy", "strict"];
    /// <summary>Writes the run's TRX report.</summary>
    /// <param name="directory">The results directory.</param>
    /// <param name="fileName">The report's file name in it.</param>
    /// <returns>The options.</returns>
    public static string[] Report(string directory, string fileName) => ["--results-directory", directory, "--report-xunit-trx", "--report-xunit-trx-filename", fileName];
    /// <summary>Reads the counts of a run's closing summary from its console output: the <c>total:</c>,
    /// <c>failed:</c>, <c>succeeded:</c> and <c>skipped:</c> lines after the last <c>Test run summary:</c> line.</summary>
    /// <param name="output">The run's standard output, one line per entry.</param>
    /// <returns>The counts on one line, such as <c>total 12, failed 0, succeeded 12, skipped 0</c>, or
    /// <c>no summary</c> when the run printed none.</returns>
    public static string Summary(IReadOnlyList<string> output) {
        var start = -1;

        for (var index = 0; (index < output.Count); index++) {
            if (output[index].TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "Test run summary:")) {
                start = index;
            }
        }

        if (start < 0) {
            return "no summary";
        }

        string[] counts = ["total", "failed", "succeeded", "skipped"];

        return string.Join(separator: ", ", values: output.Skip(count: (start + 1))
            .Select(selector: static line => line.Trim().Split(count: 2, options: StringSplitOptions.TrimEntries, separator: ':'))
            .Where(predicate: parts => ((parts.Length == 2) && counts.Contains(value: parts[0])))
            .Select(selector: static parts => $"{parts[0]} {parts[1]}"));
    }
    /// <summary>Writes a mini dump of a run that stops making progress, and ends it.</summary>
    /// <param name="timeout">How long the run may go without progress.</param>
    /// <returns>The options.</returns>
    public static string[] HangDump(TimeSpan timeout) => ["--hangdump", "--hangdump-timeout", $"{((int)timeout.TotalMinutes)}m", "--hangdump-type", "Mini"];
}
