using System.Runtime.InteropServices;

namespace Puck.Hosting;

/// <summary>
/// The one place a presentation rate becomes a decision, in its two meanings. A floor (<see cref="Holds"/>, "at least
/// N Hz", the <c>world.rates</c> levers) holds a rated update by presentation time: an item updates on the first frame
/// where waiting one more frame, estimated from the latest frame's span, would pass its period, so a slow or uneven frame
/// (a variable-refresh display, a missed frame) never lowers the rate below N. On a display that shows frames on a fixed
/// grid (<see cref="GridHertz"/>), the period is a whole number of grid steps rounded down, so the cadence stays even and
/// never falls below N. Off a grid, frames that land about one pacing step apart (<see cref="PacedFrameLow"/>,
/// <see cref="PacedFrameHigh"/>), as a timer-paced variable-refresh display's do within the timer's jitter, count the same
/// way in steps of <see cref="DisplayHertz"/>, which keeps each item's interval whole and the offset between two items
/// fixed. A cap (<see cref="FramesAtMost"/>, "at most N Hz", an authored refresh or a source's own rate
/// such as a machine's 59.7 Hz) counts presented frames, rounding the divisor up so the instance never renders faster
/// than asked. Presentation only: nothing here reaches simulation state.
/// <para>A floor also keeps rated work off the frames that run a simulation step, the frame thread's heaviest: on a
/// frame that runs none, an item that could hold now but would then fall due on a next frame that runs a step updates
/// now instead (<see cref="Steps"/>, <see cref="StepsNext"/>), so it updates early, never late, and never on the step's
/// frame while a frame without one serves it.</para>
/// </summary>
/// <param name="DisplayHertz">The presented frames a second the host paces the display at, or zero when it is unknown or
/// unbounded (offscreen), where a floor holds nothing and a cap renders every frame.</param>
/// <param name="GridHertz">The rate presented frames land on a whole number of steps of: a fixed display signal's
/// refresh rate, or the simulation's tick rate under lockstep; zero when frames land at any time, as on a
/// variable-refresh display, whose frames still count as steps of <paramref name="DisplayHertz"/> while they span about one.</param>
/// <param name="Steps">Whether this frame's host iteration ran a simulation step (<see cref="FrameContext.Steps"/>).</param>
/// <param name="StepsNext">Whether the host expects its next iteration to run a simulation step
/// (<see cref="FrameContext.StepsNext"/>).</param>
public readonly record struct PresentationRate(int DisplayHertz, double GridHertz, bool Steps = false, bool StepsNext = false) {
    /// <summary>The slack, in seconds, a floor off a grid allows for the jitter of a sampled wall interval: two frames
    /// a display presents a period apart read a few tenths of a millisecond either side of it, so without the slack a
    /// steady 60 Hz would update the 30 Hz tier on every frame. An interval may pass its period by this much.</summary>
    public const double ToleranceSeconds = 0.001;
    /// <summary>The latest frame's span, in pacing steps (1 / <see cref="DisplayHertz"/>), above which a frame off a grid
    /// lands on the pacing cadence: three quarters of a step.</summary>
    public const double PacedFrameLow = 0.75;
    /// <summary>The latest frame's span, in pacing steps, below which a frame off a grid lands on the pacing cadence: a
    /// step and a half.</summary>
    public const double PacedFrameHigh = 1.25;

    /// <summary>Gets whether the host paces presentation to a display, the only case a floor holds anything.</summary>
    public bool IsPaced => (DisplayHertz > 0);

    /// <summary>Returns the presentation rate a frame context describes.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <returns>The context's display rate and grid, and whether this frame and the next run a simulation step.</returns>
    public static PresentationRate Of(in FrameContext context) => new(
        DisplayHertz: context.DisplayHertz,
        GridHertz: context.GridHertz,
        Steps: context.Steps,
        StepsNext: context.StepsNext
    );
    // The frames a hold decided now must cover: this one, and the next too when this frame runs no step and the next
    // runs one, so an item held now never falls due on the step's frame.
    private int FramesHeld => ((!Steps && StepsNext) ? 2 : 1);
    // The rate frames land on whole steps of for a decision: the fixed grid, or, off one, the pacing rate while the latest
    // frame spans about one pacing step. A host that paces presentation with a timer lands its frames on that cadence to
    // within the timer's jitter, so counting steps keeps every item's interval whole and an offset between two items
    // fixed, where summing jittered spans at an integer ratio of the period (60 Hz frames, a 15 Hz tier) flips an
    // interval between n and n - 1 frames and walks two items onto one frame. A frame far from the pacing step (a
    // hitch, a display running off its target) is no step of it and is counted by its time.
    private double LandingHertz(double frameSeconds) => ((GridHertz > 0d)
        ? GridHertz
        : (((DisplayHertz > 0) && (frameSeconds > (PacedFrameLow / DisplayHertz)) && (frameSeconds < (PacedFrameHigh / DisplayHertz)))
            ? DisplayHertz
            : 0d));
    // The steps of a grid a floor's period spans: the most whole steps that still update at least `hertz` times a
    // second, and at least one.
    private static double GridSteps(double gridHertz, int hertz) => Math.Max(
        val1: 1d,
        val2: Math.Floor(d: ((gridHertz / hertz) + 1e-9))
    );
    /// <summary>Decides a floor: whether an item that last updated <paramref name="elapsedSeconds"/> of presentation time
    /// ago holds its update through this frame and still updates at least <paramref name="hertz"/> times a second.</summary>
    /// <param name="hertz">The floor, in updates a second; zero or less holds nothing.</param>
    /// <param name="elapsedSeconds">The presentation time from the frame the item last updated on to this frame.</param>
    /// <param name="frameSeconds">The latest frame's span, the estimate of how long waiting one more frame takes.</param>
    /// <param name="leadSeconds">The presentation time the decision counts ahead of <paramref name="elapsedSeconds"/>, which
    /// places the item within the floor's period by updating it that much early once (<see cref="PresentationPhases"/>);
    /// zero for none. A frame that runs a step before one that runs none counts no lead, so a lead never brings an
    /// update onto the step's frame while the next frame serves it.</param>
    /// <returns><see langword="true"/> when holding this frame and updating on the next still keeps the interval within the
    /// floor's period, and, on a frame that runs no step before one that does, holding through that next frame too;
    /// <see langword="false"/> when the item updates this frame, or the host is not paced.</returns>
    public bool Holds(int hertz, double elapsedSeconds, double frameSeconds, double leadSeconds = 0d) {
        if (!IsPaced || (hertz <= 0)) {
            return false;
        }
        if (!(Steps && !StepsNext)) {
            elapsedSeconds += leadSeconds;
        }
        if (LandingHertz(frameSeconds: frameSeconds) is (> 0d) and var landing) {
            var elapsed = Math.Round(value: (elapsedSeconds * landing), mode: MidpointRounding.AwayFromZero);
            var frame = Math.Max(val1: 1d, val2: Math.Round(value: (frameSeconds * landing), mode: MidpointRounding.AwayFromZero));

            return ((elapsed + (frame * FramesHeld)) <= GridSteps(gridHertz: landing, hertz: hertz));
        }

        return ((elapsedSeconds + (Math.Max(val1: 0d, val2: frameSeconds) * FramesHeld)) <= ((1d / hertz) + ToleranceSeconds));
    }
    /// <summary>Returns the longest interval between updates a floor allows: a whole number of grid steps on a fixed grid,
    /// otherwise the floor's own period.</summary>
    /// <param name="hertz">The floor, in updates a second, at least one.</param>
    /// <returns>The period, in seconds.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hertz"/> is zero or negative.</exception>
    public double PeriodSeconds(int hertz) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: hertz);

        return ((GridHertz > 0d)
            ? (GridSteps(gridHertz: GridHertz, hertz: hertz) / GridHertz)
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
/// <summary>
/// Spreads the rated items that take a floor's tier together over the tier's period, so they do not all update on one
/// frame of it: every other item to take a tier counts its hold decisions half the tier's period ahead of the time since
/// its latest update (a lead) until it next updates, then keeps the period. The lead only moves one update earlier, so
/// the floor still holds, and <see cref="PresentationRate.Holds"/>, which takes the lead, still keeps that update off a
/// frame that runs a simulation step. Each owner of rated items (a pose pool, a lever's views or screens) keeps its own, so its items
/// split evenly whatever another owner's do. Presentation only; one thread uses an instance.
/// </summary>
public sealed class PresentationPhases {
    // Whether the next item to take each tier takes the later half of its period, by the tier's hertz.
    private readonly Dictionary<int, bool> m_lateHalf = [];

    /// <summary>Returns the lead of the next item to take a tier: zero, then half the tier's period, alternately.</summary>
    /// <param name="rate">The frame's presentation rate, whose grid the period rounds to.</param>
    /// <param name="hertz">The tier the item takes, in updates a second.</param>
    /// <returns>The seconds the item's hold decisions count ahead until its next update; zero on a host not paced to a
    /// display, where a floor holds nothing, and for a tier of zero or less.</returns>
    public double Take(PresentationRate rate, int hertz) {
        if (!rate.IsPaced || (hertz <= 0)) {
            return 0d;
        }

        ref var lateHalf = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_lateHalf, exists: out _, key: hertz);
        var lead = (lateHalf ? (rate.PeriodSeconds(hertz: hertz) * 0.5d) : 0d);

        lateHalf = !lateHalf;

        return lead;
    }
}
