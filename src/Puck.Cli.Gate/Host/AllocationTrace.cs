using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Puck.Cli.Host;

/// <summary>
/// Another .NET process's managed allocation, read by the runtime's allocation ticks with their stacks. The runtime
/// raises one <c>GCAllocationTick</c> event each time a thread's allocations cross about 100 KB (the sampling the
/// runtime does itself, so the reading costs the target almost nothing), naming the type that crossed it, whether it went
/// to the small or the large object heap, and the bytes since the last tick, with the allocating thread's stack. Summed,
/// the ticks give each thread's, type's and call site's share of the bytes allocated; a speedscope conversion of the same
/// trace weighs stacks by time and cannot. The collections the window held come from the runtime's GC events: each
/// collection's generation and reason, and the pause from the suspension's start to the restart's end.
/// </summary>
public static class AllocationTrace {
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>Records a window of a process's allocation ticks and GC events to an EventPipe trace file.</summary>
    /// <param name="processId">The target process, which must expose its diagnostics port.</param>
    /// <param name="duration">How long to record.</param>
    /// <param name="path">The <c>.nettrace</c> file to write.</param>
    /// <param name="cancellationToken">Ends the recording early.</param>
    /// <exception cref="ServerNotAvailableException">The process exposes no diagnostics port.</exception>
    public static async Task CollectAsync(int processId, TimeSpan duration, string path, CancellationToken cancellationToken) {
        // GC at verbose raises the allocation ticks; Loader and JIT name the code that loads or tiers up during the
        // window, and the rundown at its end names the rest (precompiled code included).
        var keywords = (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Loader | ClrTraceEventParser.Keywords.Jit);
        await RecordAsync(cancellationToken: cancellationToken, duration: duration, path: path, processId: processId, providers: [new EventPipeProvider(eventLevel: EventLevel.Verbose, keywords: keywords, name: RuntimeProvider)]);
    }
    /// <summary>Records a window of a process's events from the given providers, with the rundown that names its code at
    /// the window's end, to an EventPipe trace file.</summary>
    /// <param name="processId">The target process, which must expose its diagnostics port.</param>
    /// <param name="providers">The providers the session enables.</param>
    /// <param name="duration">How long to record.</param>
    /// <param name="path">The <c>.nettrace</c> file to write.</param>
    /// <param name="cancellationToken">Ends the recording early.</param>
    /// <exception cref="ServerNotAvailableException">The process exposes no diagnostics port.</exception>
    internal static async Task RecordAsync(int processId, IReadOnlyList<EventPipeProvider> providers, TimeSpan duration, string path, CancellationToken cancellationToken) {
        var client = new DiagnosticsClient(processId: processId);
        using var session = client.StartEventPipeSession(providers: providers, requestRundown: true, circularBufferMB: 512);
        await using var file = File.Create(path: path);
        var copy = session.EventStream.CopyToAsync(destination: file, cancellationToken: CancellationToken.None);

        try { await Task.Delay(delay: duration, cancellationToken: cancellationToken); } catch (OperationCanceledException) { }
        await session.StopAsync(cancellationToken: CancellationToken.None);
        await copy;
    }

    /// <summary>Reads a trace's allocation ticks and collections.</summary>
    /// <param name="path">A <c>.nettrace</c> file holding the runtime provider's GC events at verbose level with stacks
    /// (<see cref="CollectAsync"/>, or <c>dotnet-trace collect --profile gc-verbose</c>).</param>
    /// <param name="thread">Only this thread's ticks, or <see langword="null"/> for every thread.</param>
    /// <param name="fromMs">The first millisecond of the trace read; earlier ticks and collections are skipped.</param>
    /// <param name="toMs">The last millisecond of the trace read.</param>
    /// <param name="focus">Only the ticks whose stack holds an engine frame naming this text, each also counted under
    /// the path of engine frames from that frame outward (<see cref="AllocationReport.Paths"/>); <see langword="null"/>
    /// for every tick.</param>
    /// <returns>The report.</returns>
    public static AllocationReport Read(string path, int? thread, double fromMs = 0, double toMs = double.MaxValue, string? focus = null) {
        var etlx = TraceLog.CreateFromEventPipeDataFile(filePath: path, etlxFilePath: Path.ChangeExtension(path: path, extension: ".etlx"));

        using var log = new TraceLog(etlxFilePath: etlx);
        var source = log.Events.GetSource();
        var threads = new Dictionary<int, Share>();
        var types = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        var sites = new Dictionary<string, Share>(comparer: StringComparer.Ordinal);
        var frames = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var paths = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        var collections = new List<Collection>();
        long ticks = 0, bytes = 0, large = 0;
        var suspended = double.NaN;
        double first = double.NaN, last = 0;

        source.Clr.GCAllocationTick += data => {
            var at = data.TimeStampRelativeMSec;

            if (at < fromMs || at > toMs) { return; }
            if (double.IsNaN(first)) { first = at; }
            last = at;
            if (!threads.TryGetValue(key: data.ThreadID, value: out var share)) { threads[data.ThreadID] = share = new Share(); }

            var amount = data.AllocationAmount64;
            var stack = data.CallStack();
            var root = Root(stack: stack);

            share.Add(bytes: amount, part: root);
            if (thread is { } only && only != data.ThreadID) { return; }
            if (focus is not null) {
                if (FocusPath(focus: focus, stack: stack) is not { } path) { return; }
                paths[path] = paths.GetValueOrDefault(key: path) + amount;
            }
            ticks++;
            bytes += amount;
            if (data.AllocationKind != GCAllocationKind.Small) { large += amount; }

            var type = (data.TypeName is { Length: > 0 } name ? name : "?");

            types[type] = types.GetValueOrDefault(key: type) + amount;

            var site = Site(stack: stack);

            if (!sites.TryGetValue(key: site, value: out var siteShare)) { sites[site] = siteShare = new Share(); }
            siteShare.Add(bytes: amount, part: type);
            seen.Clear();
            for (var frame = stack; frame is not null; frame = frame.Caller) {
                var method = frame.CodeAddress.FullMethodName;

                if (Engine(method: method) && seen.Add(item: method)) { frames[method] = frames.GetValueOrDefault(key: method) + amount; }
            }
        };
        source.Clr.GCStart += data => {
            if (data.TimeStampRelativeMSec >= fromMs && data.TimeStampRelativeMSec <= toMs) { collections.Add(item: new Collection(Index: data.Count, Generation: data.Depth, Reason: data.Reason.ToString(), Type: data.Type.ToString(), AtMs: data.TimeStampRelativeMSec)); }
        };
        source.Clr.GCSuspendEEStart += data => suspended = data.TimeStampRelativeMSec;
        source.Clr.GCRestartEEStop += data => {
            if (double.IsNaN(suspended)) { return; }

            var pause = data.TimeStampRelativeMSec - suspended;

            suspended = double.NaN;
            // A pause belongs to the last collection that started inside it; a suspension with none (a debugger, a
            // profiler) is not a collection's pause.
            for (var index = collections.Count - 1; index >= 0 && collections[index].AtMs >= data.TimeStampRelativeMSec - pause; index--) {
                if (collections[index].PauseMs == 0) { collections[index] = collections[index] with { PauseMs = pause }; break; }
            }
        };
        source.Process();

        return new AllocationReport(
            Ticks: ticks,
            Bytes: bytes,
            LargeBytes: large,
            Seconds: (double.IsNaN(first) ? 0 : (last - first) / 1000),
            Threads: [.. threads.OrderByDescending(static pair => pair.Value.Bytes).Select(static pair => new AllocationThread(Id: pair.Key, Bytes: pair.Value.Bytes, Root: pair.Value.Parts.OrderByDescending(static root => root.Value).Select(static root => root.Key).FirstOrDefault() ?? "?"))],
            Types: Ranked(totals: types),
            Sites: [.. sites.OrderByDescending(static pair => pair.Value.Bytes).Select(static pair => new AllocationSite(Method: pair.Key, Bytes: pair.Value.Bytes, Types: Ranked(totals: pair.Value.Parts)))],
            Frames: Ranked(totals: frames),
            Collections: collections,
            Paths: Ranked(totals: paths)
        );
    }

    private static IReadOnlyList<KeyValuePair<string, long>> Ranked(Dictionary<string, long> totals) =>
        [.. totals.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal)];

    // Whether a frame is the engine's or the program's own code, rather than the runtime's or the libraries'.
    internal static bool Engine(string method) => method.Length > 0 && !method.StartsWith(value: "System.", comparisonType: StringComparison.Ordinal) &&
        !method.StartsWith(value: "Microsoft.", comparisonType: StringComparison.Ordinal) && !method.StartsWith(value: "Interop", comparisonType: StringComparison.Ordinal) &&
        !method.StartsWith(value: "?", comparisonType: StringComparison.Ordinal) && !method.StartsWith(value: "ROOT", comparisonType: StringComparison.Ordinal) &&
        !method.StartsWith(value: "Thread (", comparisonType: StringComparison.Ordinal) && !method.StartsWith(value: "Process", comparisonType: StringComparison.Ordinal);

    // The engine frames from the innermost one naming the focus outward, a few deep, or null when no frame names it.
    private static string? FocusPath(string focus, TraceCallStack? stack) {
        for (var frame = stack; frame is not null; frame = frame.Caller) {
            if (!frame.CodeAddress.FullMethodName.Contains(value: focus, comparisonType: StringComparison.Ordinal)) { continue; }

            var names = new List<string>(capacity: PathDepth);

            for (var outer = frame; (outer is not null) && (names.Count < PathDepth); outer = outer.Caller) {
                var method = outer.CodeAddress.FullMethodName;

                if (Engine(method: method)) { names.Add(item: method[..Math.Max(val1: 0, val2: method.IndexOf(value: '(', comparisonType: StringComparison.Ordinal) is var open and >= 0 ? open : method.Length)]); }
            }
            return string.Join(separator: " < ", values: names);
        }
        return null;
    }
    private const int PathDepth = 7;

    // The allocating call site: the innermost engine frame, so a list's growth is charged to the code that added to it.
    private static string Site(TraceCallStack? stack) {
        for (var frame = stack; frame is not null; frame = frame.Caller) {
            var method = frame.CodeAddress.FullMethodName;

            if (Engine(method: method)) { return method; }
        }
        return (stack?.CodeAddress.FullMethodName is { Length: > 0 } leaf ? leaf : "?");
    }
    // The outermost engine frame, which names what a thread is for (the host loop, a bake worker).
    internal static string? Root(TraceCallStack? stack) {
        string? outer = null;

        for (var frame = stack; frame is not null; frame = frame.Caller) {
            var method = frame.CodeAddress.FullMethodName;

            if (Engine(method: method)) { outer = method; }
        }
        return outer;
    }

    // One thread's or site's bytes, and the same bytes by a name within it (a thread's roots, a site's types).
    private sealed class Share {
        public long Bytes;
        public Dictionary<string, long> Parts { get; } = new(comparer: StringComparer.Ordinal);

        public void Add(string? part, long bytes) {
            Bytes += bytes;
            if (part is not null) { Parts[part] = Parts.GetValueOrDefault(key: part) + bytes; }
        }
    }
}
/// <summary>A trace's allocation, as its ticks sampled it.</summary>
/// <param name="Ticks">The allocation ticks read.</param>
/// <param name="Bytes">The bytes they account for.</param>
/// <param name="LargeBytes">Of those, the bytes ticks charged to the large or pinned object heap.</param>
/// <param name="Seconds">The span from the first tick to the last.</param>
/// <param name="Threads">Every thread's bytes, largest first, whatever thread the report reads.</param>
/// <param name="Types">The bytes by type, largest first.</param>
/// <param name="Sites">The bytes by allocating call site (the innermost engine frame), largest first.</param>
/// <param name="Frames">The bytes beneath each engine frame (each frame counted once a stack), largest first.</param>
/// <param name="Collections">The collections that started in the trace.</param>
/// <param name="Paths">With a focus, the bytes by the path of engine frames from the focused frame outward, largest first;
/// empty without one.</param>
public sealed record AllocationReport(long Ticks, long Bytes, long LargeBytes, double Seconds, IReadOnlyList<AllocationThread> Threads,
    IReadOnlyList<KeyValuePair<string, long>> Types, IReadOnlyList<AllocationSite> Sites, IReadOnlyList<KeyValuePair<string, long>> Frames,
    IReadOnlyList<Collection> Collections, IReadOnlyList<KeyValuePair<string, long>> Paths);
/// <summary>One thread's sampled allocation.</summary>
/// <param name="Id">The operating system's thread id.</param>
/// <param name="Bytes">The bytes its ticks account for.</param>
/// <param name="Root">The outermost engine frame its most allocating stacks share.</param>
public sealed record AllocationThread(int Id, long Bytes, string Root);
/// <summary>One allocating call site.</summary>
/// <param name="Method">The innermost engine frame.</param>
/// <param name="Bytes">The bytes its ticks account for.</param>
/// <param name="Types">Its bytes by type, largest first.</param>
public sealed record AllocationSite(string Method, long Bytes, IReadOnlyList<KeyValuePair<string, long>> Types);
/// <summary>One collection.</summary>
/// <param name="Index">The runtime's collection count.</param>
/// <param name="Generation">The generation collected.</param>
/// <param name="Reason">Why it ran.</param>
/// <param name="Type">Blocking, background or a foreground collection during a background one.</param>
/// <param name="AtMs">When it started, in milliseconds from the trace's start.</param>
/// <param name="PauseMs">The suspension it ran in, from suspension's start to the restart's end; 0 for a background
/// collection's concurrent phase.</param>
public sealed record Collection(int Index, int Generation, string Reason, string Type, double AtMs, double PauseMs = 0);
