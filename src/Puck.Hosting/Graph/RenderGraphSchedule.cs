using System.Collections.ObjectModel;

namespace Puck.Hosting;

/// <summary>What the scheduler decided for one instance in one frame.</summary>
public enum RenderGraphInstanceStatus : byte {
    /// <summary>Neither the display nor any instance rendering this frame shows or reads it, so it does not
    /// render.</summary>
    Unread = 1,
    /// <summary>Something shows or reads it, but it does not render this frame: its refresh is not due, or no consumer
    /// that shows or reads it renders. Its consumers read its latest completed output.</summary>
    Waiting = 2,
    /// <summary>It renders this frame.</summary>
    Rendered = 3,
    /// <summary>Its refresh is due, but rendering it would exceed the policy's pass-pixel budget; its consumers read
    /// its latest completed output, and it is first in line on a later frame.</summary>
    Deferred = 4,
}
/// <summary>One instance's row in a frame's schedule: its decision and its price.</summary>
/// <param name="Instance">The instance name.</param>
/// <param name="Status">What the scheduler decided.</param>
/// <param name="IsRoot">Whether the display shows it directly.</param>
/// <param name="Width">The width it renders at, in pixels, or its allocated width when it does not render this frame;
/// zero when it has never been allocated.</param>
/// <param name="Height">The height it renders at, in pixels, or its allocated height; zero when never allocated.</param>
/// <param name="Divisor">Its refresh resolved to a frame divisor.</param>
/// <param name="Passes">The passes one render records.</param>
/// <param name="PassPixels">Its price this frame: passes times pixels when it renders, otherwise zero.</param>
/// <param name="LatestFrame">The frame its latest completed output belongs to once this frame's renders complete, or -1
/// when it has never rendered.</param>
public readonly record struct RenderGraphInstanceSchedule(
    string Instance,
    RenderGraphInstanceStatus Status,
    bool IsRoot,
    int Width,
    int Height,
    int Divisor,
    int Passes,
    long PassPixels,
    long LatestFrame
);
/// <summary>Which frame of a producer's output one consumer reads this frame.</summary>
/// <param name="Consumer">The instance reading.</param>
/// <param name="Producer">The instance read.</param>
/// <param name="PreviousFrame">Whether the read is a previous-frame edge: declared so, or a read of the consumer's own
/// output.</param>
/// <param name="Frame">The frame of the producer's output the consumer samples: this frame when the producer renders
/// first on a same-frame edge, otherwise its latest output completed before this frame, or -1 when it has none.</param>
/// <param name="Kind">What the read carries: an image the consumer shows, or a buffer it reads.</param>
public readonly record struct RenderGraphReadSchedule(string Consumer, string Producer, bool PreviousFrame, long Frame, ShaderPipelineResourceKind Kind = ShaderPipelineResourceKind.Image);
/// <summary>The scheduler's per-instance memory between frames: when each instance last rendered and the extent its
/// targets are allocated at. A history a schedule carries as its <see cref="RenderGraphSchedule.Next"/> is rewritten
/// when that schedule is scheduled into again; <see cref="Empty"/> creates one nothing rewrites.</summary>
public sealed class RenderGraphHistory {
    private RenderGraphHistory(int count) {
        Frame = -1;
        Height = new double[count];
        Latest = new long[count];
        Width = new double[count];

        Array.Fill(
            array: Latest,
            value: -1L
        );
    }

    /// <summary>Gets the frame this history follows, or -1 before the first frame.</summary>
    public long Frame { get; internal set; }
    /// <summary>Gets the number of instances it covers.</summary>
    public int Count => Latest.Length;

    internal double[] Height { get; }
    internal long[] Latest { get; }
    internal double[] Width { get; }

    internal static RenderGraphHistory Of(int count) => new(count: count);

    /// <summary>Creates the history of a set that has not rendered.</summary>
    /// <param name="set">The instance set.</param>
    /// <returns>A history in which no instance has rendered or allocated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> is <see langword="null"/>.</exception>
    public static RenderGraphHistory Empty(RenderGraphInstanceSet set) {
        ArgumentNullException.ThrowIfNull(argument: set);

        return new RenderGraphHistory(count: set.Instances.Count);
    }
    /// <summary>Returns the frame an instance last rendered.</summary>
    /// <param name="index">The instance's index in its set.</param>
    /// <returns>The frame, or -1 when it has never rendered.</returns>
    public long LatestFrame(int index) => Latest[index];
    /// <summary>Returns the extent an instance's targets are allocated at, as quantized fractions of the display.</summary>
    /// <param name="index">The instance's index in its set.</param>
    /// <returns>The width and height fractions, zero when never allocated.</returns>
    public (double Width, double Height) Allocated(int index) => (Width[index], Height[index]);
}
/// <summary>One frame's schedule: which instances render, in what order, at what extent and price, and which frame of
/// each producer every rendering consumer reads.
/// <para>
/// A schedule is a buffer its caller owns and <see cref="RenderGraphScheduler.Schedule"/> fills, together with the
/// scratch the scheduler works in. Scheduling into it again replaces every member, <see cref="Next"/> included, and
/// allocates nothing once its read list has grown to the frame's reads, so a host alternating two schedules, each frame
/// scheduled against the other's <see cref="Next"/>, schedules a steady frame without allocating.
/// </para>
/// </summary>
public sealed class RenderGraphSchedule {
    private readonly RenderGraphInstanceSchedule[] m_instances;
    private readonly List<RenderGraphReadSchedule> m_reads;
    private readonly List<int> m_renders;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphSchedule"/> class: an empty schedule for a set's
    /// instances, whose <see cref="Frame"/> is -1 and whose <see cref="Next"/> is the history of a set that has not
    /// rendered.</summary>
    /// <param name="set">The instance set it is scheduled for.</param>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> is <see langword="null"/>.</exception>
    public RenderGraphSchedule(RenderGraphInstanceSet set) {
        ArgumentNullException.ThrowIfNull(argument: set);

        var count = set.Instances.Count;

        m_instances = new RenderGraphInstanceSchedule[count];
        m_reads = [];
        m_renders = new List<int>(capacity: count);
        Frame = -1;
        Instances = Array.AsReadOnly(array: m_instances);
        Next = RenderGraphHistory.Of(count: count);
        Reads = new ReadOnlyCollection<RenderGraphReadSchedule>(list: m_reads);
        Renders = new ReadOnlyCollection<int>(list: m_renders);
        Work = new RenderGraphScheduler.Scratch(count: count);
    }

    /// <summary>Gets the frame scheduled, or -1 before the schedule is first scheduled into.</summary>
    public long Frame { get; private set; }
    /// <summary>Gets every instance's row, parallel to <see cref="RenderGraphInstanceSet.Instances"/>.</summary>
    public IReadOnlyList<RenderGraphInstanceSchedule> Instances { get; }
    /// <summary>Gets the history the next frame is scheduled against. It belongs to this schedule, so the next frame is
    /// scheduled into another schedule.</summary>
    public RenderGraphHistory Next { get; }
    /// <summary>Gets the frame's total price: every render's passes times pixels.</summary>
    public long PassPixels { get; private set; }
    /// <summary>Gets the reads of every rendering consumer this frame: each producer it shows, then each buffer it
    /// reads.</summary>
    public IReadOnlyList<RenderGraphReadSchedule> Reads { get; }
    /// <summary>Gets the instances that render, as indices into <see cref="Instances"/>, in render order: every
    /// same-frame producer before its consumers. Each instance appears at most once however many consumers read
    /// it.</summary>
    public IReadOnlyList<int> Renders { get; }

    internal int Count => m_instances.Length;
    internal RenderGraphInstanceSchedule[] InstanceRows => m_instances;
    internal List<RenderGraphReadSchedule> ReadRows => m_reads;
    internal List<int> RenderRows => m_renders;
    internal RenderGraphScheduler.Scratch Work { get; }

    internal void Publish(long frame, long passPixels) {
        Frame = frame;
        PassPixels = passPixels;
        Next.Frame = frame;
    }
}
