using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// One residency's view exposures: the multiplier each view of a frame maps the luminance it shades by
/// (<see cref="SdfPhotometry.ExposureOf"/>), which its pass block carries and its composite applies, and the share of the
/// sky the air about its camera sees open, which scales the sky's in-scatter into its atmosphere. A manual exposure is
/// its <c>EV100</c>; an automatic one meters about the view's camera (<see cref="SdfMeshIrradiance.TryMeter"/>) and
/// eases toward that reading over presentation time, each view on its own, snapping to the first reading after a camera
/// cut, which meters at the camera itself rather than reusing a sample taken elsewhere in its cell. A view that
/// publishes its luminance partially exposed (<see cref="SdfViewSnapshot.Unexposed"/>) still takes its exposure, which
/// divides its display-referred terms, while its composite applies the window share of it
/// (<see cref="SdfPhotometry.WindowAdaptation"/>). Presentation only: no value here reaches simulation state.
/// </summary>
public sealed class SdfViewExposures {
    private float[] m_ev100 = [];
    private long[] m_cuts = [];
    private bool[] m_cutOwed = [];
    private float[] m_multipliers = [];
    private float[] m_metered = [];
    private float[] m_airLight = [];
    // Six rows a view (LightOf), W one once the meter has read.
    private Vector4[] m_light = [];
    private bool[] m_reads = [];
    private string[] m_sources = [];
    // Per view, the presentation clock and the frame serial it was last staged at.
    private double[] m_stagedAt = [];
    private long[] m_stagedFrame = [];
    private int m_count;
    // The open frame (Stage) its views stage against on their first read, the presentation clock its seconds advance, and
    // whether reads may stage (false while a frame holds every exposure).
    private SdfFrame? m_frame;
    private SdfMeshIrradiance? m_irradiance;
    private double m_clock;
    private long m_frameSerial;
    private bool m_open;

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
    /// <summary>Opens a frame's exposures: each view is staged on its first read in the frame (<see cref="MultiplierOf"/>,
    /// <see cref="AirLightOf"/>, <see cref="MeteredOf"/>, <see cref="LightOf"/>), its <c>EV100</c> eased toward its target over the presentation
    /// time since it was last staged, so a view nothing renders or reads meters nothing and a view rendered at a reduced
    /// rate meters at that rate.</summary>
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
            Array.Resize(array: ref m_cutOwed, newSize: grown);
            Array.Resize(array: ref m_multipliers, newSize: grown);
            Array.Resize(array: ref m_metered, newSize: grown);
            Array.Resize(array: ref m_airLight, newSize: grown);
            Array.Resize(array: ref m_light, newSize: (grown * 6));
            Array.Resize(array: ref m_reads, newSize: grown);
            Array.Resize(array: ref m_sources, newSize: grown);
            Array.Resize(array: ref m_stagedAt, newSize: grown);
            Array.Resize(array: ref m_stagedFrame, newSize: grown);
            Array.Fill(array: m_ev100, count: (grown - first), startIndex: first, value: float.NaN);
            Array.Fill(array: m_multipliers, count: (grown - first), startIndex: first, value: 1f);
            Array.Fill(array: m_airLight, count: (grown - first), startIndex: first, value: 1f);
        }
        for (var index = count; (index < m_count); index++) {
            m_ev100[index] = float.NaN;
        }

        m_count = count;
        m_frame = frame;
        m_irradiance = irradiance;
        m_clock += Math.Max(val1: 0d, val2: seconds);
        m_open = true;
        m_frameSerial++;
        SkyPeakLuminance = irradiance.SkyPeakLuminance;
    }
    /// <summary>Holds every view's exposure through this frame: a locked tick whose geometry is still landing adapts
    /// nothing, and its reads take the exposures last staged.</summary>
    public void Hold() => m_open = false;
    // Stages a view on its first read in an open frame: meters about its camera and eases its EV100 over the presentation
    // time since it was last staged. A cut stays owed until the meter reads, and the first reading after it snaps.
    private void StageView(int index) {
        if (!m_open || (m_stagedFrame[index] == m_frameSerial) || (m_frame is not { } frame) || (m_irradiance is not { } irradiance)) {
            return;
        }

        m_stagedFrame[index] = m_frameSerial;

        var view = frame.Views[index];
        var exposure = frame.Exposure;
        var seconds = (float.IsFinite(f: m_ev100[index]) ? ((float)(m_clock - m_stagedAt[index])) : 0f);

        m_stagedAt[index] = m_clock;
        m_cutOwed[index] |= (view.CutRevision != m_cuts[index]);

        var cut = m_cutOwed[index];
        var ev100 = exposure.Ev100;

        m_cuts[index] = view.CutRevision;
        // Another residency renders the view and meters it there; its camera is in that world's coordinates.
        if (view.Elsewhere) {
            m_reads[index] = false;
            m_sources[index] = "elsewhere";
            m_multipliers[index] = SdfPhotometry.ExposureOf(ev100: (float.IsFinite(f: m_ev100[index]) ? m_ev100[index] : ev100));
            return;
        }
        Span<Vector3> light = stackalloc Vector3[6];

        m_reads[index] = irradiance.TryMeter(forward: view.Camera.Forward, fresh: cut, light: light, luminance: out var luminance,
            position: view.Camera.Position, skyVisibility: out var airLight, source: out var source);
        if (m_reads[index]) { m_cutOwed[index] = false; }
        m_sources[index] = source switch {
            SdfExposureMeterSource.Probes => "probes",
            SdfExposureMeterSource.CameraTrace => "camera-trace",
            _ => (float.IsFinite(f: m_ev100[index]) ? "held" : "initial"),
        };
        if (m_reads[index] || !float.IsFinite(f: m_ev100[index])) {
            m_airLight[index] = airLight;
            m_metered[index] = luminance;
            for (var axis = 0; (axis < 6); axis++) { m_light[(index * 6) + axis] = (m_reads[index] ? new Vector4(value: light[axis], w: 1f) : Vector4.Zero); }
        }
        if (exposure.Mode == SdfExposureMode.Auto) {
            ev100 = (m_reads[index]
                ? exposure.Target(luminance: m_metered[index])
                : (float.IsFinite(f: m_ev100[index]) ? m_ev100[index] : Math.Clamp(value: exposure.Ev100, min: exposure.Minimum, max: MathF.Max(x: exposure.Minimum, y: exposure.Maximum))));
            ev100 = (cut ? ev100 : exposure.Adapt(current: m_ev100[index], seconds: seconds, target: ev100));
        }

        m_ev100[index] = ev100;
        m_multipliers[index] = SdfPhotometry.ExposureOf(ev100: ev100);
    }
    /// <summary>Returns a view's exposure multiplier, staging the view on its first read in the frame, or one for a view
    /// the frame does not hold.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The multiplier, per nit.</returns>
    public float MultiplierOf(int view) {
        if ((view < 0) || (view >= m_count)) { return 1f; }
        StageView(index: view);
        return m_multipliers[view];
    }
    /// <summary>Returns the share of the sky the air about a view's camera sees open, which scales the sky's in-scatter
    /// into the view's atmosphere, staging the view on its first read in the frame: one in the open, held at its last
    /// reading while the scene cannot answer.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The share, in [0, 1]; one for a view the frame does not hold.</returns>
    public float AirLightOf(int view) {
        if ((view < 0) || (view >= m_count)) { return 1f; }
        StageView(index: view);
        return m_airLight[view];
    }
    /// <summary>Returns the luminance a view's meter read about its camera, in nits, which the local adaptation measures
    /// each surface's surroundings against, staging the view on its first read in the frame; held at its last reading
    /// while the scene cannot answer.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The last reading, or zero for a view the frame does not hold or whose meter never had a reading.</returns>
    public float MeteredOf(int view) {
        if ((view < 0) || (view >= m_count)) { return 0f; }
        StageView(index: view);
        return m_metered[view];
    }
    /// <summary>Writes the light a view's meter sample receives along each of its six axes (+X, -X, +Y, -Y, +Z, -Z), RGB
    /// in nits with W one, staging the view on its first read in the frame; held at its last reading while the scene
    /// cannot answer. A surface no published probe answers reads it as one probe at the camera.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <param name="rows">The six rows to fill; zero, W included, for a view the frame does not hold or whose meter never
    /// had a reading.</param>
    /// <exception cref="ArgumentException"><paramref name="rows"/> is shorter than six.</exception>
    public void LightOf(int view, Span<Vector4> rows) {
        if (rows.Length < 6) { throw new ArgumentException(message: "The meter's light is six rows.", paramName: nameof(rows)); }
        rows[..6].Clear();
        if ((view < 0) || (view >= m_count)) { return; }
        StageView(index: view);
        m_light.AsSpan(length: 6, start: (view * 6)).CopyTo(destination: rows);
    }
    /// <summary>Returns the <c>EV100</c> a view rendered at in the frame last staged, without staging it.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The <c>EV100</c>, or <see cref="float.NaN"/> for a view the frame does not hold or that has not rendered.</returns>
    public float Ev100Of(int view) => (((view < 0) || (view >= m_count)) ? float.NaN : m_ev100[view]);
    /// <summary>Describes each staged view's exposure: its <c>EV100</c>, multiplier, the meter's reading and the open
    /// sky its air sees.</summary>
    /// <returns>One line a view.</returns>
    public IReadOnlyList<string> Describe() {
        var lines = new List<string>(capacity: m_count);

        for (var index = 0; (index < m_count); index++) {
            lines.Add(item: $"view {index}: ev100={m_ev100[index]:0.00} multiplier={m_multipliers[index]:G4} meter={(m_reads[index] ? $"{m_metered[index]:0.###} nits" : "none")} source={m_sources[index]} air={m_airLight[index]:0.###}");
        }

        return lines;
    }

}
