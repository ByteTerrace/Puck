using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.SignedDistance;

/// <summary>How a view chooses the exposure it maps luminance to the display by.</summary>
[JsonConverter(typeof(StrictEnumConverter<SdfExposureMode>))]
public enum SdfExposureMode {
    /// <summary>A fixed <c>EV100</c>, <see cref="SdfExposure.Ev100"/>.</summary>
    Manual = 0,
    /// <summary>The <c>EV100</c> the view's own image meters (<see cref="SdfExposure.Meter"/>), less the compensation,
    /// held within the bounds, and eased toward over the adaptation time once its target leaves the dead band.</summary>
    Auto = 1,
}
/// <summary>
/// A world's camera exposure (<c>render.exposure</c>): how each view maps the luminance it shades, in nits, to the
/// display before the tonemap (<see cref="SdfPhotometry.ExposureOf"/>). An automatic exposure meters the view's own
/// image: its composite bins each pixel's luminance, at the exposure the view rendered with, into a log histogram of
/// <see cref="MeterBins"/> bins over <see cref="MeterStops"/> stops from <see cref="MeterLowestStop"/> below display
/// white, each pixel weighted toward the image's centre by <see cref="Center"/>, and <see cref="Meter"/> averages the
/// bins between the <see cref="Low"/> and <see cref="High"/> percentiles. Presentation only: the exposure a view adapts
/// to reads presentation time and its rendered image, and nothing in simulation state depends on it.
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
/// irradiance volume, against the luminance the view's image meters taken over <see cref="LocalReflectance"/>), so a dim
/// ceiling beside a sunlit window lifts and the sunlit ground through the window compresses while a dark paint stays
/// dark, as the eye adapts across a scene; the sky is scaled so its brightest reaches display white
/// (<see cref="SkyPeakTarget"/>). Zero exposes the whole view alike, one meters every surface on its own. The image the
/// view meters is the adapted one, so the view's reading settles by a factor of <paramref name="Local"/> a reading.</param>
/// <param name="Band">The dead band, in stops: an automatic exposure holds while its meter reads within this far of the
/// reading it last settled on, and once a reading leaves the band it follows the meter until it settles again, so the
/// small changes of a walk through an evenly lit room never move the exposure. Zero follows every change.</param>
/// <param name="Low">The percentile, in [0, 1), below which the meter leaves the image's darkest pixels out.</param>
/// <param name="High">The percentile, above <paramref name="Low"/> and at most one, above which the meter leaves the
/// image's brightest pixels out, so a light fitting or the sun's disc does not darken the whole view.</param>
/// <param name="Center">How far the meter weights the image toward its centre, in [0, 1]: a pixel counts
/// <c>1 − Center · r²</c>, with <c>r</c> its distance from the centre over the half diagonal. Zero weights every pixel
/// alike.</param>
public readonly record struct SdfExposure(SdfExposureMode Mode, float Ev100, float Minimum, float Maximum, float Compensation, float Adaptation, float Local, float Band, float Low, float High, float Center) {
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
    /// <summary>The bins of the image meter's log histogram (<see cref="Meter"/>).</summary>
    public const int MeterBins = 128;
    /// <summary>The stops the image meter's histogram spans, from <see cref="MeterLowestStop"/> up.</summary>
    public const int MeterStops = 16;
    /// <summary>The lowest stop of the image meter's histogram: the base-2 log of the darkest exposed luminance its first
    /// bin holds, display white being zero. A darker pixel counts in the first bin, and a brighter one than the last
    /// bin's top in the last.</summary>
    public const float MeterLowestStop = -12f;
    /// <summary>The image meter's bins a stop.</summary>
    public const float MeterBinsPerStop = (((float)MeterBins) / MeterStops);
    /// <summary>The histogram count a pixel the image meter weights fully adds to its bin; a pixel toward the image's
    /// edge adds its <see cref="Center"/> weight of it, rounded.</summary>
    public const int MeterPixelWeight = 64;

    /// <summary>Gets the exposure an unauthored world renders with: manual, at <see cref="DefaultEv100"/>, with no
    /// local adaptation, and a meter that would average the whole image alike with no dead band.</summary>
    public static SdfExposure Default { get; } = new(
        Adaptation: DefaultAdaptation,
        Band: 0f,
        Center: 0f,
        Compensation: 0f,
        Ev100: DefaultEv100,
        High: 1f,
        Local: 0f,
        Low: 0f,
        Maximum: DefaultEv100,
        Minimum: DefaultEv100,
        Mode: SdfExposureMode.Manual
    );
    /// <summary>Gets the largest exposure multiplier any view of the world takes: its darkest <c>EV100</c> less any
    /// compensation, which sizes each point light's range (<see cref="SdfPhotometry.RangeOf"/>).</summary>
    public float BrightestGain => SdfPhotometry.ExposureOf(ev100: ((Mode == SdfExposureMode.Auto)
        ? MathF.Min(x: Minimum, y: Ev100)
        : Ev100));

    /// <summary>Returns this exposure held at one <c>EV100</c>: manual at that value with no local adaptation, so every
    /// view maps luminance alike whatever its meter reads. Both holds use it: a document's bindable
    /// <c>render.exposure.hold</c> and the presentation lever <c>world.exposure pin</c>. The bounds stay as authored, so a
    /// hold never changes the range each point and spot light is sized by.</summary>
    /// <param name="ev100">The held <c>EV100</c>.</param>
    /// <returns>The held exposure.</returns>
    public SdfExposure HeldAt(float ev100) => (this with {
        Ev100 = ev100,
        Local = 0f,
        Mode = SdfExposureMode.Manual,
    });
    /// <summary>Returns the <c>EV100</c> an automatic exposure aims for at a metered average luminance: the meter's reading
    /// less the compensation, within the bounds.</summary>
    /// <param name="luminance">The metered average luminance, in nits.</param>
    /// <returns>The target <c>EV100</c>.</returns>
    public float Target(float luminance) => Clamp(ev100: (SdfPhotometry.MeteredEv100(luminance: luminance) - Compensation));
    /// <summary>Returns an <c>EV100</c> held within the automatic bounds.</summary>
    /// <param name="ev100">The <c>EV100</c>.</param>
    /// <returns>The value within <see cref="Minimum"/> and <see cref="Maximum"/>.</returns>
    public float Clamp(float ev100) => Math.Clamp(value: ev100, min: Minimum, max: MathF.Max(x: Minimum, y: Maximum));
    /// <summary>Returns the scene luminance a view's image meters: the weighted mean of its histogram's log bins between
    /// the <see cref="Low"/> and <see cref="High"/> percentiles, each bin read at its centre and the window's edge bins at
    /// the share of their weight inside it, divided by the exposure the image was binned at.</summary>
    /// <param name="bins">The histogram, <see cref="MeterBins"/> weights, bin <c>i</c> holding the exposed luminances
    /// from <c>2^(MeterLowestStop + i / MeterBinsPerStop)</c> up.</param>
    /// <param name="multiplier">The exposure multiplier (<see cref="SdfPhotometry.ExposureOf"/>) the image was binned
    /// at.</param>
    /// <param name="luminance">The metered geometric-mean luminance, in nits, when the method returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when the histogram holds no weight (no pixel was metered) or the multiplier is not
    /// positive.</returns>
    /// <exception cref="ArgumentException"><paramref name="bins"/> is not <see cref="MeterBins"/> long.</exception>
    public bool Meter(ReadOnlySpan<uint> bins, float multiplier, out float luminance) {
        if (bins.Length != MeterBins) { throw new ArgumentException(message: $"The image meter's histogram is {MeterBins} bins.", paramName: nameof(bins)); }
        luminance = 0f;
        if (!(multiplier > 0f)) { return false; }
        var total = 0d;

        foreach (var weight in bins) { total += weight; }
        if (total <= 0d) { return false; }
        var low = (Math.Clamp(value: Low, min: 0f, max: 1f) * total);
        var high = (Math.Clamp(value: High, min: 0f, max: 1f) * total);
        var below = 0d;
        var taken = 0d;
        var stops = 0d;
        var atLow = 0;

        for (var bin = 0; (bin < MeterBins); bin++) {
            var above = (below + bins[bin]);
            var share = (Math.Min(val1: above, val2: high) - Math.Max(val1: below, val2: low));

            if ((below <= low) && (bins[bin] > 0u)) { atLow = bin; }
            if (share > 0d) {
                taken += share;
                stops += (share * StopOf(bin: bin));
            }
            below = above;
        }
        // An empty window (Low at High) reads the bin at the low percentile.
        var mean = ((taken > 0d) ? (stops / taken) : StopOf(bin: atLow));

        luminance = (float)(Math.Pow(x: 2d, y: mean) / multiplier);
        return (float.IsFinite(f: luminance) && (luminance > 0f));

        static double StopOf(int bin) => (MeterLowestStop + ((bin + 0.5d) / MeterBinsPerStop));
    }
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
