using System.Diagnostics;
using Puck.Abstractions.Counting;

namespace Puck.Shaders;

/// <summary>
/// The host time render nodes spend on their frame record jobs, process-wide, in the three phases a job has: preparing
/// (the slot's fence wait, lease retirement, region binding and the cadence checkpoint), recording (the command lists)
/// and committing (submission, publication, readbacks). <c>frames</c> counts render-graph frames that recorded at
/// least one job; <c>longest-microseconds</c> sums each such frame's longest single recording, so divided by
/// <c>frames</c> it is the recording critical path a frame would keep if its jobs ran side by side, beside
/// <c>record-microseconds</c> divided by <c>frames</c>, the serial sum. <c>packages-microseconds</c> is the frame's
/// package preparation that precedes every job (an SDF residency's upload and background pump), which no job pays.
/// Wall-clock readings: pacing counts, never compared between runs.
/// </summary>
public sealed class RenderRecordWork : IWorkCounterSource {
    private static readonly WorkKind Frames = Kind(name: "render.record.frames", unit: "count");
    private static readonly WorkKind Jobs = Kind(name: "render.record.jobs", unit: "count");
    private static readonly WorkKind Failed = Kind(name: "render.record.failed", unit: "count");
    private static readonly WorkKind Prepare = Kind(name: "render.record.prepare-microseconds", unit: "microseconds");
    private static readonly WorkKind Record = Kind(name: "render.record.record-microseconds", unit: "microseconds");
    private static readonly WorkKind Commit = Kind(name: "render.record.commit-microseconds", unit: "microseconds");
    private static readonly WorkKind Longest = Kind(name: "render.record.longest-microseconds", unit: "microseconds");
    private static readonly WorkKind PackagePreparation = Kind(name: "render.record.packages-microseconds", unit: "microseconds");
    private static readonly WorkKind[] Kinds = [Frames, Jobs, Failed, Prepare, Record, Commit, Longest, PackagePreparation];
    private readonly WorkCounterSet m_counts = new(name: "render.record", kinds: Kinds);

    private RenderRecordWork() { }

    /// <summary>Gets the process's record work.</summary>
    public static RenderRecordWork Process { get; } = new();
    /// <inheritdoc/>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Kinds;
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) => m_counts.TryRead(kind, out value);

    /// <summary>Counts one committed job's phases.</summary>
    /// <param name="prepareTicks">The preparation's duration, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="recordTicks">The recording's duration, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="commitTicks">The commit's duration, in <see cref="Stopwatch"/> ticks.</param>
    internal void Job(long prepareTicks, long recordTicks, long commitTicks) {
        m_counts.Count(Jobs);
        m_counts.Add(Prepare, Microseconds(ticks: prepareTicks));
        m_counts.Add(Record, Microseconds(ticks: recordTicks));
        m_counts.Add(Commit, Microseconds(ticks: commitTicks));
    }
    /// <summary>Counts a frame's package preparation, the packages' shared frame work done before any job records
    /// (<see cref="IRenderGraphPackageFactory.PrepareRender"/>).</summary>
    /// <param name="ticks">The preparation's duration, in <see cref="Stopwatch"/> ticks.</param>
    internal void PreparedPackages(long ticks) => m_counts.Add(PackagePreparation, Microseconds(ticks: ticks));
    /// <summary>Counts one job whose recording failed and was abandoned.</summary>
    internal void Abandoned() => m_counts.Count(Failed);
    /// <summary>Counts one render-graph frame that recorded jobs, with its longest single recording.</summary>
    /// <param name="longestRecordTicks">The frame's longest recording, in <see cref="Stopwatch"/> ticks.</param>
    internal void Frame(long longestRecordTicks) {
        m_counts.Count(Frames);
        m_counts.Add(Longest, Microseconds(ticks: longestRecordTicks));
    }

    private static long Microseconds(long ticks) => ((ticks * 1_000_000L) / Stopwatch.Frequency);
    private static WorkKind Kind(string name, string unit) => new(name: name, unit: unit, workClass: WorkClass.Pacing);
}
