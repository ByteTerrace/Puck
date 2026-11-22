using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.Hosting;

/// <summary>Named parts of produce and simulation on the frame thread.</summary>
public enum HostFrameWorkPart {
    /// <summary>Polls the presentation inputs and render packages.</summary>
    Pump,
    /// <summary>Captures the scene and its cameras.</summary>
    Capture,
    /// <summary>Packs a captured frame into render tables.</summary>
    Pack,
    /// <summary>Stages lighting.</summary>
    Lighting,
    /// <summary>Meters and stages exposure.</summary>
    Exposure,
    /// <summary>Builds the graph schedule.</summary>
    Schedule,
    /// <summary>Prepares the scheduled render packages.</summary>
    Prepare,
    /// <summary>Builds and records the scheduled renders.</summary>
    Record,
    /// <summary>Flushes the render batch.</summary>
    Flush,
    /// <summary>Applies tick inputs and pending commands.</summary>
    TickIngress,
    /// <summary>Runs the bodies' producer programs.</summary>
    TickProducers,
    /// <summary>Admits the snapshots the bodies' own producers published.</summary>
    TickAdmission,
    /// <summary>Advances bodies and their constraints.</summary>
    TickMotion,
    /// <summary>Solves dynamic contacts.</summary>
    TickContact,
    /// <summary>Collects the tick's collision, region and link events.</summary>
    TickEvents,
    /// <summary>Evaluates world rules and interactions.</summary>
    TickRules,
    /// <summary>Steps board enforcement, searches, fields and deadlines.</summary>
    TickFields,
    /// <summary>Installs placement responses and deals.</summary>
    TickResponses,
    /// <summary>Advances emulated machines.</summary>
    TickMachines,
    /// <summary>Delivers definition changes and snapshots.</summary>
    TickDelivery,
    /// <summary>Hashes and compares the scoped replays.</summary>
    TickReplay,
    /// <summary>The number of named parts.</summary>
    Count
}

/// <summary>Exclusive work durations in stopwatch ticks, indexed by <see cref="HostFrameWorkPart"/>.</summary>
[InlineArray((int)HostFrameWorkPart.Count)]
public struct HostFrameWorkTimes {
    private long m_ticks;
}

/// <summary>One world instance's work within a host iteration, in stopwatch ticks.</summary>
/// <param name="World">The instance's identity.</param>
/// <param name="Steps">The completed world steps.</param>
/// <param name="Ticks">The time those steps took.</param>
public record struct HostWorldWork(string World, int Steps, long Ticks) {
    /// <summary>The named parts of those steps.</summary>
    public HostFrameWorkTimes Parts;
}

/// <summary>Collects exclusive timings for the iteration the frame thread is running.</summary>
public sealed class HostFrameWork {
    [ThreadStatic]
    private static HostFrameWork? Active;
    private readonly Dictionary<string, int> m_worlds = new(StringComparer.Ordinal);
    private readonly List<HostWorldWork> m_worldWork = [];
    private HostFrameWorkTimes m_times;
    private HostFrameWorkPart m_part = HostFrameWorkPart.Count;
    private int m_world = -1;
    private long m_stamp;
    private long m_blocked;
    private long m_executed;

    /// <summary>The named work of this iteration.</summary>
    public HostFrameWorkTimes Times => m_times;
    /// <summary>World instance timings, held until <see cref="Clear"/>.</summary>
    public ReadOnlySpan<HostWorldWork> Worlds => CollectionsMarshal.AsSpan(m_worldWork);
    /// <summary>Gets the number of graph nodes scheduled in the current iteration.</summary>
    public int ScheduledRenders { get; private set; }

    /// <summary>Clears the current iteration after the host takes its readings.</summary>
    public void Clear() {
        m_times = default;
        m_worlds.Clear();
        m_worldWork.Clear();
        ScheduledRenders = 0;
        m_executed = 0L;
    }

    /// <summary>Adds the stopwatch ticks a step thread spent executing authority ticks to the calling thread's
    /// measurement: the tick's own cost, apart from what the frame thread spent preparing and collecting it.</summary>
    /// <param name="ticks">The stopwatch ticks the execution took.</param>
    public static void NoteExecuted(long ticks) {
        if (Active is { } work) { work.m_executed += ticks; }
    }
    /// <summary>Takes the stopwatch ticks of tick execution noted or folded in since the last take.</summary>
    /// <returns>The ticks, or zero when no step thread reported any.</returns>
    public long TakeExecuted() {
        var executed = m_executed;

        m_executed = 0L;

        return executed;
    }
    /// <summary>Adds the work another thread measured on the calling thread's behalf to the calling thread's
    /// measurement, world by world and part by part, then clears <paramref name="source"/>. The other thread must have
    /// finished: neither measurement is synchronized. With nothing bound on the calling thread the source is only
    /// cleared.</summary>
    /// <param name="source">The measurement the other thread bound while it worked.</param>
    public static void Fold(HostFrameWork source) {
        ArgumentNullException.ThrowIfNull(source);

        if ((Active is { } work) && !ReferenceEquals(work, source)) {
            for (var part = 0; (part < (int)HostFrameWorkPart.Count); part++) {
                work.m_times[part] += source.m_times[part];
            }
            foreach (ref readonly var row in CollectionsMarshal.AsSpan(source.m_worldWork)) {
                if (!work.m_worlds.TryGetValue(row.World, out var index)) {
                    index = work.m_worldWork.Count;
                    work.m_worlds.Add(row.World, index);
                    work.m_worldWork.Add(new HostWorldWork(row.World, 0, 0));
                }

                ref var target = ref CollectionsMarshal.AsSpan(work.m_worldWork)[index];

                target.Steps += row.Steps;
                target.Ticks += row.Ticks;
                for (var part = 0; (part < (int)HostFrameWorkPart.Count); part++) {
                    target.Parts[part] += row.Parts[part];
                }
            }
            work.m_executed += source.m_executed;
            work.ScheduledRenders += source.ScheduledRenders;
        }
        source.Clear();
    }

    /// <summary>Enables measurements on the calling thread for the duration of a host phase.</summary>
    public Binding Bind() {
        var previous = Active;
        Active = this;
        return new Binding(previous);
    }

    /// <summary>Restores the thread's prior measurement context.</summary>
    public readonly ref struct Binding(HostFrameWork? previous) {
        /// <summary>Restores the prior context.</summary>
        public void Dispose() => Active = previous;
    }

    /// <summary>Measures a part, deducting time attributed to nested parts and to GPU fences.</summary>
    public static PartScope Measure(HostFrameWorkPart part) {
        if (Active is not { } work) { return default; }
        var previous = work.m_part;
        work.SwitchPart(part);
        return new PartScope(work, previous);
    }

    /// <summary>Attributes a completed world step and its named phases to the instance that ran it.</summary>
    public static WorldScope MeasureWorld(string world) {
        if (Active is not { } work) { return default; }
        if (!work.m_worlds.TryGetValue(world, out var index)) {
            index = work.m_worldWork.Count;
            work.m_worlds.Add(world, index);
            work.m_worldWork.Add(new HostWorldWork(world, 0, 0));
        }
        var previous = work.m_world;
        work.m_world = index;
        return new WorldScope(work, index, previous, Stopwatch.GetTimestamp());
    }

    /// <summary>Records how many graph nodes this frame scheduled.</summary>
    public static void NoteScheduledRenders(int count) {
        if (Active is { } work) { work.ScheduledRenders += count; }
    }

    private void SwitchPart(HostFrameWorkPart next) {
        var now = Stopwatch.GetTimestamp();
        var blocked = GpuFenceBlocking.Blocked;
        if (m_part != HostFrameWorkPart.Count) {
            var ticks = Math.Max(0L, now - m_stamp - (blocked - m_blocked));
            m_times[(int)m_part] += ticks;
            if (m_world >= 0) { CollectionsMarshal.AsSpan(m_worldWork)[m_world].Parts[(int)m_part] += ticks; }
        }
        m_part = next;
        m_stamp = now;
        m_blocked = blocked;
    }

    /// <summary>A named part's measurement.</summary>
    public readonly ref struct PartScope(HostFrameWork? work, HostFrameWorkPart previous) {
        /// <summary>Closes the current part and starts its successor at the same boundary.</summary>
        public void Next(HostFrameWorkPart part) => work?.SwitchPart(part);
        /// <summary>Closes this part and resumes the enclosing part.</summary>
        public void Dispose() => work?.SwitchPart(previous);
    }

    /// <summary>A world step's measurement.</summary>
    public readonly ref struct WorldScope(HostFrameWork? work, int index, int previous, long start) {
        /// <summary>Counts the step once it reaches its publication boundary.</summary>
        public void Complete() {
            if (work is not null) { CollectionsMarshal.AsSpan(work.m_worldWork)[index].Steps++; }
        }
        /// <summary>Closes the world step's measurement.</summary>
        public void Dispose() {
            if (work is null) { return; }
            ref var row = ref CollectionsMarshal.AsSpan(work.m_worldWork)[index];
            row.Ticks += Stopwatch.GetTimestamp() - start;
            work.m_world = previous;
        }
    }
}
