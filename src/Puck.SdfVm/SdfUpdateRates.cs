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
    /// <summary>A body's or an animated placed creation's pose evaluation (its drivers, effectors and part slots) at
    /// least 30, 15, 10 or 5 times a second as its pixels per metre fall (<see cref="SdfUpdateRateTiers.BodyHertz"/>),
    /// and held while nothing may show or read its pose, its root still followed every frame.</summary>
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
    /// for: a stride's swing. A placed creation's tiers are sized for its own measured part speed instead
    /// (<see cref="BodyHertz"/>).</summary>
    public const float BodyPartSpeed = 2f;
    /// <summary>Gets the rates a body's pose takes, in hertz, fastest first: every frame, then each reduced tier. Each
    /// divides 30 and 60, so its floor lands on an even cadence on either grid.</summary>
    public static ReadOnlySpan<int> BodyTiers => [EveryFrameHertz, 30, 15, 10, 5];
    /// <summary>Gets the slowest nonzero body tier, in hertz: the rate a posed creation takes while every view's sight
    /// hides it and its shadow's receivers, so its pose is at most one period plus the sight's latency stale when it shows
    /// again, where a hold (<see cref="HeldHertz"/>) would be unbounded.</summary>
    public static int OccludedHertz => BodyTiers[^1];
    /// <summary>The share of its own height a nested view's content moves in a second at most, which the view tiers are
    /// sized for: a viewer walking past a window, not a snap turn.</summary>
    public const float ViewParallaxPerSecond = 0.5f;
    /// <summary>The pixels of display height at and below which a nested view renders at 30 Hz: content moving
    /// <see cref="ViewParallaxPerSecond"/> of it a second steps <see cref="PerceptibleStepPixels"/> in a 30th of a
    /// second.</summary>
    public const float ViewHalfRatePixels = ((PerceptibleStepPixels * 30f) / ViewParallaxPerSecond);
    /// <summary>The pixels of display height at and below which a nested view renders at 15 Hz.</summary>
    public const float ViewQuarterRatePixels = ((PerceptibleStepPixels * 15f) / ViewParallaxPerSecond);
    /// <summary>The maximum width or height in pixels at and below which the glass a nested view shows on renders nothing and holds its latest image
    /// (<see cref="HeldHertz"/>): two <see cref="PerceptibleStepPixels"/> steps, so whatever the destination did while
    /// held, the held image differs from a fresh one within a patch no larger than the step pair, and the first fresh
    /// render as the glass grows past it changes no more than that. Measured in the pixels of the image that shows the
    /// glass: the display's for a view the display reaches, a filming camera's own for one only that camera
    /// films.</summary>
    public const float ViewHeldPixels = (PerceptibleStepPixels * 2f);
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
    /// <summary>Returns the light turn, in radians, past which a held far cascade refreshes at once: the turn that moves
    /// the shadow of a caster at the far end of the cascade's depth span (<see cref="SdfMeshShadowCamera.Far"/>, its
    /// sphere and the casters' reach toward the light) by one of its texels. A sun on a day clock turns less in a 30th of a
    /// second (a 24-minute day turns it 0.15 milliradians, against about 0.3 for the debug world's far cascade), so a
    /// steadily moving sun holds and a light that jumps ends a hold.</summary>
    /// <param name="camera">The far cascade's camera.</param>
    /// <param name="texels">The texels along each side of its depth.</param>
    /// <returns>The turn, in radians.</returns>
    public static float ShadowFarLightTurnRadians(in SdfMeshShadowCamera camera, int texels) =>
        (camera.TexelOf(texels: texels) / Math.Max(val1: camera.Far, val2: 1f));

    /// <summary>Returns the rate a posed creation (a body, or a placed creation) updates at for its largest pixels per
    /// metre over the views that may see it and the fastest its parts move relative to its root: the slowest tier at
    /// which a part moving <paramref name="partSpeed"/> steps at most <see cref="PerceptibleStepPixels"/> on screen in
    /// one period (pixels per metre times metres a second at most <see cref="PerceptibleStepPixels"/> times the tier).
    /// Between updates the root still follows every frame and the parts ride it rigidly, so the held pose differs from
    /// the full-rate pose only by the parts' own motion.</summary>
    /// <param name="pixelsPerMetre">The creation's pixels per metre in the view that shows it largest, or zero when no
    /// view may see it; infinity for a view holding it at or inside its near plane.</param>
    /// <param name="shownElsewhere">Whether anything but a displayed view's image may show or read the pose: a shadow a
    /// cascade may draw, a camera whose view the display showed none of yet, or a consumer of its parts (a light, a camera
    /// anchor, an audio emitter). A creation no displayed view may see but that is shown elsewhere takes the 15 Hz tier,
    /// its pose still moving.</param>
    /// <param name="partSpeed">The fastest the creation's parts move relative to its root, in metres a second: a body's
    /// assumed <see cref="BodyPartSpeed"/>, a placed creation's measured bound; infinity for motion no bound covers,
    /// which updates every frame wherever anything may show it.</param>
    /// <returns>The slowest of <see cref="BodyTiers"/> that bound allows, where 60 means every frame; 15 for a creation
    /// only shown elsewhere, whose size in what shows it is not measured, when its motion has a bound; or
    /// <see cref="HeldHertz"/> when nothing may show or read its pose.</returns>
    public static int BodyHertz(float pixelsPerMetre, bool shownElsewhere, float partSpeed = BodyPartSpeed) {
        var bounded = float.IsFinite(f: partSpeed);

        if (!(pixelsPerMetre > 0f)) {
            return (shownElsewhere ? (bounded ? 15 : EveryFrameHertz) : HeldHertz);
        }
        if (!bounded || float.IsPositiveInfinity(f: pixelsPerMetre)) {
            return EveryFrameHertz;
        }

        // The on-screen speed of the fastest part, in pixels a second.
        var screenSpeed = (pixelsPerMetre * MathF.Max(x: partSpeed, y: 0f));
        var hertz = EveryFrameHertz;

        foreach (var tier in BodyTiers[1..]) {
            if (screenSpeed > (PerceptibleStepPixels * tier)) {
                break;
            }

            hertz = tier;
        }

        return hertz;
    }
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
    /// <summary>The tier of rated work nothing shown depends on: it holds until something may show it again, whatever the
    /// time held.</summary>
    public const int HeldHertz = 0;

    /// <summary>Returns the code a tier view shows a rated item by (<see cref="SdfUpdateRateCode"/>).</summary>
    /// <param name="hertz">The item's tier: 60 or more for every frame, 30, or below 30.</param>
    /// <param name="updated">Whether the item updated on the frame the code is for.</param>
    /// <returns>The tier's code, plus <see cref="SdfUpdateRateCode.Updated"/> when <paramref name="updated"/>.</returns>
    public static SdfUpdateRateCode CodeOf(int hertz, bool updated) {
        var tier = ((hertz >= EveryFrameHertz)
            ? SdfUpdateRateCode.Every
            : ((hertz >= 30) ? SdfUpdateRateCode.Half : SdfUpdateRateCode.Quarter));

        return (updated ? ((SdfUpdateRateCode)(((int)tier) + ((int)SdfUpdateRateCode.Updated))) : tier);
    }

    /// <summary>Returns whether a rated item holds its latest update through this frame: its tier is below every frame
    /// and the floor at its tier still holds (<see cref="PresentationRate.Holds"/>), so it updates at least
    /// <paramref name="hertz"/> times a second of presentation time whatever the frames' pace.</summary>
    /// <param name="rate">The frame's presentation rate.</param>
    /// <param name="hertz">The item's tier: 60 for every frame, 30 or 15, or <see cref="HeldHertz"/>, which holds on
    /// every paced frame.</param>
    /// <param name="elapsedSeconds">The presentation time from the frame the item last updated on to this frame.</param>
    /// <param name="frameSeconds">The latest frame's span.</param>
    /// <returns><see langword="true"/> when the item holds this frame; never on a host not paced to a display.</returns>
    public static bool Holds(PresentationRate rate, int hertz, double elapsedSeconds, double frameSeconds) => ((hertz == HeldHertz)
        ? rate.IsPaced
        : ((hertz < EveryFrameHertz) && rate.Holds(elapsedSeconds: elapsedSeconds, frameSeconds: frameSeconds, hertz: hertz)));
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
    public static readonly int[] Tiers = [30, 15, 10, 5];
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
/// The items each <see cref="SdfUpdateRates"/> lever rated on the latest frame that rated any, with the size on screen
/// each was rated by and the tier it took, which <c>world.rates</c> lists: a body by its pixels per metre, a nested view
/// by its display pixels high and a screen by its height on the display over its image's. Any thread publishes and
/// reads; presentation only.
/// </summary>
public sealed class SdfUpdateRateReadings {
    /// <summary>One rated item's reading.</summary>
    /// <param name="Name">The item: <c>body:&lt;index&gt;</c>, or the instance a view or screen shows.</param>
    /// <param name="Size">The size on screen the item was rated by, in the lever's unit.</param>
    /// <param name="Hertz">The tier it took: 60 for every frame, 30 or 15.</param>
    /// <param name="SightFrames">For a creation every view's sight hid (<see cref="SdfUpdateRateTiers.OccludedHertz"/>),
    /// the most frames a sight that hid it took from a query to its answer; -1 for any other reading.</param>
    public readonly record struct Reading(string Name, double Size, int Hertz, int SightFrames = -1);

    private readonly Lock m_gate = new();
    private readonly Dictionary<SdfUpdateRates, List<Reading>> m_latest = [];

    /// <summary>One producer's reusable readings. Publication transfers its private storage under the reader lock
    /// and takes back the previous storage. No mutable published list escapes. Use a buffer from one thread.</summary>
    public sealed class Buffer {
        private List<Reading> m_items = [];

        /// <summary>Clears the readings being prepared.</summary>
        public void Clear() => m_items.Clear();
        /// <summary>Adds a reading.</summary>
        /// <param name="item">The reading.</param>
        public void Add(Reading item) => m_items.Add(item);
        /// <summary>Orders the prepared readings by name.</summary>
        public void Sort() => m_items.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        /// <summary>Publishes the prepared readings and takes back reusable storage, without copying the rows.</summary>
        /// <param name="readings">The destination.</param>
        /// <param name="lever">The lever.</param>
        public void PublishTo(SdfUpdateRateReadings readings, SdfUpdateRates lever) {
            ArgumentNullException.ThrowIfNull(readings);

            lock (readings.m_gate) {
                var previous = readings.m_latest.GetValueOrDefault(lever);
                readings.m_latest[lever] = m_items;
                m_items = previous ?? [];
            }
            m_items.Clear();
        }
    }
    /// <summary>Returns a lever's latest readings.</summary>
    /// <param name="lever">The lever.</param>
    /// <returns>A copy of the readings, empty when the lever published none.</returns>
    public IReadOnlyList<Reading> Latest(SdfUpdateRates lever) {
        lock (m_gate) {
            return (m_latest.TryGetValue(key: lever, value: out var readings) ? [.. readings] : []);
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
    /// <summary>Counts each animated placed creation's pose a frame evaluated in full.</summary>
    public static readonly WorkKind PlacementsPosed = new(name: "sdf.rates.placements.posed", unit: "placements", workClass: WorkClass.Pacing);
    /// <summary>Counts each animated placed creation's pose a frame held.</summary>
    public static readonly WorkKind PlacementsHeld = new(name: "sdf.rates.placements.held", unit: "placements", workClass: WorkClass.Pacing);
    /// <summary>Counts each body or animated placed creation a frame rated at <see cref="SdfUpdateRateTiers.OccludedHertz"/>
    /// because every view's sight hid it and the receivers its shadow can reach.</summary>
    public static readonly WorkKind Occluded = new(name: "sdf.rates.occluded", unit: "creations", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a view rendered both layers of its far cascade.</summary>
    public static readonly WorkKind ShadowFarRendered = new(name: "sdf.rates.shadow-far.rendered", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a view rendered one layer of its far cascade, half of a split refresh.</summary>
    public static readonly WorkKind ShadowFarSplit = new(name: "sdf.rates.shadow-far.split", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a view held its far cascade.</summary>
    public static readonly WorkKind ShadowFarHeld = new(name: "sdf.rates.shadow-far.held", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a nested view was shown at a reduced rate.</summary>
    public static readonly WorkKind ViewsRated = new(name: "sdf.rates.views.rated", unit: "footprints", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a nested view's glass was shown too small to render (<see cref="SdfUpdateRateTiers.ViewHeldPixels"/>),
    /// holding its latest image.</summary>
    public static readonly WorkKind ViewsHeld = new(name: "sdf.rates.views.held", unit: "footprints", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a screen's source was shown at a reduced rate.</summary>
    public static readonly WorkKind ScreensRated = new(name: "sdf.rates.screens.rated", unit: "footprints", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [BodiesPosed, BodiesHeld, PlacementsPosed, PlacementsHeld, Occluded, ShadowFarRendered, ShadowFarSplit, ShadowFarHeld, ViewsRated, ViewsHeld, ScreensRated];

    /// <summary>Creates the counters a host's frames count into.</summary>
    /// <returns>A set over every kind this class declares, in report order, every count zero.</returns>
    public static WorkCounterSet Counters() => new(kinds: DeclaredKinds, name: SourceName);
}
