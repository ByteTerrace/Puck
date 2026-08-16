using System.Globalization;
using System.Text;

using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// The measurement sink. Every reported number is written here as it is produced, and the file lands beside
/// the test assembly so a run leaves its own evidence rather than a claim about it.
/// </summary>
internal static class MeasurementReport {
    private static readonly Lock Gate = new();
    private static readonly StringBuilder Lines = new();

    /// <summary>Gets the path the report is written to.</summary>
    internal static string Path => System.IO.Path.Combine(path1: AppContext.BaseDirectory, path2: "physics-measurements.txt");

    /// <summary>Appends one line and rewrites the file.</summary>
    /// <param name="line">The line.</param>
    internal static void Write(string line) {
        lock (Gate) {
            Lines.AppendLine(value: line);
            File.WriteAllText(path: Path, contents: Lines.ToString());
        }
    }
    /// <summary>Appends a section heading.</summary>
    /// <param name="title">The heading.</param>
    internal static void Section(string title) {
        Write(line: string.Empty);
        Write(line: ("## " + title));
    }
    /// <summary>Formats a fixed-point value for the report.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The formatted value.</returns>
    internal static string Format(FixedQ4816 value) =>
        ((double)value).ToString(format: "0.######", provider: CultureInfo.InvariantCulture);
}
