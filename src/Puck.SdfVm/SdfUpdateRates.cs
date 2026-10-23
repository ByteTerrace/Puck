using Puck.Abstractions.Counting;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>
/// Which presentation work updates less often when it is far or small on screen (<c>world.rates</c>,
/// <see cref="SdfFrame.UpdateRates"/>). Each lever rates work by its projected size at the resolution it is shown at and
/// switches only where the drop is barely perceptible (<see cref="SdfUpdateRateTiers"/>). A tier is a floor: rated work
/// updates at least that many times a second of presentation time, held between by <see cref="PresentationRate.Holds"/>,
/// whatever the display's rate or the frames' pace. Presentation only: the simulation ticks every body and runs every
/// machine at its own rate whatever the levers say.
/// </summary>
[Flags]
public enum SdfUpdateRates : byte {
    /// <summary>Every lever off: everything updates every frame.</summary>
    None = 0,
    /// <summary>A body's pose evaluation and skinned pose (its drivers, effectors and part slots) at least 30 or 15 times
    /// a second as its pixels per metre fall, its root still followed every frame.</summary>
    Bodies = 1,
    /// <summary>A nested portal or session view at least 30 or 15 times a second as the share of the display it covers
    /// shrinks.</summary>
    Views = 2,
    /// <summary>An in-world screen's source image upload at least 30 or 15 times a second once it is shown below its
    /// native size; the machine behind it still runs at full rate.</summary>
    Screens = 4,
    /// <summary>The key light's far shadow cascade refreshed at least 30 times a second, its fit held between
    /// refreshes.</summary>
    ShadowFar = 8,
    /// <summary>Every lever on, the default.</summary>
    All = (((Bodies | Views) | Screens) | ShadowFar),
}
/// <summary>
/// The size-on-screen thresholds every <see cref="SdfUpdateRates"/> lever switches at, each derived from how far the
/// held content moves on screen between updates. A reduced rate is barely perceptible when that step stays within
/// <see cref="PerceptibleStepPixels"/>: content moving at most a bounded speed (a body's limbs, a window's parallax, a
/// game scrolling a pixel a frame) holds still for a period, then jumps. The owner's ruling: a visible trade-off switches
/// only beyond the distance where it is barely perceptible, judged by its size on screen.
/// </summary>
public static class SdfUpdateRateTiers {
    /// <summary>The on-screen step, in pixels, a held update may jump by and stay barely perceptible.</summary>
    public const float PerceptibleStepPixels = 2f;
    /// <summary>The fastest a body's parts move relative to its root, in metres a second, that the body tiers are sized
    /// for: a stride's swing.</summary>
    public const float BodyPartSpeed = 2f;
    /// <summary>The pixels per metre at and below which a body updates at 30 Hz: a part moving
    /// <see cref="BodyPartSpeed"/> steps <see cref="PerceptibleStepPixels"/> in a 30th of a second.</summary>
    public const float BodyHalfRatePixelsPerMetre = ((PerceptibleStepPixels * 30f) / BodyPartSpeed);
    /// <summary>The pixels per metre at and below which a body updates at 15 Hz.</summary>
    public const float BodyQuarterRatePixelsPerMetre = ((PerceptibleStepPixels * 15f) / BodyPartSpeed);
    /// <summary>The share of its own height a nested view's content moves in a second at most, which the view tiers are
    /// sized for: a viewer walking past a window, not a snap turn.</summary>
    public const float ViewParallaxPerSecond = 0.5f;
    /// <summary>The pixels of display height at and below which a nested view renders at 30 Hz: content moving
    /// <see cref="ViewParallaxPerSecond"/> of it a second steps <see cref="PerceptibleStepPixels"/> in a 30th of a
    /// second.</summary>
    public const float ViewHalfRatePixels = ((PerceptibleStepPixels * 30f) / ViewParallaxPerSecond);
    /// <summary>The pixels of display height at and below which a nested view renders at 15 Hz.</summary>
    public const float ViewQuarterRatePixels = ((PerceptibleStepPixels * 15f) / ViewParallaxPerSecond);
    /// <summary>The share of its native height at and below which a screen's image uploads at 30 Hz: a game scrolling a
    /// texel a frame steps two texels at 30 Hz, which shown at its native size or smaller is
    /// <see cref="PerceptibleStepPixels"/> or less.</summary>
    public const float ScreenHalfRateShare = 1f;
    /// <summary>The share of its native height at and below which a screen's image uploads at 15 Hz: four texels a step,
    /// two pixels or less at half its native size.</summary>
    public const float ScreenQuarterRateShare = 0.5f;
    /// <summary>The rate the far shadow cascade refreshes at, in hertz: a dynamic caster lags it by a 30th of a second,
    /// under one of its texels at walking speed (the cascade covers 120 m in at most 2,048 texels, so a texel is 12 cm or
    /// more) and beyond the near cascade's 16 m.</summary>
    public const int ShadowFarHertz = 30;
    /// <summary>The camera turn, in radians, past which a held far cascade refreshes at once: at its 120 m reach the
    /// view's frustum moves about a metre, within the shadow filter's reach the cascade's caster cull keeps beside
    /// every receiver (<see cref="SdfMeshCull.ShadowFilterTexels"/>).</summary>
    public const float ShadowFarTurnRadians = 0.0087f;
    /// <summary>The camera move, in metres, past which a held far cascade refreshes at once.</summary>
    public const float ShadowFarMoveMetres = 0.5f;

    /// <summary>Returns the rate a body updates at for its largest pixels per metre over the views that may see it.</summary>
    /// <param name="pixelsPerMetre">The body's pixels per metre in the view that shows it largest, or zero when no view
    /// may see it.</param>
    /// <returns>60, 30 or 15: the hertz the body's pose updates at, where 60 means every frame.</returns>
    public static int BodyHertz(float pixelsPerMetre) => ((pixelsPerMetre > BodyHalfRatePixelsPerMetre)
        ? 60
        : ((pixelsPerMetre > BodyQuarterRatePixelsPerMetre) ? 30 : 15));
    /// <summary>Returns the rate a nested view renders at for the pixels of display height it covers.</summary>
    /// <param name="pixels">The view's height on the display, in pixels.</param>
    /// <returns>60, 30 or 15, where 60 means every frame its consumer renders.</returns>
    public static int ViewHertz(double pixels) => ((pixels > ViewHalfRatePixels)
        ? 60
        : ((pixels > ViewQuarterRatePixels) ? 30 : 15));
    /// <summary>Returns the rate a screen's image uploads at for its height on the display against its image's own.</summary>
    /// <param name="share">The screen's height on the display over its image's height in texels.</param>
    /// <returns>60, 30 or 15, where 60 means at the source's own cadence.</returns>
    public static int ScreenHertz(double share) => ((share > ScreenHalfRateShare)
        ? 60
        : ((share > ScreenQuarterRateShare) ? 30 : 15));
    /// <summary>The tier that means every frame: content at it is not rated.</summary>
    public const int EveryFrameHertz = 60;

    /// <summary>Returns whether a rated item holds its latest update through this frame: its tier is below every frame
    /// and the floor at its tier still holds (<see cref="PresentationRate.Holds"/>), so it updates at least
    /// <paramref name="hertz"/> times a second of presentation time whatever the frames' pace.</summary>
    /// <param name="rate">The frame's presentation rate.</param>
    /// <param name="hertz">The item's tier: 60 for every frame, 30 or 15.</param>
    /// <param name="elapsedSeconds">The presentation time from the frame the item last updated on to this frame.</param>
    /// <param name="frameSeconds">The latest frame's span.</param>
    /// <returns><see langword="true"/> when the item holds this frame.</returns>
    public static bool Holds(PresentationRate rate, int hertz, double elapsedSeconds, double frameSeconds) => (
        (hertz < EveryFrameHertz) &&
        rate.Holds(elapsedSeconds: elapsedSeconds, frameSeconds: frameSeconds, hertz: hertz)
    );
}
/// <summary>
/// The longest interval between updates each <see cref="SdfUpdateRates"/> lever achieved at each reduced tier over a
/// recent window of presentation time, which <c>world.rates</c> prints against the tier's period
/// (<see cref="PresentationRate.PeriodSeconds"/>). Counting held frames cannot show a rate on a display whose frames
/// come unevenly; the interval can. An interval counts only where the item updated or was held on every frame between
/// its two updates, so a view out of sight or a frame that rendered nothing is not read as a slow rate. Any thread
/// records and reads; presentation only.
/// </summary>
public sealed class SdfUpdateRateIntervals {
    /// <summary>The presentation time each half of the window spans, in seconds: a reading covers between one and two of
    /// them.</summary>
    public const double HalfWindowSeconds = 1d;
    /// <summary>The reduced tiers an interval is recorded under, in hertz.</summary>
    public static readonly int[] Tiers = [30, 15];
    /// <summary>The levers an interval is recorded for, in report order.</summary>
    public static readonly SdfUpdateRates[] Levers = [SdfUpdateRates.Bodies, SdfUpdateRates.Views, SdfUpdateRates.Screens, SdfUpdateRates.ShadowFar];

    private readonly Lock m_gate = new();
    private readonly double[] m_current = new double[(Levers.Length * Tiers.Length)];
    private readonly double[] m_previous = new double[(Levers.Length * Tiers.Length)];
    private double m_elapsed;
    private PresentationRate m_rate;

    /// <summary>Gets the presentation rate of the latest frame the window advanced over, which a tier's period is read
    /// against.</summary>
    public PresentationRate Rate {
        get {
            lock (m_gate) {
                return m_rate;
            }
        }
    }

    // The slot of a lever and tier, or -1 for a lever or tier no interval is recorded under.
    private static int SlotOf(SdfUpdateRates lever, int hertz) {
        var leverIndex = Array.IndexOf(array: Levers, value: lever);
        var tierIndex = Array.IndexOf(array: Tiers, value: hertz);

        return (((leverIndex < 0) || (tierIndex < 0)) ? -1 : ((leverIndex * Tiers.Length) + tierIndex));
    }
    /// <summary>Advances the window by one presented frame.</summary>
    /// <param name="rate">The frame's presentation rate.</param>
    /// <param name="frameSeconds">The frame's span, in seconds of presentation time.</param>
    public void Advance(PresentationRate rate, double frameSeconds) {
        lock (m_gate) {
            m_rate = rate;
            m_elapsed += Math.Max(val1: 0d, val2: frameSeconds);

            if (m_elapsed >= HalfWindowSeconds) {
                m_elapsed = 0d;
                Array.Copy(sourceArray: m_current, destinationArray: m_previous, length: m_current.Length);
                Array.Clear(array: m_current);
            }
        }
    }
    /// <summary>Records one interval between an item's updates under its lever and the slowest tier it was rated at over
    /// the interval; a lever or tier outside <see cref="Levers"/> and <see cref="Tiers"/> records nothing.</summary>
    /// <param name="lever">The lever that rated the item.</param>
    /// <param name="hertz">The slowest tier the item was rated at between its two updates.</param>
    /// <param name="seconds">The presentation time between the two updates.</param>
    public void Record(SdfUpdateRates lever, int hertz, double seconds) {
        var slot = SlotOf(hertz: hertz, lever: lever);

        if (slot < 0) {
            return;
        }

        lock (m_gate) {
            m_current[slot] = Math.Max(val1: m_current[slot], val2: seconds);
        }
    }
    /// <summary>Returns the longest interval recorded under a lever and tier over the window.</summary>
    /// <param name="lever">The lever.</param>
    /// <param name="hertz">The tier.</param>
    /// <returns>The interval, in seconds, or <see langword="null"/> when none was recorded.</returns>
    public double? Longest(SdfUpdateRates lever, int hertz) {
        var slot = SlotOf(hertz: hertz, lever: lever);

        if (slot < 0) {
            return null;
        }

        lock (m_gate) {
            var longest = Math.Max(val1: m_current[slot], val2: m_previous[slot]);

            return ((longest > 0d) ? longest : null);
        }
    }
}
/// <summary>
/// The <c>world.rates</c> counters (<see cref="SdfUpdateRates"/>): how often each rated piece of work updated and how
/// often it held. A frame source counts into the set its frame carries (<see cref="SdfFrame.RateCounts"/>).
/// </summary>
public static class SdfUpdateRateCounts {
    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.rates";
    /// <summary>Counts each body pose a frame evaluated in full.</summary>
    public static readonly WorkKind BodiesPosed = new(name: "sdf.rates.bodies.posed", unit: "bodies", workClass: WorkClass.Pacing);
    /// <summary>Counts each body pose a frame held, carrying its last pose rigidly with its root.</summary>
    public static readonly WorkKind BodiesHeld = new(name: "sdf.rates.bodies.held", unit: "bodies", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a view rendered its far cascade.</summary>
    public static readonly WorkKind ShadowFarRendered = new(name: "sdf.rates.shadow-far.rendered", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a view held its far cascade.</summary>
    public static readonly WorkKind ShadowFarHeld = new(name: "sdf.rates.shadow-far.held", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a nested view was shown at a reduced rate.</summary>
    public static readonly WorkKind ViewsRated = new(name: "sdf.rates.views.rated", unit: "footprints", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a screen's source was shown at a reduced rate.</summary>
    public static readonly WorkKind ScreensRated = new(name: "sdf.rates.screens.rated", unit: "footprints", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [BodiesPosed, BodiesHeld, ShadowFarRendered, ShadowFarHeld, ViewsRated, ScreensRated];

    /// <summary>Creates the counters a host's frames count into.</summary>
    /// <returns>A set over every kind this class declares, in report order, every count zero.</returns>
    public static WorkCounterSet Counters() => new(kinds: DeclaredKinds, name: SourceName);
}
