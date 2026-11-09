using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// One residency's view exposures: the multiplier each view of a frame maps the luminance it shades by
/// (<see cref="SdfPhotometry.ExposureOf"/>), which its pass block carries and its composite applies, and the view's camera
/// sample of the irradiance volume (<see cref="SdfMeshIrradiance.TrySampleCamera"/>): the share of the sky the air about
/// its camera sees open, which scales the sky's in-scatter into its atmosphere, and the six-axis light a surface no probe
/// answers is lit by. A manual exposure is its <c>EV100</c>. An automatic one meters the view's own image: each view's
/// composite bins its output into a log histogram (<see cref="SdfExposure.Meter"/>), which the composite's recorder hands
/// over once the frame that drew it completes (<see cref="Publish"/>), a few frames after it rendered. The exposure holds
/// while the reading stays within the dead band (<see cref="SdfExposure.Band"/>) of the one it last settled on, and once
/// a reading leaves the band it follows the meter, easing over presentation time, until it settles again; after a camera
/// cut it holds until a reading the view rendered after the cut lands, then snaps to it, so the exposure after a teleport
/// depends only on the destination's picture. A view that publishes its luminance partially exposed
/// (<see cref="SdfViewSnapshot.Unexposed"/>) still takes its exposure, which divides its display-referred terms, while
/// its composite applies the window share of it (<see cref="SdfPhotometry.WindowAdaptation"/>); it meters its own image
/// at its whole exposure. Presentation only: no value here reaches simulation state.
/// </summary>
public sealed class SdfViewExposures {
    private float[] m_ev100 = [];
    // The meter's state in metered EV100, before compensation and bounds: the reading the exposure follows or holds, the
    // value eased toward it (whose luminance MeteredOf gives the local adaptation), and whether a reading left the band.
    private float[] m_heldEv = [];
    private float[] m_meterEv = [];
    private bool[] m_adapting = [];
    private float[] m_metered = [];
    private string[] m_meterSources = [];
    private long[] m_cuts = [];
    // A cut owes the camera sample a fresh reading at the camera and the meter a reading rendered after it.
    private bool[] m_sampleOwed = [];
    private bool[] m_meterOwed = [];
    private float[] m_multipliers = [];
    private float[] m_airLight = [];
    // Six rows a view (LightOf), W one once the camera sample has read.
    private Vector4[] m_light = [];
    private bool[] m_sampled = [];
    private string[] m_sampleSources = [];
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
    // Each view's newest published image reading not yet staged. A composite's recorder publishes from the node's slot
    // completion, which can run beside another node's recording, so readings cross under their own gate.
    private readonly Lock m_readingGate = new();
    private Reading?[] m_readings = [];

    private sealed class Reading {
        internal readonly uint[] Bins = new uint[SdfExposure.MeterBins];
        internal long Cut;
        internal float Multiplier;
        internal bool Fresh;
    }

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
    /// <summary>Hands over the image meter of a frame a view's composite drew, once its submission completed: the view's
    /// newest reading, which the view's next staging reads, replacing any reading not yet staged.</summary>
    /// <param name="view">The view's index in the frame the composite drew.</param>
    /// <param name="cut">The view's cut revision in that frame (<see cref="SdfViewSnapshot.CutRevision"/>).</param>
    /// <param name="multiplier">The exposure multiplier the composite binned the image at.</param>
    /// <param name="bins">The histogram, <see cref="SdfExposure.MeterBins"/> weights.</param>
    /// <exception cref="ArgumentException"><paramref name="bins"/> is not <see cref="SdfExposure.MeterBins"/> long.</exception>
    public void Publish(int view, long cut, float multiplier, ReadOnlySpan<uint> bins) {
        if (bins.Length != SdfExposure.MeterBins) { throw new ArgumentException(message: $"The image meter's histogram is {SdfExposure.MeterBins} bins.", paramName: nameof(bins)); }
        if (view < 0) { return; }
        lock (m_readingGate) {
            if (m_readings.Length <= view) { Array.Resize(array: ref m_readings, newSize: Math.Max(val1: (view + 1), val2: (m_readings.Length * 2))); }
            var reading = (m_readings[view] ??= new Reading());

            bins.CopyTo(destination: reading.Bins);
            reading.Cut = cut;
            reading.Multiplier = multiplier;
            reading.Fresh = true;
        }
    }
    // Takes a view's newest published reading not yet staged.
    private bool TakeReading(int view, Span<uint> bins, out long cut, out float multiplier) {
        lock (m_readingGate) {
            cut = 0;
            multiplier = 0f;
            if ((view >= m_readings.Length) || (m_readings[view] is not { Fresh: true } reading)) { return false; }
            reading.Bins.CopyTo(destination: bins);
            cut = reading.Cut;
            multiplier = reading.Multiplier;
            reading.Fresh = false;
            return true;
        }
    }
    /// <summary>Opens a frame's exposures: each view is staged on its first read in the frame (<see cref="MultiplierOf"/>,
    /// <see cref="AirLightOf"/>, <see cref="MeteredOf"/>, <see cref="LightOf"/>), its <c>EV100</c> eased toward its target
    /// over the presentation time since it was last staged, so a view nothing renders or reads meters nothing and a view
    /// rendered at a reduced rate adapts at that rate.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="irradiance">The residency's irradiance volume, which the camera sample reads.</param>
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
            Array.Resize(array: ref m_heldEv, newSize: grown);
            Array.Resize(array: ref m_meterEv, newSize: grown);
            Array.Resize(array: ref m_adapting, newSize: grown);
            Array.Resize(array: ref m_metered, newSize: grown);
            Array.Resize(array: ref m_meterSources, newSize: grown);
            Array.Resize(array: ref m_cuts, newSize: grown);
            Array.Resize(array: ref m_sampleOwed, newSize: grown);
            Array.Resize(array: ref m_meterOwed, newSize: grown);
            Array.Resize(array: ref m_multipliers, newSize: grown);
            Array.Resize(array: ref m_airLight, newSize: grown);
            Array.Resize(array: ref m_light, newSize: (grown * 6));
            Array.Resize(array: ref m_sampled, newSize: grown);
            Array.Resize(array: ref m_sampleSources, newSize: grown);
            Array.Resize(array: ref m_stagedAt, newSize: grown);
            Array.Resize(array: ref m_stagedFrame, newSize: grown);
            Array.Fill(array: m_ev100, count: (grown - first), startIndex: first, value: float.NaN);
            Array.Fill(array: m_heldEv, count: (grown - first), startIndex: first, value: float.NaN);
            Array.Fill(array: m_meterEv, count: (grown - first), startIndex: first, value: float.NaN);
            Array.Fill(array: m_multipliers, count: (grown - first), startIndex: first, value: 1f);
            Array.Fill(array: m_airLight, count: (grown - first), startIndex: first, value: 1f);
        }
        for (var index = count; (index < m_count); index++) {
            m_ev100[index] = float.NaN;
            m_heldEv[index] = float.NaN;
            m_meterEv[index] = float.NaN;
            m_adapting[index] = false;
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
    // Stages a view on its first read in an open frame: samples the volume at its camera, takes its newest image reading
    // and eases its EV100 over the presentation time since it was last staged.
    private void StageView(int index) {
        if (!m_open || (m_stagedFrame[index] == m_frameSerial) || (m_frame is not { } frame) || (m_irradiance is not { } irradiance)) {
            return;
        }

        m_stagedFrame[index] = m_frameSerial;

        var view = frame.Views[index];
        var exposure = frame.Exposure;
        var seconds = (float.IsFinite(f: m_ev100[index]) ? ((float)(m_clock - m_stagedAt[index])) : 0f);

        m_stagedAt[index] = m_clock;
        if (view.CutRevision != m_cuts[index]) {
            m_cuts[index] = view.CutRevision;
            m_sampleOwed[index] = true;
            m_meterOwed[index] = true;
        }
        // Another residency renders the view and meters it there; its camera is in that world's coordinates.
        if (view.Elsewhere) {
            m_sampleSources[index] = "elsewhere";
            m_meterSources[index] = "elsewhere";
            m_multipliers[index] = SdfPhotometry.ExposureOf(ev100: (float.IsFinite(f: m_ev100[index]) ? m_ev100[index] : exposure.Ev100));
            return;
        }
        SampleCamera(index: index, irradiance: irradiance, view: view);

        var snap = Meter(exposure: in exposure, index: index);
        var ev100 = exposure.Ev100;

        if (exposure.Mode == SdfExposureMode.Auto) {
            var current = m_ev100[index];

            if (!float.IsFinite(f: m_heldEv[index])) {
                ev100 = (float.IsFinite(f: current) ? current : exposure.Clamp(ev100: exposure.Ev100));
            } else {
                var target = exposure.Clamp(ev100: (m_heldEv[index] - exposure.Compensation));

                ev100 = ((snap || !float.IsFinite(f: current)) ? target : (m_meterOwed[index] ? current : exposure.Adapt(current: current, seconds: seconds, target: target)));
            }
        }
        m_ev100[index] = ev100;
        m_multipliers[index] = SdfPhotometry.ExposureOf(ev100: ev100);

        // The meter eases toward the reading it follows; once it arrives, the band holds again.
        if (!snap && !m_meterOwed[index] && float.IsFinite(f: m_heldEv[index])) {
            m_meterEv[index] = exposure.Adapt(current: m_meterEv[index], seconds: seconds, target: m_heldEv[index]);
            if (m_meterEv[index] == m_heldEv[index]) { m_adapting[index] = false; }
        }
    }
    // Samples the volume at the view's camera; a failed sample holds the last one (none before the first).
    private void SampleCamera(int index, SdfMeshIrradiance irradiance, SdfViewSnapshot view) {
        Span<Vector3> light = stackalloc Vector3[6];

        var sampled = irradiance.TrySampleCamera(fresh: m_sampleOwed[index], light: light, position: view.Camera.Position, skyVisibility: out var airLight, source: out var source);

        if (sampled) {
            m_sampleOwed[index] = false;
            m_sampled[index] = true;
            m_airLight[index] = airLight;
            for (var axis = 0; (axis < 6); axis++) { m_light[(index * 6) + axis] = new Vector4(value: light[axis], w: 1f); }
        } else if (!m_sampled[index]) {
            m_airLight[index] = 1f;
            m_light.AsSpan(length: 6, start: (index * 6)).Clear();
        }
        m_sampleSources[index] = (source switch {
            SdfCameraSampleSource.Probes => "probes",
            SdfCameraSampleSource.CameraTrace => "camera-trace",
            _ => (m_sampled[index] ? "held" : "none"),
        });
    }
    // Takes the view's newest image reading and moves the band: a reading after a cut (or the first) snaps the meter to
    // it, a reading beyond the band from the one held starts following, and a reading while following is followed.
    // Returns whether the meter snapped.
    private bool Meter(in SdfExposure exposure, int index) {
        Span<uint> bins = stackalloc uint[SdfExposure.MeterBins];

        if (TakeReading(bins: bins, cut: out var cut, multiplier: out var multiplier, view: index) &&
            (!m_meterOwed[index] || (cut == m_cuts[index])) &&
            exposure.Meter(bins: bins, luminance: out var luminance, multiplier: multiplier)) {
            var reading = SdfPhotometry.MeteredEv100(luminance: luminance);

            m_metered[index] = luminance;
            m_meterSources[index] = "image";
            if (m_meterOwed[index] || !float.IsFinite(f: m_heldEv[index])) {
                m_meterOwed[index] = false;
                m_adapting[index] = false;
                m_heldEv[index] = reading;
                m_meterEv[index] = reading;
                return true;
            }
            if (m_adapting[index] || (MathF.Abs(x: (reading - m_heldEv[index])) > exposure.Band)) {
                m_adapting[index] = true;
                m_heldEv[index] = reading;
            }
            return false;
        }
        m_meterSources[index] = ((m_meterOwed[index] && float.IsFinite(f: m_heldEv[index])) ? "cut" : (float.IsFinite(f: m_heldEv[index]) ? "held" : "initial"));
        return false;
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
    /// sample while the volume cannot answer.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The share, in [0, 1]; one for a view the frame does not hold.</returns>
    public float AirLightOf(int view) {
        if ((view < 0) || (view >= m_count)) { return 1f; }
        StageView(index: view);
        return m_airLight[view];
    }
    /// <summary>Returns the scene luminance a view's image meter stands at, in nits, eased as its exposure is and held
    /// within its band, which the local adaptation measures each surface's illumination against, staging the view on its
    /// first read in the frame.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The luminance, or zero for a view the frame does not hold or whose image has not metered yet.</returns>
    public float MeteredOf(int view) {
        if ((view < 0) || (view >= m_count)) { return 0f; }
        StageView(index: view);
        return (float.IsFinite(f: m_meterEv[view])
            ? ((SdfPhotometry.MeterCalibration / 100f) * MathF.Pow(x: 2f, y: m_meterEv[view]))
            : 0f);
    }
    /// <summary>Writes the light a view's camera sample receives along each of its six axes (+X, -X, +Y, -Y, +Z, -Z), RGB
    /// in nits with W one, staging the view on its first read in the frame; held at its last sample while the volume
    /// cannot answer. A surface no published probe answers reads it as one probe at the camera.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <param name="rows">The six rows to fill; zero, W included, for a view the frame does not hold or whose camera
    /// sample never read.</param>
    /// <exception cref="ArgumentException"><paramref name="rows"/> is shorter than six.</exception>
    public void LightOf(int view, Span<Vector4> rows) {
        if (rows.Length < 6) { throw new ArgumentException(message: "The camera sample's light is six rows.", paramName: nameof(rows)); }
        rows[..6].Clear();
        if ((view < 0) || (view >= m_count)) { return; }
        StageView(index: view);
        m_light.AsSpan(length: 6, start: (view * 6)).CopyTo(destination: rows);
    }
    /// <summary>Returns the <c>EV100</c> a view rendered at in the frame last staged, without staging it.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The <c>EV100</c>, or <see cref="float.NaN"/> for a view the frame does not hold or that has not rendered.</returns>
    public float Ev100Of(int view) => (((view < 0) || (view >= m_count)) ? float.NaN : m_ev100[view]);
    /// <summary>Describes each staged view's exposure: its <c>EV100</c> and multiplier; its image meter's latest reading,
    /// the <c>EV100</c> that reading aims for, where the reading came from (<c>image</c>, <c>held</c> without a new
    /// reading, <c>cut</c> awaiting one rendered after a cut, <c>initial</c> before any) and whether the band holds or
    /// follows; and its camera sample's source and open sky.</summary>
    /// <returns>One line a view.</returns>
    public IReadOnlyList<string> Describe() {
        var lines = new List<string>(capacity: m_count);
        var exposure = (m_frame?.Exposure ?? SdfExposure.Default);

        for (var index = 0; (index < m_count); index++) {
            var metered = float.IsFinite(f: m_heldEv[index]);
            var reading = (metered ? $"{m_metered[index]:0.###} nits target={exposure.Target(luminance: m_metered[index]):0.00} band={(m_adapting[index] ? "following" : "holding")}" : "none");

            lines.Add(item: $"view {index}: ev100={m_ev100[index]:0.00} multiplier={m_multipliers[index]:G4} meter={reading} source={m_meterSources[index]} camera={m_sampleSources[index]} air={m_airLight[index]:0.###}");
        }

        return lines;
    }
}
