using System.Text;
using System.Xml;

namespace Puck.Cli.Automation;

/// <summary>
/// Merges the Cobertura reports of every test run into one line-coverage verdict over the live <c>src/</c> tree. A line
/// is covered when any run hit it. A source file is named by its repository-relative path whatever root the build that
/// produced the report spelled (a local checkout, or <c>/_/</c> under a CI build's path mapping). Lines under a prefix of
/// the hardware-only list (<c>build/CoverageHardwareOnly.txt</c>) are left out: the runners measuring coverage have no
/// GPU and no real mouse or foreground window, and the list is the one place that says which code only that hardware
/// reaches.
/// </summary>
public static class CoverageReport {
    /// <summary>The repository-relative path of the hardware-only list.</summary>
    public const string HardwareOnlyList = "build/CoverageHardwareOnly.txt";

    /// <summary>Reads the hardware-only list: one repository-relative file or directory prefix per line, with <c>#</c>
    /// starting a comment.</summary>
    /// <param name="text">The list's text.</param>
    /// <returns>The prefixes, with forward slashes.</returns>
    public static IReadOnlyList<string> ParseHardwareOnly(string text) => [.. text
        .Split(separator: '\n')
        .Select(selector: static line => {
            var comment = line.IndexOf(value: '#');

            return ((comment < 0) ? line : line[..comment]).Trim().Replace(oldChar: '\\', newChar: '/');
        })
        .Where(predicate: static line => (line.Length != 0))];
    /// <summary>Returns the repository-relative <c>src/</c> path a report spells for a source file, or
    /// <see langword="null"/> for a file outside <c>src/</c> or generated under an <c>obj</c> directory.</summary>
    /// <param name="fileName">The report's file name, absolute or mapped.</param>
    /// <returns>The path, starting <c>src/</c>, or <see langword="null"/>.</returns>
    public static string? SourcePath(string fileName) {
        var path = fileName.Replace(oldChar: '\\', newChar: '/');
        var at = path.LastIndexOf(comparisonType: StringComparison.Ordinal, value: "/src/Puck.");

        if (at < 0) {
            if (!path.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/Puck.")) { return null; }
            at = -1;
        }
        var relative = path[(at + 1)..];

        return relative.Contains(comparisonType: StringComparison.Ordinal, value: "/obj/") ? null : relative;
    }
    /// <summary>The extension of a compact report (<see cref="WriteCompact"/>): a Cobertura report a CI run uploads is
    /// tens of megabytes, its compact form a few hundred kilobytes.</summary>
    public const string CompactExtension = ".coverage.tsv";

    /// <summary>Writes merged coverage compactly: one line per source file, its path, the lines a run hit and the
    /// lines none did, tab-separated, each list comma-separated. <see cref="Merge"/> reads it back.</summary>
    /// <param name="files">The merged coverage.</param>
    /// <param name="writer">The destination.</param>
    public static void WriteCompact(SortedDictionary<string, SortedDictionary<int, bool>> files, TextWriter writer) {
        foreach (var (path, lines) in files) {
            writer.Write(value: path);
            writer.Write(value: '\t');
            writer.Write(value: string.Join(separator: ',', values: lines.Where(predicate: static line => line.Value).Select(selector: static line => line.Key)));
            writer.Write(value: '\t');
            writer.WriteLine(value: string.Join(separator: ',', values: lines.Where(predicate: static line => !line.Value).Select(selector: static line => line.Key)));
        }
    }
    /// <summary>Replaces a Cobertura report with its compact form beside it (<c>name.cobertura.xml</c> becomes
    /// <c>name.coverage.tsv</c>).</summary>
    /// <param name="report">The Cobertura report's path; it is deleted.</param>
    /// <returns>The compact report's path.</returns>
    public static string Compact(string report) {
        var compact = (report[..report.LastIndexOf(comparisonType: StringComparison.Ordinal, value: ".cobertura.xml")] + CompactExtension);

        using (var writer = new StreamWriter(path: compact)) { WriteCompact(files: Merge(reports: [report]), writer: writer); }
        File.Delete(path: report);
        return compact;
    }
    /// <summary>Merges reports, Cobertura or compact (<see cref="CompactExtension"/>): for each source file, every
    /// line a report lists, and whether any report hit it.</summary>
    /// <param name="reports">The reports' paths.</param>
    /// <returns>Each repository-relative source file's lines, mapped to whether any run hit them.</returns>
    public static SortedDictionary<string, SortedDictionary<int, bool>> Merge(IEnumerable<string> reports) {
        var files = new SortedDictionary<string, SortedDictionary<int, bool>>(comparer: StringComparer.Ordinal);

        foreach (var report in reports) {
            if (report.EndsWith(comparisonType: StringComparison.Ordinal, value: CompactExtension)) {
                foreach (var entry in File.ReadLines(path: report)) {
                    var fields = entry.Split(separator: '\t');

                    if (!files.TryGetValue(key: fields[0], value: out var known)) {
                        known = [];
                        files.Add(key: fields[0], value: known);
                    }
                    for (var column = 1; (column <= 2); column++) {
                        foreach (var number in fields[column].Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ',')) {
                            var line = int.Parse(s: number, provider: System.Globalization.CultureInfo.InvariantCulture);

                            known[line] = (known.GetValueOrDefault(key: line) || (column == 1));
                        }
                    }
                }
                continue;
            }
            using var reader = XmlReader.Create(inputUri: report, settings: new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            SortedDictionary<int, bool>? lines = null;

            while (reader.Read()) {
                if (reader.NodeType != XmlNodeType.Element) { continue; }
                if (reader.Name == "class") {
                    var path = SourcePath(fileName: (reader.GetAttribute(name: "filename") ?? ""));

                    lines = null;
                    if (path is not null) {
                        if (!files.TryGetValue(key: path, value: out lines)) {
                            lines = [];
                            files.Add(key: path, value: lines);
                        }
                    }
                } else if ((reader.Name == "line") && (lines is not null)) {
                    var number = int.Parse(s: reader.GetAttribute(name: "number")!, provider: System.Globalization.CultureInfo.InvariantCulture);
                    var hit = (reader.GetAttribute(name: "hits") is { } hits && (hits != "0"));

                    lines[number] = (lines.GetValueOrDefault(key: number) || hit);
                }
            }
        }
        return files;
    }
    /// <summary>Writes the verdict over merged coverage: the covered and measured line counts, every uncovered line
    /// grouped into ranges by file, and every live project directory no run loaded a line of.</summary>
    /// <param name="files">The merged coverage (<see cref="Merge"/>).</param>
    /// <param name="projects">The live <c>src/</c> project directories, repository-relative.</param>
    /// <param name="hardwareOnly">The hardware-only prefixes (<see cref="ParseHardwareOnly"/>).</param>
    /// <param name="writer">The report's destination.</param>
    /// <returns><see langword="true"/> when every measured line outside the hardware-only list is covered and every project
    /// outside it was loaded.</returns>
    public static bool Judge(SortedDictionary<string, SortedDictionary<int, bool>> files, IEnumerable<string> projects, IReadOnlyList<string> hardwareOnly, TextWriter writer) {
        bool Excluded(string path) => hardwareOnly.Any(predicate: prefix => path.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix));
        var measured = 0;
        var covered = 0;
        var uncovered = new StringBuilder();
        var uncoveredFiles = 0;

        foreach (var (path, lines) in files) {
            if (Excluded(path: path)) { continue; }
            measured += lines.Count;
            var hit = lines.Count(predicate: static line => line.Value);

            covered += hit;
            if (hit == lines.Count) { continue; }
            uncoveredFiles++;
            uncovered.Append(value: $"  {path}: {(lines.Count - hit)} of {lines.Count} lines uncovered:");
            int? start = null;
            var end = 0;

            foreach (var (number, isHit) in lines) {
                if (!isHit && (start is not null) && (number == (end + 1))) { end = number; continue; }
                if (start is not null) { uncovered.Append(value: (start == end) ? $" {start}" : $" {start}-{end}"); start = null; }
                if (!isHit) { start = number; end = number; }
            }
            if (start is not null) { uncovered.Append(value: (start == end) ? $" {start}" : $" {start}-{end}"); }
            uncovered.AppendLine();
        }
        var unloaded = projects
            .Select(selector: static project => (project.TrimEnd(trimChar: '/') + "/"))
            .Where(predicate: project => !Excluded(path: project) && !files.Keys.Any(predicate: path => path.StartsWith(comparisonType: StringComparison.Ordinal, value: project)))
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();
        var rate = ((measured == 0) ? 0d : ((100d * covered) / measured));

        writer.WriteLine(value: $"Line coverage: {covered} of {measured} measured lines ({rate:F2}%), hardware-only prefixes left out: {hardwareOnly.Count}.");
        if (uncoveredFiles != 0) {
            writer.WriteLine(value: $"{uncoveredFiles} files hold uncovered lines:");
            writer.Write(value: uncovered.ToString());
        }
        if (unloaded.Length != 0) {
            writer.WriteLine(value: $"{unloaded.Length} live projects were never loaded by any test run:");
            foreach (var project in unloaded) { writer.WriteLine(value: $"  {project}"); }
        }
        return ((covered == measured) && (unloaded.Length == 0));
    }
}
