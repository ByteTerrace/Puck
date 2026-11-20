using Puck.Abstractions.Collections;
using System.Numerics;
using Puck.Abstractions.Memory;
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
    [System.Runtime.CompilerServices.InlineArray(6)]
    private struct LightRows { private Vector4 m_first; }

    private struct ViewState {
        internal float Ev100;
        internal float HeldEv;
        internal float MeterEv;
        internal bool Adapting;
        internal float Metered;
        internal string? MeterSource;
        internal ReferenceRevision Cut;
        internal long Generation;
        internal bool SampleOwed;
        internal bool MeterOwed;
        internal float Multiplier;
        internal float AirLight;
        internal LightRows Light;
        internal bool Sampled;
        internal string? SampleSource;
        internal double StagedAt;
        internal long StagedFrame;
    }
    private ViewState[] m_views = [];
    private int m_count;
    // The open frame (Stage) its views stage against on their first read, the presentation clock its seconds advance, and
    // whether reads may stage (false while a frame holds every exposure).
    private SdfFrame? m_frame;
    // The open frame's exposure, kept apart from the frame, which its residency writes again at a later capture.
    private SdfExposure m_exposure = SdfExposure.Default;
    private SdfMeshIrradiance? m_irradiance;
    private double m_clock;
    private long m_frameSerial;
    private bool m_open;
    // Each view's newest published image reading not yet staged. A composite's recorder publishes from the node's slot
    // completion, which can run beside another node's recording, so readings cross under their own gate.
    private readonly Lock m_readingGate = new();
    private Reading?[] m_readings = [];

    private sealed class Reading {
        internal readonly uint[] Words = new uint[SdfExposure.MeterWords];
        internal long Generation;
        internal float Multiplier;
        internal float Reference;
        internal float Local;
        internal bool Fresh;
    }

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
    /// newest reading, which the view's next staging reads, replacing any reading not yet staged, and discards unless the
    /// slot still holds the generation the reading was recorded under.</summary>
    /// <param name="view">The view's index in the frame the composite drew.</param>
    /// <param name="generation">The slot's generation when the composite recorded (<see cref="GenerationOf"/>).</param>
    /// <param name="multiplier">The exposure multiplier the composite binned the image at.</param>
    /// <param name="reference">The metered <c>EV100</c> the frame's local adaptation shaded against
    /// (<see cref="MeteredOf"/>), or <see cref="float.NaN"/> for none.</param>
    /// <param name="local">The local adaptation's share the frame shaded with, in [0, 1].</param>
    /// <param name="words">The readback, <see cref="SdfExposure.MeterWords"/> words: the histogram, then the covered
    /// weight.</param>
    /// <exception cref="ArgumentException"><paramref name="words"/> is not <see cref="SdfExposure.MeterWords"/> long.</exception>
    public void Publish(int view, long generation, float multiplier, float reference, float local, ReadOnlySpan<uint> words) {
        if (words.Length != SdfExposure.MeterWords) { throw new ArgumentException(message: $"The image meter's readback is {SdfExposure.MeterWords} words.", paramName: nameof(words)); }
        if (view < 0) { return; }
        lock (m_readingGate) {
            ArrayCapacity.EnsureLength(ref m_readings, view + 1);
            var reading = (m_readings[view] ??= new Reading());

            words.CopyTo(destination: reading.Words);
            reading.Generation = generation;
            reading.Multiplier = multiplier;
            reading.Reference = reference;
            reading.Local = local;
            reading.Fresh = true;
        }
    }
    // Takes a view's newest published reading not yet staged, discarding one recorded under a retired generation of the slot.
    private bool TakeReading(int view, Span<uint> words, out float multiplier, out float reference, out float local) {
        lock (m_readingGate) {
            (multiplier, reference, local) = (0f, float.NaN, 0f);
            if ((view >= m_readings.Length) || (m_readings[view] is not { Fresh: true } reading)) { return false; }
            reading.Fresh = false;
            if (reading.Generation != m_views[view].Generation) { return false; }
            reading.Words.CopyTo(destination: words);
            (multiplier, reference, local) = (reading.Multiplier, reading.Reference, reading.Local);
            return true;
        }
    }
    /// <summary>Returns the generation of the view a slot holds: it moves whenever another view (another source, or
    /// another cut, <see cref="SdfViewSnapshot.Source"/>, <see cref="SdfViewSnapshot.CutRevision"/>) takes the slot,
    /// once that view is staged. A meter recorded under it (<see cref="Publish"/>) is read only while the slot holds that
    /// generation, so a reading of a view the slot no longer shows never sets another's exposure. Zero for a slot the
    /// frame does not hold.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The generation.</returns>
    public long GenerationOf(int view) => (((view < 0) || (view >= m_count)) ? 0L : m_views[view].Generation);
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

        if (count > m_views.Length) {
            var first = m_views.Length;
            ArrayCapacity.EnsureLength(ref m_views, count);
            m_views.AsSpan(first).Fill(new ViewState {
                Ev100 = float.NaN, HeldEv = float.NaN, MeterEv = float.NaN, Multiplier = 1f, AirLight = 1f,
            });
        }
        for (var index = count; (index < m_count); index++) {
            m_views[index].Ev100 = float.NaN;
            m_views[index].HeldEv = float.NaN;
            m_views[index].MeterEv = float.NaN;
            m_views[index].Adapting = false;
        }

        m_count = count;
        m_frame = frame;
        m_exposure = frame.Exposure;
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
        if (!m_open || (m_views[index].StagedFrame == m_frameSerial) || (m_frame is not { } frame) || (m_irradiance is not { } irradiance)) {
            return;
        }

        m_views[index].StagedFrame = m_frameSerial;

        var view = frame.Views[index];
        var exposure = frame.Exposure;
        var seconds = (float.IsFinite(f: m_views[index].Ev100) ? ((float)(m_clock - m_views[index].StagedAt)) : 0f);

        m_views[index].StagedAt = m_clock;
        // Another view in the slot is a cut: a new generation, whose camera sample and meter the slot owes afresh.
        if (view.Cut != m_views[index].Cut) {
            m_views[index].Cut = view.Cut;
            m_views[index].Generation++;
            m_views[index].Sampled = false;
            m_views[index].SampleOwed = true;
            m_views[index].MeterOwed = true;
        }
        // Another residency renders the view and meters it there; its camera is in that world's coordinates.
        if (view.Elsewhere) {
            m_views[index].SampleSource = "elsewhere";
            m_views[index].MeterSource = "elsewhere";
            m_views[index].Multiplier = SdfPhotometry.ExposureOf(ev100: (float.IsFinite(f: m_views[index].Ev100) ? m_views[index].Ev100 : exposure.Ev100));
            return;
        }
        SampleCamera(index: index, irradiance: irradiance, view: view);

        var snap = Meter(exposure: in exposure, index: index);
        var ev100 = exposure.Ev100;

        if (exposure.Mode == SdfExposureMode.Auto) {
            var current = m_views[index].Ev100;

            if (!float.IsFinite(f: m_views[index].HeldEv)) {
                ev100 = (float.IsFinite(f: current) ? current : exposure.Clamp(ev100: exposure.Ev100));
            } else {
                var target = exposure.Clamp(ev100: (m_views[index].HeldEv - exposure.Compensation));

                ev100 = ((snap || !float.IsFinite(f: current)) ? target : (m_views[index].MeterOwed ? current : exposure.Adapt(current: current, seconds: seconds, target: target)));
            }
        }
        m_views[index].Ev100 = ev100;
        m_views[index].Multiplier = SdfPhotometry.ExposureOf(ev100: ev100);

        // The meter eases toward the reading it follows; once it arrives, the band holds again.
        if (!snap && !m_views[index].MeterOwed && float.IsFinite(f: m_views[index].HeldEv)) {
            m_views[index].MeterEv = exposure.Adapt(current: m_views[index].MeterEv, seconds: seconds, target: m_views[index].HeldEv);
            if (m_views[index].MeterEv == m_views[index].HeldEv) { m_views[index].Adapting = false; }
        }
    }
    // Samples the volume at the view's camera; a failed sample holds the last one (none before the first).
    private void SampleCamera(int index, SdfMeshIrradiance irradiance, SdfViewSnapshot view) {
        Span<Vector3> light = stackalloc Vector3[6];

        var sampled = irradiance.TrySampleCamera(fresh: m_views[index].SampleOwed, light: light, position: view.Camera.Position, skyVisibility: out var airLight, source: out var source);

        if (sampled) {
            m_views[index].SampleOwed = false;
            m_views[index].Sampled = true;
            m_views[index].AirLight = airLight;
            for (var axis = 0; (axis < 6); axis++) { m_views[index].Light[axis] = new Vector4(value: light[axis], w: 1f); }
        } else if (!m_views[index].Sampled) {
            m_views[index].AirLight = 1f;
            m_views[index].Light = default;
        }
        m_views[index].SampleSource = (source switch {
            SdfCameraSampleSource.Probes => "probes",
            SdfCameraSampleSource.CameraTrace => "camera-trace",
            _ => (m_views[index].Sampled ? "held" : "none"),
        });
    }
    // Takes the view's newest image reading, settled for the local adaptation it was shaded with, and moves the band: a
    // reading after a cut (or the first) snaps the meter to it, a reading beyond the band from the one held starts
    // following, and a reading while following is followed. Returns whether the meter snapped.
    private bool Meter(in SdfExposure exposure, int index) {
        Span<uint> words = stackalloc uint[SdfExposure.MeterWords];

        if (TakeReading(local: out var local, multiplier: out var multiplier, reference: out var reference, view: index, words: words) &&
            exposure.Meter(bins: words[..SdfExposure.MeterBins], luminance: out var luminance, multiplier: multiplier)) {
            var total = 0d;

            foreach (var weight in words[..SdfExposure.MeterBins]) { total += weight; }
            var covered = (float)Math.Clamp(value: (words[SdfExposure.MeterBins] / total), min: 0d, max: 1d);
            var reading = SdfExposure.Settled(covered: covered, local: local, reading: SdfPhotometry.MeteredEv100(luminance: luminance), reference: reference);

            m_views[index].Metered = ((SdfPhotometry.MeterCalibration / 100f) * MathF.Pow(x: 2f, y: reading));
            m_views[index].MeterSource = "image";
            if (m_views[index].MeterOwed || !float.IsFinite(f: m_views[index].HeldEv)) {
                m_views[index].MeterOwed = false;
                m_views[index].Adapting = false;
                m_views[index].HeldEv = reading;
                m_views[index].MeterEv = reading;
                return true;
            }
            if (m_views[index].Adapting || (MathF.Abs(x: (reading - m_views[index].HeldEv)) > exposure.Band)) {
                m_views[index].Adapting = true;
                m_views[index].HeldEv = reading;
            }
            return false;
        }
        m_views[index].MeterSource = ((m_views[index].MeterOwed && float.IsFinite(f: m_views[index].HeldEv)) ? "cut" : (float.IsFinite(f: m_views[index].HeldEv) ? "held" : "initial"));
        return false;
    }
    /// <summary>Returns a view's exposure multiplier, staging the view on its first read in the frame, or one for a view
    /// the frame does not hold.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The multiplier, per nit.</returns>
    public float MultiplierOf(int view) {
        if ((view < 0) || (view >= m_count)) { return 1f; }
        StageView(index: view);
        return m_views[view].Multiplier;
    }
    /// <summary>Returns the share of the sky the air about a view's camera sees open, which scales the sky's in-scatter
    /// into the view's atmosphere, staging the view on its first read in the frame: one in the open, held at its last
    /// sample while the volume cannot answer.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The share, in [0, 1]; one for a view the frame does not hold.</returns>
    public float AirLightOf(int view) {
        if ((view < 0) || (view >= m_count)) { return 1f; }
        StageView(index: view);
        return m_views[view].AirLight;
    }
    /// <summary>Returns the scene luminance a view's image meter stands at, in nits, eased as its exposure is and held
    /// within its band, which the local adaptation measures each surface's illumination against, staging the view on its
    /// first read in the frame.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The luminance, or zero for a view the frame does not hold or whose image has not metered yet.</returns>
    public float MeteredOf(int view) {
        if ((view < 0) || (view >= m_count)) { return 0f; }
        StageView(index: view);
        return (float.IsFinite(f: m_views[view].MeterEv)
            ? ((SdfPhotometry.MeterCalibration / 100f) * MathF.Pow(x: 2f, y: m_views[view].MeterEv))
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
        ((ReadOnlySpan<Vector4>)m_views[view].Light).CopyTo(rows);
    }
    /// <summary>Returns the <c>EV100</c> a view rendered at in the frame last staged, without staging it.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns>The <c>EV100</c>, or <see cref="float.NaN"/> for a view the frame does not hold or that has not rendered.</returns>
    public float Ev100Of(int view) => (((view < 0) || (view >= m_count)) ? float.NaN : m_views[view].Ev100);
    /// <summary>Describes each staged view's exposure: its <c>EV100</c> and multiplier; its image meter's latest reading,
    /// the <c>EV100</c> that reading aims for, where the reading came from (<c>image</c>, <c>held</c> without a new
    /// reading, <c>cut</c> awaiting one rendered after a cut, <c>initial</c> before any) and whether the band holds or
    /// follows; and its camera sample's source and open sky.</summary>
    /// <returns>One line a view.</returns>
    public IReadOnlyList<string> Describe() {
        var lines = new List<string>(capacity: m_count);
        var exposure = m_exposure;

        for (var index = 0; (index < m_count); index++) {
            var metered = float.IsFinite(f: m_views[index].HeldEv);
            var reading = (metered ? $"{m_views[index].Metered:0.###} nits target={exposure.Target(luminance: m_views[index].Metered):0.00} band={(m_views[index].Adapting ? "following" : "holding")}" : "none");
            // A view without a fresh camera sample names what the sample waits for now.
            var wait = (((m_views[index].SampleSource is "none" or "held") && (m_irradiance is { } irradiance)) ? $" wait={irradiance.CameraSampleWait()}" : string.Empty);

            lines.Add(item: $"view {index}: ev100={m_views[index].Ev100:0.00} multiplier={m_views[index].Multiplier:G4} meter={reading} source={m_views[index].MeterSource} camera={m_views[index].SampleSource}{wait} air={m_views[index].AirLight:0.###}");
        }

        return lines;
    }
}
