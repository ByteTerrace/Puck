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
/// Wall-clock readings: pacing counts, never compared between runs. <c>passes.recorded</c>, <c>passes.stood</c> and
/// <c>passes.skipped</c> count what each recorded job's passes did: recorded their work, stood on their latest output
/// because their signature and inputs held, or skipped a frame they had no work in. They are counts of work, and
/// over the same frames <c>passes.stood</c> against <c>passes.recorded</c> is how much of the graph a frame left
/// standing. A frame records its jobs in dependency waves (<c>waves</c>), each forked across the job pool as one partition
/// per group of jobs that share a recording domain (<c>partitions</c>); <c>jobs.worker</c> and <c>passes.worker</c> count
/// the jobs, and their recorded passes, that a worker thread recorded rather than the frame thread, and <c>lists</c> the
/// command lists the jobs handed to the queue.
/// </summary>
public sealed class RenderRecordWork : WorkLedger<WorkCounterSet> {
    private static readonly WorkKind Frames = Kind(name: "render.record.frames", unit: "count");
    private static readonly WorkKind Jobs = Kind(name: "render.record.jobs", unit: "count");
    private static readonly WorkKind Failed = Kind(name: "render.record.failed", unit: "count");
    private static readonly WorkKind Prepare = Kind(name: "render.record.prepare-microseconds", unit: "microseconds");
    private static readonly WorkKind Record = Kind(name: "render.record.record-microseconds", unit: "microseconds");
    private static readonly WorkKind Commit = Kind(name: "render.record.commit-microseconds", unit: "microseconds");
    private static readonly WorkKind Longest = Kind(name: "render.record.longest-microseconds", unit: "microseconds");
    private static readonly WorkKind PackagePreparation = Kind(name: "render.record.packages-microseconds", unit: "microseconds");
    private static readonly WorkKind PassesRecorded = Kind(name: "render.record.passes.recorded", unit: "count");
    private static readonly WorkKind PassesStood = Kind(name: "render.record.passes.stood", unit: "count");
    private static readonly WorkKind PassesSkipped = Kind(name: "render.record.passes.skipped", unit: "count");
    private static readonly WorkKind Waves = Kind(name: "render.record.waves", unit: "count");
    private static readonly WorkKind Partitions = Kind(name: "render.record.partitions", unit: "count");
    private static readonly WorkKind WorkerJobs = Kind(name: "render.record.jobs.worker", unit: "count");
    private static readonly WorkKind WorkerPasses = Kind(name: "render.record.passes.worker", unit: "count");
    private static readonly WorkKind Lists = Kind(name: "render.record.lists", unit: "count");
    private static readonly WorkKind[] Kinds = [Frames, Jobs, Failed, Prepare, Record, Commit, Longest, PackagePreparation, PassesRecorded, PassesStood, PassesSkipped, Waves, Partitions, WorkerJobs, WorkerPasses, Lists];

    private RenderRecordWork() : base(counters: new(name: "render.record", kinds: Kinds)) { }

    /// <summary>Gets the process's record work.</summary>
    public static RenderRecordWork Process { get; } = new();

    /// <summary>Counts one committed job's phases.</summary>
    /// <param name="prepareTicks">The preparation's duration, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="recordTicks">The recording's duration, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="commitTicks">The commit's duration, in <see cref="Stopwatch"/> ticks.</param>
    internal void Job(long prepareTicks, long recordTicks, long commitTicks) {
        Counters.Count(Jobs);
        Counters.Add(Prepare, Microseconds(ticks: prepareTicks));
        Counters.Add(Record, Microseconds(ticks: recordTicks));
        Counters.Add(Commit, Microseconds(ticks: commitTicks));
    }
    /// <summary>Counts a frame's package preparation, the packages' shared frame work done before any job records
    /// (<see cref="IRenderGraphPackageFactory.PrepareRender"/>).</summary>
    /// <param name="ticks">The preparation's duration, in <see cref="Stopwatch"/> ticks.</param>
    internal void PreparedPackages(long ticks) => Counters.Add(PackagePreparation, Microseconds(ticks: ticks));
    /// <summary>Counts what one recorded job's passes did.</summary>
    /// <param name="recorded">The passes that recorded their work.</param>
    /// <param name="stood">The passes that stood on their latest output.</param>
    /// <param name="skipped">The passes that skipped the frame.</param>
    internal void Passes(int recorded, int stood, int skipped) {
        Counters.Add(PassesRecorded, recorded);
        Counters.Add(PassesStood, stood);
        Counters.Add(PassesSkipped, skipped);
    }
    /// <summary>Counts one wave of record jobs and the partitions its fork ran them in.</summary>
    /// <param name="partitions">The wave's partitions: one per group of jobs sharing a recording domain.</param>
    internal void Wave(int partitions) {
        Counters.Count(Waves);
        Counters.Add(Partitions, partitions);
    }
    /// <summary>Counts one job a worker thread recorded rather than the frame thread.</summary>
    /// <param name="passes">The passes the job recorded.</param>
    internal void WorkerJob(int passes) {
        Counters.Count(WorkerJobs);
        Counters.Add(WorkerPasses, passes);
    }
    /// <summary>Counts the command lists one job handed to the queue.</summary>
    /// <param name="lists">The lists submitted.</param>
    internal void Submitted(int lists) => Counters.Add(Lists, lists);
    /// <summary>Counts one job whose recording failed and was abandoned.</summary>
    internal void Abandoned() => Counters.Count(Failed);
    /// <summary>Counts one render-graph frame that recorded jobs, with its longest single recording.</summary>
    /// <param name="longestRecordTicks">The frame's longest recording, in <see cref="Stopwatch"/> ticks.</param>
    internal void Frame(long longestRecordTicks) {
        Counters.Count(Frames);
        Counters.Add(Longest, Microseconds(ticks: longestRecordTicks));
    }

    private static long Microseconds(long ticks) => ((ticks * 1_000_000L) / Stopwatch.Frequency);
    private static WorkKind Kind(string name, string unit) => new(name: name, unit: unit, workClass: WorkClass.Pacing);
}
