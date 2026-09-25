namespace Puck.Hosting;

/// <summary>Schedules render-graph instances by demand. A schedule is a pure function of the instance set, what the
/// frame shows (<see cref="RenderGraphFrame"/>), and the previous frame's <see cref="RenderGraphHistory"/>:
/// <list type="bullet">
/// <item><description>An instance renders only when the display shows it or an instance rendering this frame shows it,
/// and at most once a frame however many consumers read it.</description></item>
/// <item><description>It renders at the extent its largest footprint needs, the consumer's own extent times the
/// fraction it covers, quantized by <see cref="RenderGraphExtent"/>.</description></item>
/// <item><description>It renders only when its refresh is due: it has never rendered, or its resolved divisor of frames
/// has passed since it last did. Consumers of an instance that is not due read its latest completed output and never
/// wait for it.</description></item>
/// <item><description>Instances the display does not show directly spend at most the frame's pass-pixel budget. The
/// stalest due instance is admitted first, ties in render order, so an instance the budget defers is first in line on
/// the next frame; the display's own instances always render.</description></item>
/// <item><description>A read of an instance's own output, and a read declared previous-frame, samples the producer's
/// latest output completed before this frame, so a mirror facing itself shows the previous frame.</description></item>
/// <item><description>A buffer read has no footprint: every consumer that renders reads it, so its producer is demanded
/// whenever one of its consumers is, orders and refreshes as a shown producer does, and renders at no extent for no
/// pass-pixels.</description></item>
/// </list>
/// </summary>
public static class RenderGraphScheduler {
    // One producer a consumer reads this frame: an image it shows over a footprint, or a buffer (no extent).
    internal readonly record struct Shown(int Producer, double Width, double Height, bool PreviousFrame, ShaderPipelineResourceKind Kind);
    // The working state of one schedule, sized to its set and cleared at the start of every call, so nothing from an
    // earlier frame reaches a result.
    internal sealed class Scratch {
        public Scratch(int count) {
            Admitted = new bool[count];
            Candidates = new int[count];
            Decided = new bool[count];
            Deferred = new bool[count];
            Demanded = new bool[count];
            DemandHeight = new double[count];
            DemandWidth = new double[count];
            Divisor = new int[count];
            Due = new bool[count];
            Height = new int[count];
            IsRoot = new bool[count];
            PositionOf = new int[count];
            Price = new long[count];
            ScaleHeight = new double[count];
            ScaleWidth = new double[count];
            Shows = new List<Shown>[count];
            Staleness = new long[count];
            Width = new int[count];

            for (var index = 0; (index < count); index++) {
                Shows[index] = [];
            }
        }

        public bool[] Admitted { get; }
        public int[] Candidates { get; }
        public bool[] Decided { get; }
        public bool[] Deferred { get; }
        public double[] DemandHeight { get; }
        public double[] DemandWidth { get; }
        public bool[] Demanded { get; }
        public int[] Divisor { get; }
        public bool[] Due { get; }
        public int[] Height { get; }
        public bool[] IsRoot { get; }
        public int[] PositionOf { get; }
        public long[] Price { get; }
        public double[] ScaleHeight { get; }
        public double[] ScaleWidth { get; }
        public List<Shown>[] Shows { get; }
        public long[] Staleness { get; }
        public int[] Width { get; }

        public void Clear() {
            Array.Clear(array: Admitted);
            Array.Clear(array: Decided);
            Array.Clear(array: Deferred);
            Array.Clear(array: Demanded);
            Array.Clear(array: DemandHeight);
            Array.Clear(array: DemandWidth);
            Array.Clear(array: Height);
            Array.Clear(array: IsRoot);
            Array.Clear(array: Price);
            Array.Clear(array: ScaleHeight);
            Array.Clear(array: ScaleWidth);
            Array.Clear(array: Width);

            foreach (var list in Shows) {
                list.Clear();
            }
        }
    }

    private static double Fraction(double value, string what) {
        if (
            !double.IsFinite(d: value) ||
            (value < 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: value,
                message: $"{what} must be a finite, non-negative fraction.",
                paramName: nameof(value)
            );
        }

        return Math.Min(
            val1: value,
            val2: 1.0
        );
    }
    private static void Footprints(RenderGraphInstanceSet set, RenderGraphFrame frame, List<Shown>[] shown) {
        var footprints = frame.Footprints;

        for (var position = 0; (position < footprints.Count); position++) {
            var footprint = footprints[position];
            var consumer = set.IndexOf(name: footprint.Consumer);
            var producer = set.IndexOf(name: footprint.Producer);

            if (
                (consumer < 0) ||
                (producer < 0)
            ) {
                throw new ArgumentException(
                    message: $"Footprint '{footprint.Consumer}' -> '{footprint.Producer}' names an undeclared instance.",
                    paramName: nameof(frame)
                );
            }

            var reads = set.Reads[consumer];
            RenderGraphEdge? declared = null;

            for (var read = 0; (read < reads.Count); read++) {
                if (reads[read].Producer == producer) {
                    declared = reads[read];

                    break;
                }
            }

            if (declared is not { } edge) {
                throw new ArgumentException(
                    message: $"Instance '{footprint.Consumer}' shows '{footprint.Producer}' but declares no read of it.",
                    paramName: nameof(frame)
                );
            }
            if (edge.Kind == ShaderPipelineResourceKind.Buffer) {
                throw new ArgumentException(
                    message: $"Instance '{footprint.Consumer}' shows '{footprint.Producer}', which it reads as a buffer; only an image has a footprint.",
                    paramName: nameof(frame)
                );
            }

            var width = Fraction(
                value: footprint.Width,
                what: "A footprint's width"
            );
            var height = Fraction(
                value: footprint.Height,
                what: "A footprint's height"
            );

            if (
                (width == 0) ||
                (height == 0)
            ) {
                continue;
            }

            var list = shown[consumer];
            var existing = -1;

            for (var entry = 0; (entry < list.Count); entry++) {
                if (list[entry].Producer == producer) {
                    existing = entry;

                    break;
                }
            }

            if (existing < 0) {
                list.Add(item: new Shown(
                    Height: height,
                    Kind: edge.Kind,
                    PreviousFrame: edge.PreviousFrame,
                    Producer: producer,
                    Width: width
                ));
            } else {
                list[existing] = (list[existing] with {
                    Height = Math.Max(
                        val1: list[existing].Height,
                        val2: height
                    ),
                    Width = Math.Max(
                        val1: list[existing].Width,
                        val2: width
                    ),
                });
            }
        }
        for (var consumer = 0; (consumer < shown.Length); consumer++) {
            var reads = set.Reads[consumer];

            for (var read = 0; (read < reads.Count); read++) {
                var edge = reads[read];

                if (edge.Kind == ShaderPipelineResourceKind.Buffer) {
                    shown[consumer].Add(item: new Shown(
                        Height: 0,
                        Kind: edge.Kind,
                        PreviousFrame: edge.PreviousFrame,
                        Producer: edge.Producer,
                        Width: 0
                    ));
                }
            }
        }
    }
    // The stalest instance first, ties in render order: a total order, so the sort's result never depends on its
    // algorithm.
    private static bool Precedes(int left, int right, long[] staleness, int[] positionOf) => ((staleness[left] != staleness[right])
        ? (staleness[left] > staleness[right])
        : (positionOf[left] < positionOf[right])
    );

    /// <summary>Schedules one frame into a schedule the caller owns.</summary>
    /// <param name="set">The instances.</param>
    /// <param name="frame">What the frame shows.</param>
    /// <param name="history">The previous frame's history: <see cref="RenderGraphHistory.Empty"/> or a fresh schedule's
    /// <see cref="RenderGraphSchedule.Next"/> for the first frame, otherwise the previous schedule's
    /// <see cref="RenderGraphSchedule.Next"/>.</param>
    /// <param name="schedule">The schedule to fill, created for a set of the same size. Every member it held is
    /// replaced, so the result depends on the other arguments alone; when this throws, it is left unchanged.</param>
    /// <exception cref="ArgumentNullException"><paramref name="set"/>, <paramref name="history"/> or
    /// <paramref name="schedule"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="history"/> or <paramref name="schedule"/> covers a
    /// different number of instances, <paramref name="history"/> is <paramref name="schedule"/>'s own
    /// <see cref="RenderGraphSchedule.Next"/>, the frame's roots or footprints are <see langword="null"/>, the frame
    /// does not follow the history, the display extent is not positive, a root or footprint names an undeclared
    /// instance or read, a root names an instance whose output is a buffer, or a footprint shows a buffer
    /// read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A root or footprint fraction is negative or not finite, or the
    /// budget is negative.</exception>
    public static void Schedule(RenderGraphInstanceSet set, RenderGraphFrame frame, RenderGraphHistory history, RenderGraphSchedule schedule) {
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: history);
        ArgumentNullException.ThrowIfNull(argument: schedule);
        ArgumentOutOfRangeException.ThrowIfNegative(value: frame.PassPixelBudget);

        var count = set.Instances.Count;

        if (history.Count != count) {
            throw new ArgumentException(
                message: $"The history covers {history.Count} instances; the set declares {count}.",
                paramName: nameof(history)
            );
        }
        if (schedule.Count != count) {
            throw new ArgumentException(
                message: $"The schedule covers {schedule.Count} instances; the set declares {count}.",
                paramName: nameof(schedule)
            );
        }
        if (ReferenceEquals(
            objA: history,
            objB: schedule.Next
        )) {
            throw new ArgumentException(
                message: "The history is the schedule's own next history; schedule the next frame into another schedule.",
                paramName: nameof(schedule)
            );
        }
        if (
            (frame.Roots is null) ||
            (frame.Footprints is null)
        ) {
            throw new ArgumentException(
                message: "The frame's roots and footprints must not be null.",
                paramName: nameof(frame)
            );
        }
        if (frame.Index <= history.Frame) {
            throw new ArgumentException(
                message: $"Frame {frame.Index} does not follow the history's frame {history.Frame}.",
                paramName: nameof(frame)
            );
        }
        if (
            (frame.DisplayWidth <= 0) ||
            (frame.DisplayHeight <= 0)
        ) {
            throw new ArgumentException(
                message: $"The display extent {frame.DisplayWidth}x{frame.DisplayHeight} must be positive.",
                paramName: nameof(frame)
            );
        }

        // Every refusal is raised while only the scratch is written, so a refused call leaves the schedule's members
        // as they were.
        var work = schedule.Work;

        work.Clear();

        var isRoot = work.IsRoot;
        var demandWidth = work.DemandWidth;
        var demandHeight = work.DemandHeight;
        var roots = frame.Roots;

        for (var position = 0; (position < roots.Count); position++) {
            var root = roots[position];
            var index = set.IndexOf(name: root.Instance);

            if (index < 0) {
                throw new ArgumentException(
                    message: $"Root '{root.Instance}' names an undeclared instance.",
                    paramName: nameof(frame)
                );
            }
            if (set.Instances[index].Output == ShaderPipelineResourceKind.Buffer) {
                throw new ArgumentException(
                    message: $"Root '{root.Instance}' names an instance whose output is a buffer; the display shows images.",
                    paramName: nameof(frame)
                );
            }

            var rootWidth = Fraction(
                value: root.Width,
                what: "A root's width"
            );
            var rootHeight = Fraction(
                value: root.Height,
                what: "A root's height"
            );

            if (
                (rootWidth > 0) &&
                (rootHeight > 0)
            ) {
                isRoot[index] = true;
                demandWidth[index] = Math.Max(
                    val1: demandWidth[index],
                    val2: rootWidth
                );
                demandHeight[index] = Math.Max(
                    val1: demandHeight[index],
                    val2: rootHeight
                );
            }
        }

        var shown = work.Shows;

        Footprints(
            frame: frame,
            set: set,
            shown: shown
        );

        var divisor = work.Divisor;
        var due = work.Due;

        for (var index = 0; (index < count); index++) {
            var rendered = history.LatestFrame(index: index);

            divisor[index] = set.Instances[index].Refresh.ResolveDivisor(displayHertz: frame.DisplayHertz);
            due[index] = (
                (rendered < 0) ||
                ((frame.Index - rendered) >= divisor[index])
            );
        }

        // Consumers are decided before their same-frame producers, so a producer's demand is complete when it is
        // reached; a previous-frame read that arrives after its producer was decided adds no extent this frame. A buffer
        // read demands its producer without adding extent.
        var decided = work.Decided;
        var demanded = work.Demanded;
        var scaleWidth = work.ScaleWidth;
        var scaleHeight = work.ScaleHeight;
        var changed = true;

        while (changed) {
            changed = false;

            for (var position = (count - 1); (position >= 0); position--) {
                var index = set.Order[position];

                if (
                    decided[index] ||
                    ((demandWidth[index] == 0) && !demanded[index])
                ) {
                    continue;
                }

                var (allocatedWidth, allocatedHeight) = history.Allocated(index: index);

                decided[index] = true;
                changed = true;
                scaleWidth[index] = RenderGraphExtent.Quantize(
                    allocated: allocatedWidth,
                    fraction: demandWidth[index]
                );
                scaleHeight[index] = RenderGraphExtent.Quantize(
                    allocated: allocatedHeight,
                    fraction: demandHeight[index]
                );

                if (!due[index]) {
                    continue;
                }

                foreach (var entry in shown[index]) {
                    if (decided[entry.Producer]) {
                        continue;
                    }
                    if (entry.Kind == ShaderPipelineResourceKind.Buffer) {
                        demanded[entry.Producer] = true;

                        continue;
                    }

                    demandWidth[entry.Producer] = Math.Max(
                        val1: demandWidth[entry.Producer],
                        val2: (scaleWidth[index] * entry.Width)
                    );
                    demandHeight[entry.Producer] = Math.Max(
                        val1: demandHeight[entry.Producer],
                        val2: (scaleHeight[index] * entry.Height)
                    );
                }
            }
        }

        var width = work.Width;
        var height = work.Height;
        var price = work.Price;

        for (var index = 0; (index < count); index++) {
            if (
                decided[index] &&
                (set.Instances[index].Output != ShaderPipelineResourceKind.Buffer)
            ) {
                width[index] = RenderGraphExtent.Pixels(
                    display: frame.DisplayWidth,
                    fraction: scaleWidth[index]
                );
                height[index] = RenderGraphExtent.Pixels(
                    display: frame.DisplayHeight,
                    fraction: scaleHeight[index]
                );
                price[index] = checked(((((long)set.Instances[index].Passes) * width[index]) * height[index]));
            }
        }

        var positionOf = work.PositionOf;
        var staleness = work.Staleness;

        for (var position = 0; (position < count); position++) {
            positionOf[set.Order[position]] = position;
        }

        var admitted = work.Admitted;
        var deferred = work.Deferred;
        var candidates = work.Candidates;
        var candidateCount = 0;

        for (var index = 0; (index < count); index++) {
            if (
                !decided[index] ||
                !due[index]
            ) {
                continue;
            }
            if (isRoot[index]) {
                admitted[index] = true;
            } else {
                var last = history.LatestFrame(index: index);

                staleness[index] = ((last < 0)
                    ? long.MaxValue
                    : (frame.Index - last)
                );
                candidates[candidateCount++] = index;
            }
        }

        for (var sorted = 1; (sorted < candidateCount); sorted++) {
            var candidate = candidates[sorted];
            var slot = sorted;

            while (
                (slot > 0) &&
                Precedes(
                    left: candidate,
                    positionOf: positionOf,
                    right: candidates[(slot - 1)],
                    staleness: staleness
                )
            ) {
                candidates[slot] = candidates[(slot - 1)];
                slot--;
            }

            candidates[slot] = candidate;
        }

        var spent = 0L;

        for (var position = 0; (position < candidateCount); position++) {
            var index = candidates[position];

            if (
                (frame.PassPixelBudget == 0) ||
                ((spent + price[index]) <= frame.PassPixelBudget)
            ) {
                admitted[index] = true;
                spent += price[index];
            } else {
                deferred[index] = true;
            }
        }

        // A producer is rendered only for a consumer that renders: drop one whose every reader was deferred.
        for (var position = (count - 1); (position >= 0); position--) {
            var index = set.Order[position];

            if (
                !admitted[index] ||
                isRoot[index]
            ) {
                continue;
            }

            var read = false;

            for (var consumer = 0; ((consumer < count) && !read); consumer++) {
                if (!admitted[consumer]) {
                    continue;
                }

                foreach (var entry in shown[consumer]) {
                    if (entry.Producer == index) {
                        read = true;

                        break;
                    }
                }
            }

            admitted[index] = read;
        }

        var rows = schedule.InstanceRows;
        var renders = schedule.RenderRows;
        var reads = schedule.ReadRows;
        var following = schedule.Next;
        var total = 0L;

        renders.Clear();
        reads.Clear();

        for (var position = 0; (position < count); position++) {
            var index = set.Order[position];

            if (admitted[index]) {
                renders.Add(item: index);
            }
        }
        for (var index = 0; (index < count); index++) {
            var (allocatedWidth, allocatedHeight) = history.Allocated(index: index);
            var status = (admitted[index]
                ? RenderGraphInstanceStatus.Rendered
                : (deferred[index]
                    ? RenderGraphInstanceStatus.Deferred
                    : (decided[index]
                        ? RenderGraphInstanceStatus.Waiting
                        : RenderGraphInstanceStatus.Unread)))
            ;

            following.Latest[index] = (admitted[index]
                ? frame.Index
                : history.LatestFrame(index: index)
            );
            following.Width[index] = (admitted[index]
                ? scaleWidth[index]
                : allocatedWidth
            );
            following.Height[index] = (admitted[index]
                ? scaleHeight[index]
                : allocatedHeight
            );

            var spentHere = (admitted[index]
                ? price[index]
                : 0L
            );

            total += spentHere;
            rows[index] = new RenderGraphInstanceSchedule(
                Divisor: divisor[index],
                Height: (admitted[index]
                    ? height[index]
                    : ((allocatedHeight == 0)
                        ? 0
                        : RenderGraphExtent.Pixels(
                            display: frame.DisplayHeight,
                            fraction: allocatedHeight
                        ))),
                Instance: set.Instances[index].Name,
                IsRoot: isRoot[index],
                LatestFrame: following.Latest[index],
                Passes: set.Instances[index].Passes,
                PassPixels: spentHere,
                Status: status,
                Width: (admitted[index]
                    ? width[index]
                    : ((allocatedWidth == 0)
                        ? 0
                        : RenderGraphExtent.Pixels(
                            display: frame.DisplayWidth,
                            fraction: allocatedWidth
                        )))
            );
        }

        foreach (var consumer in renders) {
            foreach (var entry in shown[consumer]) {
                reads.Add(item: new RenderGraphReadSchedule(
                    Consumer: set.Instances[consumer].Name,
                    Frame: ((!entry.PreviousFrame && admitted[entry.Producer])
                        ? frame.Index
                        : history.LatestFrame(index: entry.Producer)),
                    Kind: entry.Kind,
                    PreviousFrame: entry.PreviousFrame,
                    Producer: set.Instances[entry.Producer].Name
                ));
            }
        }

        schedule.Publish(
            frame: frame.Index,
            passPixels: total
        );
    }
}
