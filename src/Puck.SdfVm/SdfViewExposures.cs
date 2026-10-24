using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// One residency's view exposures: the multiplier each view of a frame maps the luminance it shades by
/// (<see cref="SdfPhotometry.ExposureOf"/>), which its pass block carries and its composite applies, and the share of the
/// sky the air about its camera sees open, which scales the sky's in-scatter into its atmosphere. A manual exposure is
/// its <c>EV100</c>; an automatic one meters about the view's camera (<see cref="SdfMeshIrradiance.TryMeter"/>) and
/// eases toward that reading over presentation time, each view on its own, snapping to it at a camera cut. A view that
/// publishes its luminance partially exposed (<see cref="SdfViewSnapshot.Unexposed"/>) still takes its exposure, which
/// divides its display-referred terms, while its composite applies the window share of it
/// (<see cref="SdfPhotometry.WindowAdaptation"/>). Presentation only: no value here reaches simulation state.
/// </summary>
public sealed class SdfViewExposures {
    private float[] m_ev100 = [];
    private long[] m_cuts = [];
    private float[] m_multipliers = [];
    private float[] m_metered = [];
    private float[] m_airLight = [];
    private bool[] m_reads = [];
    private int m_count;

    /// <summary>Gets each view's exposure multiplier, per nit, for the frame <see cref="Stage"/> last staged.</summary>
    public ReadOnlySpan<float> Multipliers => m_multipliers.AsSpan(length: m_count, start: 0);
    /// <summary>Gets the brightest luminance, in nits, of the sky the irradiance volume's lighting sees, for the frame
    /// last staged; zero while the volume holds no sky.</summary>
    public float SkyPeakLuminance { get; private set; }

    /// <summary>Returns the seconds a staging adapts exposure over: a lockstep frame's (<see cref="SdfFrame.TickLocked"/>)
    /// own presentation time since the frame staged before it, which the ticks decide, so a frame the render graph did
    /// not stage drops no adaptation; the host frame's interval otherwise.</summary>
    /// <param name="frame">The frame being staged.</param>
    /// <param name="previousTime">The presentation time of the frame staged before it, or <see cref="float.NaN"/> for
    /// none.</param>
    /// <param name="frameSeconds">The host frame's interval, in seconds.</param>
    /// <returns>The seconds to adapt over; never negative.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static double SecondsOf(SdfFrame frame, float previousTime, double frameSeconds) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        return (frame.TickLocked
            ? (float.IsFinite(f: previousTime) ? Math.Max(val1: 0d, val2: (frame.Time - previousTime)) : 0d)
            : frameSeconds);
    }
    /// <summary>Stages a frame's exposures: each view's <c>EV100</c> eased toward its target over the frame's
    /// presentation time, and the open sky its air sees.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="irradiance">The residency's irradiance volume, which the meter reads.</param>
    /// <param name="seconds">The presentation time since the last frame, in seconds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="irradiance"/> is
    /// <see langword="null"/>.</exception>
    public void Stage(SdfFrame frame, SdfMeshIrradiance irradiance, double seconds) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: irradiance);

        var count = frame.Views.Count;

        if (m_ev100.Length < count) {
            var grown = Math.Max(val1: count, val2: (m_ev100.Length * 2));
            var first = m_ev100.Length;

            Array.Resize(array: ref m_ev100, newSize: grown);
            Array.Resize(array: ref m_cuts, newSize: grown);
            Array.Resize(array: ref m_multipliers, newSize: grown);
            Array.Resize(array: ref m_metered, newSize: grown);
            Array.Resize(array: ref m_airLight, newSize: grown);
            Array.Resize(array: ref m_reads, newSize: grown);
            Array.Fill(array: m_ev100, value: float.NaN, startIndex: first, count: (grown - first));
        }
        for (var index = count; (index < m_count); index++) {
            m_ev100[index] = float.NaN;
        }

        var exposure = frame.Exposure;

        SkyPeakLuminance = irradiance.SkyPeakLuminance;
        for (var index = 0; (index < count); index++) {
            var view = frame.Views[index];
            var cut = (view.CutRevision != m_cuts[index]);
            var ev100 = exposure.Ev100;

            m_cuts[index] = view.CutRevision;
            m_reads[index] = Meter(airLight: out m_airLight[index], irradiance: irradiance, luminance: out m_metered[index], view: in view);
            if (exposure.Mode == SdfExposureMode.Auto) {
                ev100 = (m_reads[index]
                    ? exposure.Target(luminance: m_metered[index])
                    : (float.IsFinite(f: m_ev100[index]) ? m_ev100[index] : Math.Clamp(value: exposure.Ev100, min: exposure.Minimum, max: MathF.Max(x: exposure.Minimum, y: exposure.Maximum))));
                ev100 = (cut ? ev100 : exposure.Adapt(current: m_ev100[index], seconds: ((float)seconds), target: ev100));
            }

            m_ev100[index] = ev100;
            m_multipliers[index] = SdfPhotometry.ExposureOf(ev100: ev100);
        }

        m_count = count;
    }
    /// <summary>Returns a view's exposure multiplier for the staged frame, or one for a view it did not stage.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The multiplier, per nit.</returns>
    public float MultiplierOf(int view) => (((view >= 0) && (view < m_count)) ? m_multipliers[view] : 1f);
    /// <summary>Returns the share of the sky the air about a view's camera sees open, which scales the sky's in-scatter
    /// into the view's atmosphere: one in the open, and wherever the irradiance volume holds no probe about the
    /// camera.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The share, in [0, 1]; one for a view it did not stage.</returns>
    public float AirLightOf(int view) => (((view >= 0) && (view < m_count)) ? m_airLight[view] : 1f);
    /// <summary>Returns the luminance a view's meter read about its camera, in nits, which the local adaptation measures
    /// each surface's surroundings against.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The reading, or zero for a view it did not stage or whose meter had no reading.</returns>
    public float MeteredOf(int view) => (((view >= 0) && (view < m_count) && m_reads[view]) ? m_metered[view] : 0f);
    /// <summary>Describes each staged view's exposure: its <c>EV100</c>, multiplier, the meter's reading and the open
    /// sky its air sees.</summary>
    /// <returns>One line a view.</returns>
    public IReadOnlyList<string> Describe() {
        var lines = new List<string>(capacity: m_count);

        for (var index = 0; (index < m_count); index++) {
            lines.Add(item: $"view {index}: ev100={m_ev100[index]:0.00} multiplier={m_multipliers[index]:G4} meter={(m_reads[index] ? $"{m_metered[index]:0.###} nits" : "none")} air={m_airLight[index]:0.###}");
        }

        return lines;
    }

    // The meter's reading about a view's camera: the irradiance volume's luminance there, which carries every traced
    // light's bounce, and the open sky the volume sees there (one without a reading).
    private static bool Meter(SdfMeshIrradiance irradiance, in SdfViewSnapshot view, out float luminance, out float airLight) {
        var camera = view.Camera;

        if (!irradiance.TryMeter(forward: camera.Forward, luminance: out luminance, position: camera.Position, skyVisibility: out airLight)) {
            airLight = 1f;

            return false;
        }

        return true;
    }
}
