using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Puck.Cli.Host;

/// <summary>A thread's sampled time beneath one frame, broken down by the callee each sample was in.</summary>
/// <param name="Samples">The samples the trace held on the threads read, in the window read.</param>
/// <param name="Focused">The samples whose stack holds a frame naming the focus.</param>
/// <param name="Seconds">The span, in seconds, between the first and last sample read.</param>
/// <param name="Threads">Each thread's samples with its outermost engine frame, most samples first.</param>
/// <param name="Callees">Each callee path beneath the focus frame with its samples, most first; <c>self</c> for samples
/// in the focus frame itself.</param>
public sealed record SampleReport(long Samples, long Focused, double Seconds, IReadOnlyList<(int Id, long Samples, string Root)> Threads, IReadOnlyList<(string Callee, long Samples)> Callees);

/// <summary>
/// Another .NET process's threads, sampled by the runtime's sample profiler: about every millisecond the runtime records
/// each managed thread's stack, running or waiting, so a thread's samples beneath a frame are the share of its wall time
/// spent there, attributed without timing anything in the target. The report keeps the samples whose stack holds a frame
/// naming a focus text (its outermost such frame, so recursion counts once) and sums them by the path of callees
/// beneath it.
/// </summary>
public static class SampleTrace {
    private const string SampleProvider = "Microsoft-DotNETCore-SampleProfiler";
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>Records a window of a process's thread samples to an EventPipe trace file.</summary>
    /// <param name="processId">The target process, which must expose its diagnostics port.</param>
    /// <param name="duration">How long to record.</param>
    /// <param name="path">The <c>.nettrace</c> file to write.</param>
    /// <param name="cancellationToken">Ends the recording early.</param>
    /// <exception cref="ServerNotAvailableException">The process exposes no diagnostics port.</exception>
    public static async Task CollectAsync(int processId, TimeSpan duration, string path, CancellationToken cancellationToken) {
        // The runtime's loader and JIT events name the code that loads or tiers up during the window; the rundown at its
        // end names the rest.
        var keywords = (long)(ClrTraceEventParser.Keywords.Loader | ClrTraceEventParser.Keywords.Jit);
        await AllocationTrace.RecordAsync(cancellationToken: cancellationToken, duration: duration, path: path, processId: processId, providers: [
            new EventPipeProvider(eventLevel: EventLevel.Informational, keywords: 0, name: SampleProvider),
            new EventPipeProvider(eventLevel: EventLevel.Informational, keywords: keywords, name: RuntimeProvider),
        ]);
    }

    /// <summary>Reads a trace's thread samples beneath a focus frame.</summary>
    /// <param name="path">A <c>.nettrace</c> file holding the sample profiler's events (<see cref="CollectAsync"/>, or
    /// <c>dotnet-trace collect --profile cpu-sampling</c> or <c>thread-time</c>).</param>
    /// <param name="focus">The text a frame's full method name contains; <see langword="null"/> keeps every sample, beneath
    /// its thread's outermost frame.</param>
    /// <param name="thread">Only this thread's samples, or <see langword="null"/> for every thread.</param>
    /// <param name="depth">How many callee frames beneath the focus frame name a path, from one.</param>
    /// <param name="leaves">Zero to name each sample by the callees beneath the focus frame; otherwise how many of its
    /// innermost frames beneath the focus name it, innermost first, which sums the samples by where each thread was (a
    /// copy, a clear, a lock) and what called it.</param>
    /// <param name="fromMs">The first millisecond of the trace read.</param>
    /// <param name="toMs">The last millisecond of the trace read.</param>
    /// <returns>The report.</returns>
    public static SampleReport Read(string path, string? focus, int? thread, int depth = 1, double fromMs = 0, double toMs = double.MaxValue, int leaves = 0) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: depth, other: 1);
        ArgumentOutOfRangeException.ThrowIfNegative(value: leaves);

        var etlx = TraceLog.CreateFromEventPipeDataFile(filePath: path, etlxFilePath: Path.ChangeExtension(path: path, extension: ".etlx"));

        using var log = new TraceLog(etlxFilePath: etlx);
        var threads = new Dictionary<int, (long Samples, Dictionary<string, long> Roots)>();
        var callees = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        var frames = new List<string>(capacity: 64);
        long samples = 0, focused = 0;
        double first = double.NaN, last = 0;

        foreach (var data in log.Events) {
            if (!string.Equals(a: data.ProviderName, b: SampleProvider, comparisonType: StringComparison.Ordinal)) { continue; }

            var at = data.TimeStampRelativeMSec;

            if ((at < fromMs) || (at > toMs)) { continue; }
            if (thread is { } only && (only != data.ThreadID)) { continue; }

            var stack = data.CallStack();

            if (double.IsNaN(first)) { first = at; }
            last = at;
            samples++;

            var root = (AllocationTrace.Root(stack: stack) ?? "?");

            if (!threads.TryGetValue(key: data.ThreadID, value: out var share)) { share = (0, new Dictionary<string, long>(comparer: StringComparer.Ordinal)); }
            share.Roots[root] = share.Roots.GetValueOrDefault(key: root) + 1;
            threads[data.ThreadID] = (share.Samples + 1, share.Roots);

            // The stack from its root inward, so the outermost frame naming the focus is found first.
            frames.Clear();
            for (var frame = stack; frame is not null; frame = frame.Caller) { frames.Add(item: frame.CodeAddress.FullMethodName); }
            frames.Reverse();

            var focusAt = ((focus is null) ? 0 : frames.FindIndex(match: method => method.Contains(value: focus, comparisonType: StringComparison.Ordinal)));

            if (focusAt < 0) { continue; }
            focused++;

            var below = (frames.Count - (focusAt + 1));
            string callee;

            if (below == 0) {
                callee = "self";
            } else if (leaves == 0) {
                callee = string.Join(separator: " > ", values: frames.Skip(count: (focusAt + 1)).Take(count: depth));
            } else {
                // The innermost frames, innermost first, never reaching above the focus frame.
                callee = string.Join(separator: " < ", values: Enumerable.Reverse(source: frames).Take(count: Math.Min(val1: leaves, val2: below)));
            }

            callees[callee] = callees.GetValueOrDefault(key: callee) + 1;
        }

        return new SampleReport(
            Samples: samples,
            Focused: focused,
            Seconds: (double.IsNaN(first) ? 0 : ((last - first) / 1000)),
            Threads: [.. threads.OrderByDescending(static pair => pair.Value.Samples).Select(static pair => (pair.Key, pair.Value.Samples, pair.Value.Roots.MaxBy(static root => root.Value).Key))],
            Callees: [.. callees.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => (pair.Key, pair.Value))]
        );
    }
}
