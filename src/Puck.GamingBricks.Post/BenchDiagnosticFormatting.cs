using System.Diagnostics;
using System.Text;

namespace Puck.GamingBricks.Post;

/// <summary>Shared report-formatting and argument-parsing helpers for a machine family's <c>bench</c> diagnostic —
/// the scaffolding around a fleet-throughput table, not any emulation semantics.</summary>
public static class BenchDiagnosticFormatting {
    /// <summary>Converts a <see cref="Stopwatch"/> tick count to microseconds.</summary>
    /// <param name="ticks">A duration in <see cref="Stopwatch"/> ticks.</param>
    public static double TicksToMicroseconds(long ticks) =>
        ((ticks * 1_000_000.0) / Stopwatch.Frequency);
    /// <summary>Appends <paramref name="text"/> to <paramref name="report"/> and echoes it to the console, so a
    /// diagnostic's report is built and streamed in the same call.</summary>
    /// <param name="report">The report being accumulated.</param>
    /// <param name="text">The line to append and print.</param>
    public static void Line(StringBuilder report, string text) {
        report.AppendLine(value: text);
        Console.WriteLine(value: text);
    }
    /// <summary>Parses a <c>--bench-fleet</c>-style comma-separated list of fleet sizes, falling back to
    /// <paramref name="defaultFleetSizes"/> when <paramref name="value"/> is absent.</summary>
    /// <param name="value">The raw flag value, or <see langword="null"/>/empty to use the default.</param>
    /// <param name="defaultFleetSizes">The caller's default fleet sizes.</param>
    public static int[] ParseFleetSizes(string? value, int[] defaultFleetSizes) {
        if (string.IsNullOrEmpty(value: value)) {
            return defaultFleetSizes;
        }

        return Array.ConvertAll(
            array: value.Split(separator: ','),
            converter: static size => int.Parse(s: size)
        );
    }
}
