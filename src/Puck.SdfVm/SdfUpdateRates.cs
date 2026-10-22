using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// Which presentation work updates less often when it is far or small on screen (<c>world.rates</c>,
/// <see cref="SdfFrame.UpdateRates"/>). Each lever rates work by its projected size at the resolution it is shown at and
/// switches only where the drop is barely perceptible (<see cref="SdfUpdateRateTiers"/>). Presentation only: the
/// simulation ticks every body and runs every machine at its own rate whatever the levers say.
/// </summary>
[Flags]
public enum SdfUpdateRates : byte {
    /// <summary>Every lever off: everything updates every frame.</summary>
    None = 0,
    /// <summary>A body's pose evaluation and skinned pose (its drivers, effectors and part slots) at 30 or 15 Hz as its
    /// pixels per metre fall, its root still followed every frame.</summary>
    Bodies = 1,
    /// <summary>A nested portal or session view at 30 or 15 Hz as the share of the display it covers shrinks.</summary>
    Views = 2,
    /// <summary>An in-world screen's source image upload at 30 or 15 Hz once it is shown below its native size; the
    /// machine behind it still runs at full rate.</summary>
    Screens = 4,
    /// <summary>The key light's far shadow cascade refreshed at 30 Hz, its fit held between refreshes.</summary>
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
    /// <summary>Returns how many presented frames a rate holds an update for at a display's rate.</summary>
    /// <param name="hertz">The rate, 60 or below meaning a reduced one only below 60.</param>
    /// <param name="displayHertz">The display's presented frames a second, or zero when unknown, which updates every
    /// frame.</param>
    /// <returns>The frame divisor, at least one.</returns>
    public static int Divisor(int hertz, int displayHertz) => (((hertz >= 60) || (displayHertz <= 0))
        ? 1
        : Math.Max(val1: 1, val2: ((displayHertz + (hertz / 2)) / hertz)));
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

    /// <summary>Gets the kinds the counters count, in report order.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Creates the counters a host's frames count into.</summary>
    /// <returns>A set over <see cref="Kinds"/>, every count zero.</returns>
    public static WorkCounterSet Counters() => new(kinds: DeclaredKinds, name: SourceName);
}
