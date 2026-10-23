namespace Puck.Hosting;

/// <summary>
/// The one place a presentation rate becomes a decision, in its two meanings. A floor (<see cref="Holds"/>, "at least
/// N Hz", the <c>world.rates</c> levers) holds a rated update by presentation time: an item updates on the first frame
/// where waiting one more frame, estimated from the latest frame's span, would pass its period, so a slow or uneven frame
/// (a variable-refresh display, a missed frame) never lowers the rate below N. On a display that shows frames on a fixed
/// grid (<see cref="GridHertz"/>), the period is a whole number of grid steps rounded down, so the cadence stays even and
/// never falls below N. A cap (<see cref="FramesAtMost"/>, "at most N Hz", an authored refresh or a source's own rate
/// such as a machine's 59.7 Hz) counts presented frames, rounding the divisor up so the instance never renders faster
/// than asked. Presentation only: nothing here reaches simulation state.
/// </summary>
/// <param name="DisplayHertz">The presented frames a second the host paces the display at, or zero when it is unknown or
/// unbounded (offscreen), where a floor holds nothing and a cap renders every frame.</param>
/// <param name="GridHertz">The rate presented frames land on a whole number of steps of: a fixed display signal's
/// refresh rate, or the simulation's tick rate under lockstep; zero when frames land at any time, as on a
/// variable-refresh display.</param>
public readonly record struct PresentationRate(int DisplayHertz, double GridHertz) {
    /// <summary>The slack, in seconds, a floor off a grid allows for the jitter of a sampled wall interval: two frames
    /// a display presents a period apart read a few tenths of a millisecond either side of it, so without the slack a
    /// steady 60 Hz would update the 30 Hz tier on every frame. An interval may pass its period by this much.</summary>
    public const double ToleranceSeconds = 0.001;

    /// <summary>Gets whether the host paces presentation to a display, the only case a floor holds anything.</summary>
    public bool IsPaced => (DisplayHertz > 0);

    /// <summary>Returns the presentation rate a frame context describes.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <returns>The context's display rate and grid.</returns>
    public static PresentationRate Of(in FrameContext context) => new(
        DisplayHertz: context.DisplayHertz,
        GridHertz: context.GridHertz
    );
    // The grid steps a floor's period spans on a fixed grid: the most whole steps that still update at least `hertz`
    // times a second, and at least one.
    private double GridSteps(int hertz) => Math.Max(
        val1: 1d,
        val2: Math.Floor(d: ((GridHertz / hertz) + 1e-9))
    );
    /// <summary>Decides a floor: whether an item that last updated <paramref name="elapsedSeconds"/> of presentation time
    /// ago holds its update through this frame and still updates at least <paramref name="hertz"/> times a second.</summary>
    /// <param name="hertz">The floor, in updates a second; zero or less holds nothing.</param>
    /// <param name="elapsedSeconds">The presentation time from the frame the item last updated on to this frame.</param>
    /// <param name="frameSeconds">The latest frame's span, the estimate of how long waiting one more frame takes.</param>
    /// <returns><see langword="true"/> when holding this frame and updating on the next still keeps the interval within the
    /// floor's period; <see langword="false"/> when the item updates this frame, or the host is not paced.</returns>
    public bool Holds(int hertz, double elapsedSeconds, double frameSeconds) {
        if (!IsPaced || (hertz <= 0)) {
            return false;
        }
        if (GridHertz > 0d) {
            var elapsed = Math.Round(value: (elapsedSeconds * GridHertz), mode: MidpointRounding.AwayFromZero);
            var frame = Math.Max(val1: 1d, val2: Math.Round(value: (frameSeconds * GridHertz), mode: MidpointRounding.AwayFromZero));

            return ((elapsed + frame) <= GridSteps(hertz: hertz));
        }

        return ((elapsedSeconds + Math.Max(val1: 0d, val2: frameSeconds)) <= ((1d / hertz) + ToleranceSeconds));
    }
    /// <summary>Returns the longest interval between updates a floor allows: a whole number of grid steps on a fixed grid,
    /// otherwise the floor's own period.</summary>
    /// <param name="hertz">The floor, in updates a second, at least one.</param>
    /// <returns>The period, in seconds.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hertz"/> is zero or negative.</exception>
    public double PeriodSeconds(int hertz) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: hertz);

        return ((GridHertz > 0d)
            ? (GridSteps(hertz: hertz) / GridHertz)
            : (1d / hertz));
    }
    /// <summary>Decides a cap: the fewest presented frames between renders that never render faster than
    /// <paramref name="hertz"/>.</summary>
    /// <param name="hertz">The cap, in renders a second, at least one.</param>
    /// <returns>The frame divisor, at least one; one when the display's rate is unknown.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hertz"/> is zero or negative.</exception>
    public int FramesAtMost(int hertz) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: hertz);

        return ((DisplayHertz <= 0)
            ? 1
            : Math.Max(val1: 1, val2: ((int)((((long)DisplayHertz) + (hertz - 1)) / hertz))));
    }
}
