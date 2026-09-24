using System.Collections.ObjectModel;

namespace Puck.Hosting;

/// <summary>What the scheduler decided for one instance in one frame.</summary>
public enum RenderGraphInstanceStatus : byte {
    /// <summary>Neither the display nor any instance rendering this frame shows it, so it does not render.</summary>
    Unread = 1,
    /// <summary>Something shows it, but it does not render this frame: its refresh is not due, or no consumer that shows
    /// it renders. Its consumers read its latest completed output.</summary>
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
public readonly record struct RenderGraphReadSchedule(string Consumer, string Producer, bool PreviousFrame, long Frame);
/// <summary>The scheduler's per-instance memory between frames: when each instance last rendered and the extent its
/// targets are allocated at. It is a value the scheduler returns, never mutated in place.</summary>
public sealed class RenderGraphHistory {
    private readonly long[] m_latest;
    private readonly double[] m_width;
    private readonly double[] m_height;

    private RenderGraphHistory(long frame, long[] latest, double[] width, double[] height) {
        Frame = frame;
        m_latest = latest;
        m_width = width;
        m_height = height;
    }

    /// <summary>Gets the frame this history follows, or -1 before the first frame.</summary>
    public long Frame { get; }
    /// <summary>Gets the number of instances it covers.</summary>
    public int Count => m_latest.Length;

    internal static RenderGraphHistory Create(long frame, long[] latest, double[] width, double[] height) => new(
        frame: frame,
        height: height,
        latest: latest,
        width: width
    );

    /// <summary>Creates the history of a set that has not rendered.</summary>
    /// <param name="set">The instance set.</param>
    /// <returns>A history in which no instance has rendered or allocated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> is <see langword="null"/>.</exception>
    public static RenderGraphHistory Empty(RenderGraphInstanceSet set) {
        ArgumentNullException.ThrowIfNull(argument: set);

        var latest = new long[set.Instances.Count];

        Array.Fill(
            array: latest,
            value: -1L
        );

        return new RenderGraphHistory(
            frame: -1,
            height: new double[latest.Length],
            latest: latest,
            width: new double[latest.Length]
        );
    }
    /// <summary>Returns the frame an instance last rendered.</summary>
    /// <param name="index">The instance's index in its set.</param>
    /// <returns>The frame, or -1 when it has never rendered.</returns>
    public long LatestFrame(int index) => m_latest[index];
    /// <summary>Returns the extent an instance's targets are allocated at, as quantized fractions of the display.</summary>
    /// <param name="index">The instance's index in its set.</param>
    /// <returns>The width and height fractions, zero when never allocated.</returns>
    public (double Width, double Height) Allocated(int index) => (m_width[index], m_height[index]);
}
/// <summary>One frame's schedule: which instances render, in what order, at what extent and price, and which frame of
/// each producer every rendering consumer reads.</summary>
public sealed class RenderGraphSchedule {
    internal RenderGraphSchedule(long frame, IReadOnlyList<RenderGraphInstanceSchedule> instances, IReadOnlyList<int> renders, IReadOnlyList<RenderGraphReadSchedule> reads, long passPixels, RenderGraphHistory next) {
        Frame = frame;
        Instances = instances;
        Renders = renders;
        Reads = reads;
        PassPixels = passPixels;
        Next = next;
    }

    /// <summary>Gets the frame scheduled.</summary>
    public long Frame { get; }
    /// <summary>Gets every instance's row, parallel to <see cref="RenderGraphInstanceSet.Instances"/>.</summary>
    public IReadOnlyList<RenderGraphInstanceSchedule> Instances { get; }
    /// <summary>Gets the history the next frame is scheduled against.</summary>
    public RenderGraphHistory Next { get; }
    /// <summary>Gets the frame's total price: every render's passes times pixels.</summary>
    public long PassPixels { get; }
    /// <summary>Gets the reads of every rendering consumer that shows a producer this frame.</summary>
    public IReadOnlyList<RenderGraphReadSchedule> Reads { get; }
    /// <summary>Gets the instances that render, as indices into <see cref="Instances"/>, in render order: every
    /// same-frame producer before its consumers. Each instance appears at most once however many consumers read
    /// it.</summary>
    public IReadOnlyList<int> Renders { get; }

    internal static RenderGraphSchedule Create(long frame, RenderGraphInstanceSchedule[] instances, List<int> renders, List<RenderGraphReadSchedule> reads, long passPixels, RenderGraphHistory next) => new(
        frame: frame,
        instances: Array.AsReadOnly(array: instances),
        next: next,
        passPixels: passPixels,
        reads: new ReadOnlyCollection<RenderGraphReadSchedule>(list: reads),
        renders: new ReadOnlyCollection<int>(list: renders)
    );
}
