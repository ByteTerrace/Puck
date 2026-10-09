using System.Globalization;
using System.Text.RegularExpressions;
using Puck.World;

namespace Puck.Cli.Counters;

public sealed record IndexedCountersReading(int Ordinal, int Line, ulong Tick, string Method, WorldCountersRun Run);
public static partial class CountersReading {
    private static readonly Regex IndirectIdentity = new(options: RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        pattern: @"^residency=[^|]+ allocation=(?<allocation>[1-9][0-9]*) epoch=(?<epoch>[0-9]+) generation=[01] stamp=(?<stamp>[1-9][0-9]*) source=(?<source>[1-9][0-9]*)$");

    /// <summary>Reads the warm-up's actual settlement tick and requires complete captured identity rows, without
    /// interpreting submitted scheduling flags as GPU completion.</summary>
    public static bool TryReadIndirectCompletion(string line, out ulong tick) {
        tick = 0;
        const string Prefix = "[indirect: settled at tick ";

        if (!line.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix) || !line.EndsWith(value: ']')) { return false; }
        var separator = line.IndexOf(": ", Prefix.Length, StringComparison.Ordinal);

        return ((separator >= 0)
            && ulong.TryParse(line.AsSpan(Prefix.Length, (separator - Prefix.Length)), NumberStyles.None, CultureInfo.InvariantCulture, out tick)
            && line[(separator + 2)..^1].Split(options: StringSplitOptions.None, separator: " | ").All(predicate: ValidIndirectIdentity));
    }
    /// <summary>Reads the existing engine-ready verdict for a sky-only observation. This establishes installed
    /// pipelines and completed initial frames, not an indirect source fence.</summary>
    /// <param name="line">The exact stderr completion line.</param>
    /// <param name="tick">The reported completed tick, or zero when the line refuses.</param>
    /// <returns>Whether the line is the complete named ready verdict with an unsigned tick.</returns>
    public static bool TryReadEngineCompletion(string line, out ulong tick) {
        tick = 0;
        const string Prefix = "[engine: ready at tick ";

        return (line.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix) && line.EndsWith(value: ']')
            && ulong.TryParse(line.AsSpan(Prefix.Length, ((line.Length - Prefix.Length) - 1)), NumberStyles.None,
                CultureInfo.InvariantCulture, out tick));
    }

    private static bool ValidIndirectIdentity(string identity) {
        var match = IndirectIdentity.Match(input: identity);

        return (match.Success
            && long.TryParse(match.Groups["allocation"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && uint.TryParse(match.Groups["epoch"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && uint.TryParse(match.Groups["stamp"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && ulong.TryParse(match.Groups["source"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    /// <summary>Reads exactly the ordered batch observations, retaining each actual response line and input release tick.
    /// A method echo and a new 120-tick wait must precede every reading; the runner's final error count must be zero.</summary>
    public static bool TryReadIndexed(IReadOnlyList<string> stdout, IReadOnlyList<string> methods, string backend,
        int width, int height, string compiler, out IReadOnlyList<IndexedCountersReading> readings, out string reason) {
        var collected = new List<IndexedCountersReading>();

        readings = [];
        reason = string.Empty;
        string? method = null;
        ulong? tick = null;
        var methodEchoed = false;
        ulong? precedingTick = null;

        for (var index = 0; (index < stdout.Count); index++) {
            var line = stdout[index];
            const string MethodPrefix = "[world.indirect-method: ";

            if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: MethodPrefix) && line.EndsWith(value: ']')) {
                method = line[MethodPrefix.Length..^1];
                methodEchoed = true;
            }
            const string WaitPrefix = "[world.wait: 120 ticks from ";
            const string Release = " — releasing at tick ";

            if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: WaitPrefix) && line.EndsWith(value: ']')) {
                var separator = line.IndexOf(comparisonType: StringComparison.Ordinal, value: Release);

                if ((separator < 0) || !ulong.TryParse(line.AsSpan((separator + Release.Length), (((line.Length - separator) - Release.Length) - 1)),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var release)
                    || !ulong.TryParse(line.AsSpan(WaitPrefix.Length, (separator - WaitPrefix.Length)), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var start) || (start > (ulong.MaxValue - 120)) || (release != (start + 120))
                    || !methodEchoed || ((precedingTick is { } previous) && (start < previous))) {
                    reason = "a batch input wait has no trustworthy 120-tick release";
                    return false;
                }
                tick = release;
            }
            if (!line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.counters:")) { continue; }
            if (collected.Count >= methods.Count) {
                reason = "the leg contains an extra counters response";
                return false;
            }
            if (!methodEchoed || (method != methods[collected.Count]) || (tick is null)) {
                reason = $"observation {(collected.Count + 1)} lacks its {methods[collected.Count]} method echo or new 120-tick input wait";
                return false;
            }
            if (!IsReading(line: line) || !TryReadLine(backend: backend, compiler: compiler, height: height, line: line, reason: out reason, run: out var run, width: width)) {
                if (reason.Length == 0) { reason = "a batch counters response is not bracketed JSON"; }
                return false;
            }
            collected.Add(item: new IndexedCountersReading((collected.Count + 1), (index + 1), tick.Value, method, run));
            precedingTick = tick;
            tick = null;
            methodEchoed = false;
        }
        if (collected.Count != methods.Count) {
            reason = $"expected {methods.Count} counters responses, found {collected.Count}";
            return false;
        }
        if (stdout.LastOrDefault(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[wire.errors:")) != "[wire.errors: 0 rejected]") {
            reason = "the batch did not finish with the runner's zero-rejection verdict";
            return false;
        }
        readings = collected;
        return true;
    }
}
