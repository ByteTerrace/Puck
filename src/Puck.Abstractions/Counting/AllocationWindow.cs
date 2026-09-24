using System.Diagnostics.Tracing;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Puck.Abstractions.Counting;

/// <summary>
/// Measures what a body allocates on the calling thread (<see cref="GC.GetAllocatedBytesForCurrentThread"/>), the one
/// managed-allocation meter every law, stage and diagnostic shares. <see cref="Least"/> is the zero law: a body that
/// allocates does so every time it runs, and one that does not reads zero in some window. <see cref="Measure"/> reads
/// the same least as a count, for a ceiling. <see cref="Total"/> counts the one run of a body whose single run is the
/// subject.
/// </summary>
/// <remarks>
/// <para>
/// The runtime's own work on the calling thread — a promoted method resolving its literals and handles on first
/// execution — lands in whichever window happens to be open, so one window alone cannot tell it from the body's
/// allocation; the least of up to <see cref="MaximumWindows"/> windows can. A collection another thread provokes does
/// not disturb the count when the host runs without background GC; with background GC, retiring this thread's
/// allocation context can count its unused remainder, so a verdict names the GC mode it was taken under
/// (<see cref="GcMode"/>).
/// </para>
/// <para>
/// A non-zero verdict is diagnosed before it is reported: the body runs again under an <see cref="EventListener"/>
/// enabled for the runtime's <c>AllocationSampled</c> event (event 303 of <c>Microsoft-Windows-DotNETRuntime</c>,
/// keyword <c>AllocationSamplingKeyword</c> = <c>0x80000000000</c>, .NET 10), and the thrown
/// <see cref="AllocationWindowException"/> names the types sampled on this thread. The runtime samples about one
/// allocation per 100 KB allocated, so the diagnosis repeats the body until it has allocated several sampling
/// intervals' worth, within a bounded number of runs. The event reaches an in-process listener on the runtime's
/// dispatch thread and carries no stack, so the culprit is named by type and count only.
/// </para>
/// </remarks>
public static class AllocationWindow {
    /// <summary>The most windows one measurement opens.</summary>
    public const int MaximumWindows = 16;

    // The runtime's AllocationSampled event: Microsoft-Windows-DotNETRuntime event 303 under AllocationSamplingKeyword.
    private const int AllocationSampledEventId = 303;
    private const long AllocationSamplingKeyword = 0x80000000000L;
    // The runtime's mean sampling distance, and how many of them a diagnosis allocates before it stops.
    private const long SamplingDistanceBytes = (100L * 1024L);
    private const long DiagnosisIntervals = 16L;
    private const int MaximumDiagnosisRuns = (1 << 16);
    private const string RuntimeSourceName = "Microsoft-Windows-DotNETRuntime";
    private const int SampledTypesNamed = 8;

    /// <summary>Gets the host's GC mode, as a verdict reports it: <c>workstation</c> or <c>server</c>, then
    /// <c>concurrent</c> or <c>non-concurrent</c>.</summary>
    public static string GcMode =>
        $"{(GCSettings.IsServerGC ? "server" : "workstation")}, {((GCSettings.LatencyMode == GCLatencyMode.Batch) ? "non-concurrent" : "concurrent")}";

    /// <summary>Runs <paramref name="window"/> until one run allocates nothing on the calling thread, at most
    /// <see cref="MaximumWindows"/> times, and returns zero; when every run allocated, diagnoses the body and throws.</summary>
    /// <param name="window">The warmed body. It runs more than once, so it leaves its subject able to run again.</param>
    /// <param name="name">The window's name in a verdict; the caller's member name unless given.</param>
    /// <returns>Zero: some run allocated nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is <see langword="null"/>.</exception>
    /// <exception cref="AllocationWindowException">Every run allocated; the exception carries the fewest bytes any run
    /// allocated, the GC mode, and the types the diagnosis sampled.</exception>
    public static long Least(Action window, [CallerMemberName] string name = "") {
        ArgumentNullException.ThrowIfNull(window);

        var least = Measure(window: window);

        if (least == 0L) {
            return 0L;
        }

        throw new AllocationWindowException(
            bytes: least,
            gcMode: GcMode,
            name: name,
            sampledTypes: Diagnose(
                least: least,
                window: window
            )
        );
    }
    /// <summary>Runs <paramref name="window"/> until one run allocates nothing, at most <see cref="MaximumWindows"/>
    /// times, and returns the fewest bytes any run allocated, without diagnosing a non-zero result.</summary>
    /// <param name="window">The warmed body. It runs more than once, so it leaves its subject able to run again.</param>
    /// <returns>The fewest bytes any run allocated on the calling thread; zero when some run allocated nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is <see langword="null"/>.</exception>
    public static long Measure(Action window) {
        ArgumentNullException.ThrowIfNull(window);

        var least = long.MaxValue;

        for (var index = 0; ((index < MaximumWindows) && (least != 0L)); index++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            window();
            least = Math.Min(
                val1: least,
                val2: (GC.GetAllocatedBytesForCurrentThread() - before)
            );
        }

        return least;
    }
    /// <summary>Runs <paramref name="window"/> exactly once and returns every byte it allocated on the calling thread:
    /// the count for a body whose one run is the subject — a flow that cannot run again (a boot, a first
    /// configuration), one sample of a sequence whose runs differ (a live world's tick, gathered for a median), or a
    /// batch the caller divides into a mean. One run cannot tell the runtime's own first-run work from the body's, so
    /// a zero law reads <see cref="Least"/> instead.</summary>
    /// <param name="window">The body, run once.</param>
    /// <returns>The bytes the run allocated on the calling thread.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is <see langword="null"/>.</exception>
    public static long Total(Action window) {
        ArgumentNullException.ThrowIfNull(window);

        var before = GC.GetAllocatedBytesForCurrentThread();

        window();

        return (GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Runs the body under the sampling listener until it has allocated DiagnosisIntervals sampling distances, then
    // waits for the dispatch thread to deliver what was sampled, and returns the types sampled on this thread, most
    // bytes first.
    private static IReadOnlyList<AllocationSample> Diagnose(Action window, long least) {
        var thread = CurrentOsThreadId();
        using var listener = new SamplingListener();
        var runs = ((int)Math.Clamp(
            max: MaximumDiagnosisRuns,
            min: 1L,
            value: ((((SamplingDistanceBytes * DiagnosisIntervals) + least) - 1L) / least)
        ));

        for (var run = 0; (run < runs); run++) {
            window();
        }

        listener.Drain();

        return listener.Sampled(thread: thread);
    }
    // The calling thread's operating-system id, which AllocationSampled's OSThreadId carries; zero where the platform
    // offers no cheap read, which disables the filter.
    private static long CurrentOsThreadId() {
        try {
            if (OperatingSystem.IsWindows()) {
                return NativeThread.GetCurrentThreadIdWindows();
            }

            if (OperatingSystem.IsLinux()) {
                return NativeThread.GetCurrentThreadIdLinux();
            }
        } catch (Exception exception) when ((exception is DllNotFoundException or EntryPointNotFoundException)) {
            // A C library without gettid (glibc before 2.30) leaves the diagnosis unfiltered.
        }

        return 0L;
    }

    private static class NativeThread {
        [DllImport(dllName: "kernel32", EntryPoint = "GetCurrentThreadId")]
        public static extern uint GetCurrentThreadIdWindows();
        [DllImport(dllName: "libc", EntryPoint = "gettid")]
        public static extern int GetCurrentThreadIdLinux();
    }
    private sealed class SamplingListener : EventListener {
        private readonly Lock m_gate = new();
        private readonly List<(long Thread, string Type, long Bytes)> m_samples = [];

        private int m_delivered;

        public void Drain() {
            // Delivery is asynchronous: wait until a quiet interval passes with nothing new, bounded to a second.
            var seen = -1;

            for (var wait = 0; ((wait < 10) && (Volatile.Read(location: ref m_delivered) != seen)); wait++) {
                seen = Volatile.Read(location: ref m_delivered);
                Thread.Sleep(millisecondsTimeout: 100);
            }
        }
        public IReadOnlyList<AllocationSample> Sampled(long thread) {
            lock (m_gate) {
                return [.. m_samples
                    .Where(predicate: sample => ((thread == 0L) || (sample.Thread == thread)))
                    .GroupBy(keySelector: static sample => sample.Type)
                    .Select(selector: static group => new AllocationSample(
                        Bytes: group.Sum(selector: static sample => sample.Bytes),
                        Samples: group.Count(),
                        TypeName: group.Key
                    ))
                    .OrderByDescending(keySelector: static sample => sample.Bytes)
                    .ThenBy(
                        comparer: StringComparer.Ordinal,
                        keySelector: static sample => sample.TypeName
                    )
                    .Take(count: SampledTypesNamed)];
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource) {
            if (string.Equals(
                a: eventSource.Name,
                b: RuntimeSourceName,
                comparisonType: StringComparison.Ordinal
            )) {
                EnableEvents(
                    eventSource: eventSource,
                    level: EventLevel.Informational,
                    matchAnyKeyword: ((EventKeywords)AllocationSamplingKeyword)
                );
            }
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData) {
            if ((eventData.EventId != AllocationSampledEventId) || (eventData.Payload is not { } payload) || (eventData.PayloadNames is not { } names)) {
                return;
            }

            var type = "?";
            var bytes = 0L;

            for (var index = 0; (index < names.Count); index++) {
                switch (names[index]) {
                    case "TypeName":
                        type = ((payload[index] as string) ?? type);

                        break;
                    case "ObjectSize":
                        bytes = Convert.ToInt64(value: payload[index], provider: System.Globalization.CultureInfo.InvariantCulture);

                        break;
                }
            }

            lock (m_gate) {
                m_samples.Add(item: (eventData.OSThreadId, type, bytes));
            }

            _ = Interlocked.Increment(location: ref m_delivered);
        }
    }
}
/// <summary>One type an allocation diagnosis sampled: how many of its allocations were sampled and their total size.
/// Sampling is proportional to bytes allocated, so the type with the most sampled bytes allocated the most.</summary>
/// <param name="TypeName">The allocated type's name as the runtime reports it.</param>
/// <param name="Samples">How many of its allocations were sampled.</param>
/// <param name="Bytes">The sampled allocations' total size in bytes.</param>
public readonly record struct AllocationSample(string TypeName, int Samples, long Bytes);
/// <summary>A body measured by <see cref="AllocationWindow.Least"/> allocated in every window; the message names the
/// window, the fewest bytes, the GC mode, and the types the diagnosis sampled.</summary>
public sealed class AllocationWindowException : Exception {
    /// <summary>Initializes a new instance of the <see cref="AllocationWindowException"/> class.</summary>
    /// <param name="name">The window's name.</param>
    /// <param name="bytes">The fewest bytes any window allocated.</param>
    /// <param name="gcMode">The host's GC mode.</param>
    /// <param name="sampledTypes">The types the diagnosis sampled, most bytes first.</param>
    public AllocationWindowException(string name, long bytes, string gcMode, IReadOnlyList<AllocationSample> sampledTypes)
        : base(message: Describe(
            bytes: bytes,
            gcMode: gcMode,
            name: name,
            sampledTypes: sampledTypes
        )) {
        Bytes = bytes;
        GcMode = gcMode;
        SampledTypes = sampledTypes;
        WindowName = name;
    }

    /// <summary>Gets the fewest bytes any window allocated.</summary>
    public long Bytes { get; }
    /// <summary>Gets the host's GC mode the verdict was taken under.</summary>
    public string GcMode { get; }
    /// <summary>Gets the types the diagnosis sampled on the measuring thread, most bytes first; empty when the
    /// runtime sampled none.</summary>
    public IReadOnlyList<AllocationSample> SampledTypes { get; }
    /// <summary>Gets the window's name.</summary>
    public string WindowName { get; }

    private static string Describe(string name, long bytes, string gcMode, IReadOnlyList<AllocationSample> sampledTypes) {
        var text = new StringBuilder();

        _ = text.Append(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"Allocation window '{name}' allocated in every one of {AllocationWindow.MaximumWindows} windows, at least {bytes} bytes ({gcMode} GC); "
        );

        if (sampledTypes.Count == 0) {
            return text.Append(value: "the runtime sampled no allocation on this thread while the body ran again.").ToString();
        }

        _ = text.Append(value: "sampled on this thread while the body ran again: ");

        for (var index = 0; (index < sampledTypes.Count); index++) {
            var sample = sampledTypes[index];

            _ = text.Append(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"{((index == 0) ? string.Empty : ", ")}{sample.TypeName} sampled {sample.Samples} times ({sample.Bytes} bytes)"
            );
        }

        return text.Append(value: '.').ToString();
    }
}
