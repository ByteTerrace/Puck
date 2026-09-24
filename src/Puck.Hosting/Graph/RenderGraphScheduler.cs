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
/// </list>
/// </summary>
public static class RenderGraphScheduler {
    private readonly record struct Shown(int Producer, double Width, double Height, bool PreviousFrame);

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
    private static List<Shown>[] Footprints(RenderGraphInstanceSet set, RenderGraphFrame frame) {
        var shown = new List<Shown>[set.Instances.Count];

        for (var index = 0; (index < shown.Length); index++) {
            shown[index] = [];
        }
        foreach (var footprint in frame.Footprints) {
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

            RenderGraphEdge? declared = null;

            foreach (var candidate in set.Reads[consumer]) {
                if (candidate.Producer == producer) {
                    declared = candidate;

                    break;
                }
            }

            if (declared is not { } edge) {
                throw new ArgumentException(
                    message: $"Instance '{footprint.Consumer}' shows '{footprint.Producer}' but declares no read of it.",
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
            var existing = list.FindIndex(match: entry => (entry.Producer == producer));

            if (existing < 0) {
                list.Add(item: new Shown(
                    Height: height,
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

        return shown;
    }

    /// <summary>Schedules one frame.</summary>
    /// <param name="set">The instances.</param>
    /// <param name="frame">What the frame shows.</param>
    /// <param name="history">The previous frame's history: <see cref="RenderGraphHistory.Empty"/> for the first frame,
    /// otherwise the previous schedule's <see cref="RenderGraphSchedule.Next"/>.</param>
    /// <returns>The schedule, carrying the history the next frame is scheduled against.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="history"/> covers a different number of instances, the frame
    /// does not follow it, the display extent is not positive, or a root or footprint names an undeclared instance or
    /// read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A root or footprint fraction is negative or not finite, or the
    /// budget is negative.</exception>
    public static RenderGraphSchedule Schedule(RenderGraphInstanceSet set, RenderGraphFrame frame, RenderGraphHistory history) {
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: history);
        ArgumentOutOfRangeException.ThrowIfNegative(value: frame.PassPixelBudget);

        var count = set.Instances.Count;

        if (history.Count != count) {
            throw new ArgumentException(
                message: $"The history covers {history.Count} instances; the set declares {count}.",
                paramName: nameof(history)
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

        var isRoot = new bool[count];
        var demandWidth = new double[count];
        var demandHeight = new double[count];

        foreach (var root in frame.Roots) {
            var index = set.IndexOf(name: root.Instance);

            if (index < 0) {
                throw new ArgumentException(
                    message: $"Root '{root.Instance}' names an undeclared instance.",
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

        var shown = Footprints(
            frame: frame,
            set: set
        );
        var divisor = new int[count];
        var due = new bool[count];

        for (var index = 0; (index < count); index++) {
            var rendered = history.LatestFrame(index: index);

            divisor[index] = set.Instances[index].Refresh.ResolveDivisor(displayHertz: frame.DisplayHertz);
            due[index] = (
                (rendered < 0) ||
                ((frame.Index - rendered) >= divisor[index])
            );
        }

        // Consumers are decided before their same-frame producers, so a producer's demand is complete when it is
        // reached; a previous-frame read that arrives after its producer was decided adds no extent this frame.
        var decided = new bool[count];
        var scaleWidth = new double[count];
        var scaleHeight = new double[count];
        var changed = true;

        while (changed) {
            changed = false;

            for (var position = (count - 1); (position >= 0); position--) {
                var index = set.Order[position];

                if (
                    decided[index] ||
                    (demandWidth[index] == 0)
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

        var width = new int[count];
        var height = new int[count];
        var price = new long[count];

        for (var index = 0; (index < count); index++) {
            if (decided[index]) {
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

        var positionOf = new int[count];

        for (var position = 0; (position < count); position++) {
            positionOf[set.Order[position]] = position;
        }

        var admitted = new bool[count];
        var deferred = new bool[count];
        var candidates = new List<int>();

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
                candidates.Add(item: index);
            }
        }

        candidates.Sort(comparison: (left, right) => {
            var staleness = Staleness(index: right).CompareTo(value: Staleness(index: left));

            return ((staleness != 0)
                ? staleness
                : positionOf[left].CompareTo(value: positionOf[right])
            );
        });

        var spent = 0L;

        foreach (var index in candidates) {
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

        var rows = new RenderGraphInstanceSchedule[count];
        var renders = new List<int>();
        var latest = new long[count];
        var allocatedWidths = new double[count];
        var allocatedHeights = new double[count];
        var total = 0L;

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

            latest[index] = (admitted[index]
                ? frame.Index
                : history.LatestFrame(index: index)
            );
            allocatedWidths[index] = (admitted[index]
                ? scaleWidth[index]
                : allocatedWidth
            );
            allocatedHeights[index] = (admitted[index]
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
                LatestFrame: latest[index],
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

        var reads = new List<RenderGraphReadSchedule>();

        foreach (var consumer in renders) {
            foreach (var entry in shown[consumer]) {
                reads.Add(item: new RenderGraphReadSchedule(
                    Consumer: set.Instances[consumer].Name,
                    Frame: ((!entry.PreviousFrame && admitted[entry.Producer])
                        ? frame.Index
                        : history.LatestFrame(index: entry.Producer)),
                    PreviousFrame: entry.PreviousFrame,
                    Producer: set.Instances[entry.Producer].Name
                ));
            }
        }

        return RenderGraphSchedule.Create(
            frame: frame.Index,
            instances: rows,
            next: RenderGraphHistory.Create(
                frame: frame.Index,
                height: allocatedHeights,
                latest: latest,
                width: allocatedWidths
            ),
            passPixels: total,
            reads: reads,
            renders: renders
        );

        long Staleness(int index) {
            var last = history.LatestFrame(index: index);

            return ((last < 0)
                ? long.MaxValue
                : (frame.Index - last)
            );
        }
    }
}
