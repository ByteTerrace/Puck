using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.SignedDistance;

/// <summary>How a view chooses the exposure it maps luminance to the display by.</summary>
[JsonConverter(typeof(StrictEnumConverter<SdfExposureMode>))]
public enum SdfExposureMode {
    /// <summary>A fixed <c>EV100</c>, <see cref="SdfExposure.Ev100"/>.</summary>
    Manual = 0,
    /// <summary>The <c>EV100</c> a reflected-light meter reads about the camera, less the compensation, held within the
    /// bounds and eased toward over the adaptation time.</summary>
    Auto = 1,
}
/// <summary>
/// A world's camera exposure (<c>render.exposure</c>): how each view maps the luminance it shades, in nits, to the
/// display before the tonemap (<see cref="SdfPhotometry.ExposureOf"/>). Presentation only: the exposure a view adapts to
/// reads presentation time and the irradiance volume, and nothing in simulation state depends on it.
/// </summary>
/// <param name="Mode">Manual or automatic.</param>
/// <param name="Ev100">The manual exposure value at ISO 100; an automatic exposure's initial value before its meter
/// first reads. A later missing measurement holds the previous exposure.</param>
/// <param name="Minimum">The darkest scene an automatic exposure adapts to, as the lowest <c>EV100</c> it takes.</param>
/// <param name="Maximum">The brightest scene an automatic exposure adapts to, as the highest <c>EV100</c> it takes.</param>
/// <param name="Compensation">The stops an automatic exposure brightens the metered image by.</param>
/// <param name="Adaptation">The time constant an automatic exposure eases toward its meter with, in seconds; zero
/// follows the meter at once.</param>
/// <param name="Local">The share of the local adaptation, in [0, 1]: how far each surface's exposure moves from the
/// view's toward the exposure its own illumination would meter (the light arriving at it, direct and from the
/// irradiance volume, against the view's meter reading over <see cref="LocalReflectance"/>), so a dim ceiling beside a
/// sunlit window lifts and the sunlit ground through the window compresses while a dark paint stays dark, as the eye
/// adapts across a scene; the sky is scaled so its brightest reaches display white (<see cref="SkyPeakTarget"/>). Zero
/// exposes the whole view alike, one meters every surface on its own.</param>
public readonly record struct SdfExposure(SdfExposureMode Mode, float Ev100, float Minimum, float Maximum, float Compensation, float Adaptation, float Local) {
    /// <summary>The <c>EV100</c> an unauthored world renders at: a clear sunlit day.</summary>
    public const float DefaultEv100 = 15f;
    /// <summary>An automatic exposure's default time constant, in seconds.</summary>
    public const float DefaultAdaptation = 0.75f;
    /// <summary>The most stops the local adaptation moves a surface's exposure from the view's, either way.</summary>
    public const float LocalRange = 6f;
    /// <summary>The reflectance the local adaptation takes the meter's reading, a scene luminance, to stand for: the
    /// illuminance over π a surface of this albedo would show the reading at, which each surface's own illumination is
    /// measured against.</summary>
    public const float LocalReflectance = 0.4f;
    /// <summary>The display value, before the tonemap, of a white surface under the illumination an automatic
    /// exposure's meter aims for: the meter's calibration over the saturation headroom at ISO 100 and over
    /// <see cref="LocalReflectance"/>, about a quarter. The stylized curvature terms take their full strength on a
    /// surface lit this brightly and fade with its light below it, so a dim room's edges stay as dim as its
    /// walls.</summary>
    public const float ReferenceWhite = (SdfPhotometry.MeterCalibration / ((100f * SdfPhotometry.SaturationHeadroom) * LocalReflectance));
    /// <summary>The display value, before the tonemap, the local adaptation scales a view's sky share down to where its
    /// brightest texel would exceed it, so a window from a dim room shows the sky's gradient rather than white.</summary>
    public const float SkyPeakTarget = 1f;

    /// <summary>Gets the exposure an unauthored world renders with: manual, at <see cref="DefaultEv100"/>, with no
    /// local adaptation.</summary>
    public static SdfExposure Default { get; } = new(
        Adaptation: DefaultAdaptation,
        Compensation: 0f,
        Ev100: DefaultEv100,
        Local: 0f,
        Maximum: DefaultEv100,
        Minimum: DefaultEv100,
        Mode: SdfExposureMode.Manual
    );
    /// <summary>Gets the largest exposure multiplier any view of the world takes: its darkest <c>EV100</c> less any
    /// compensation, which sizes each point light's range (<see cref="SdfPhotometry.RangeOf"/>).</summary>
    public float BrightestGain => SdfPhotometry.ExposureOf(ev100: ((Mode == SdfExposureMode.Auto)
        ? MathF.Min(x: Minimum, y: Ev100)
        : Ev100));

    /// <summary>Returns the <c>EV100</c> an automatic exposure aims for at a metered average luminance: the meter's reading
    /// less the compensation, within the bounds.</summary>
    /// <param name="luminance">The metered average luminance, in nits.</param>
    /// <returns>The target <c>EV100</c>.</returns>
    public float Target(float luminance) => Math.Clamp(
        value: (SdfPhotometry.MeteredEv100(luminance: luminance) - Compensation),
        min: Minimum,
        max: MathF.Max(x: Minimum, y: Maximum)
    );
    /// <summary>Returns the <c>EV100</c> an automatic exposure takes a frame later: eased from its current value toward
    /// the target over the adaptation time, and the target itself once within a hundredth of a stop of it, so a still
    /// scene settles on one value.</summary>
    /// <param name="current">The current <c>EV100</c>.</param>
    /// <param name="target">The target <c>EV100</c>.</param>
    /// <param name="seconds">The presentation time since the last frame, in seconds.</param>
    /// <returns>The next <c>EV100</c>.</returns>
    public float Adapt(float current, float target, float seconds) {
        if (!(Adaptation > 0f) || !float.IsFinite(f: current)) {
            return target;
        }

        var next = (current + ((target - current) * (1f - MathF.Exp(x: (-MathF.Max(x: seconds, y: 0f) / Adaptation)))));

        return ((MathF.Abs(x: (target - next)) < 0.01f) ? target : next);
    }
}
