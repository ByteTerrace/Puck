using Puck.Abstractions.Lighting;

namespace Puck.Input.Lighting;

/// <summary>
/// A short, self-contained score celebration for a lamp array: a colored wavefront sweeps the array twice along
/// its X axis, the whole board settles into a gently pulsing glow, then fades to black. The color grades the
/// score against a reference — green at reference-or-better, amber within striking distance, ember below — so a
/// benchmark completion reads at a glance from the hardware itself, no screen required. Purely presentational
/// (it drives lamps and nothing else), and deliberately engine-side so ANY host with a lamp array can fire one:
/// a scored bench run, a green build, a shipped release. Developer kin, celebrate freely.
/// </summary>
public sealed class LightCelebration {
    private const double FadeSeconds = 0.7;
    private const double GlowPulseSeconds = 0.7;
    private const double GlowSeconds = 1.4;
    private const double SweepPassSeconds = 0.9;
    private const double SweepSeconds = 1.8;
    private const float SweepTrailScale = 0.10f;
    private const float WaveWidth = 0.12f;

    private readonly ILampArrayDevice m_device;
    private readonly LampColor[] m_frame;
    private readonly int[] m_lampIds;
    private readonly float[] m_lampX;
    private readonly double m_updateIntervalSeconds;
    private double m_accumulator;
    private double m_elapsedSeconds;
    // Volatile: Begin (arming) is called from the render thread while Tick / IsPlaying run on the ~30 Hz ticker thread,
    // so the play flag crosses threads and must not be cached.
    private volatile bool m_isPlaying;
    private LampColor m_tint;

    /// <summary>Initializes a celebration over a device, snapshotting each lamp's normalized X position once.</summary>
    /// <param name="device">The lamp array the celebration plays on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public LightCelebration(ILampArrayDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        m_device = device;

        var count = Math.Max(val1: device.LampCount, val2: 1);

        m_frame = new LampColor[count];
        m_lampIds = new int[count];
        m_lampX = new float[count];

        for (var index = 0; (index < count); index++) {
            m_lampIds[index] = index;
            m_lampX[index] = (device.TryGetLampInfo(index: index, info: out var info) ? info.Position.X : 0.5f);
        }

        m_updateIntervalSeconds = Math.Max(val1: (1.0 / 30.0), val2: (device.MinUpdateIntervalInMilliseconds / 1000.0));
    }

    /// <summary>Gets whether a celebration is currently playing.</summary>
    public bool IsPlaying => m_isPlaying;

    /// <summary>
    /// Arms the celebration for a score (restarting it if one is already playing). The tier color is graded
    /// against <paramref name="referenceScore"/>: green at 98% of reference or better, amber at 90%, ember below.
    /// </summary>
    /// <param name="score">The score being celebrated.</param>
    /// <param name="referenceScore">The reference the score is graded against; defaults to the bench's 10000.</param>
    public void Begin(int score, int referenceScore = 10_000) {
        m_tint = TintFor(referenceScore: referenceScore, score: score);
        m_elapsedSeconds = 0.0;
        // Prime the accumulator so the first Tick paints immediately instead of waiting one interval.
        m_accumulator = m_updateIntervalSeconds;
        m_isPlaying = true;
    }

    /// <summary>
    /// Advances the celebration and writes at most one frame per update interval (never faster than the device's
    /// own floor). Returns <see langword="false"/> once the show has finished and the board has been blanked —
    /// the caller's legend repaints from there.
    /// </summary>
    /// <param name="elapsedSeconds">The wall/render time since the previous call, in seconds.</param>
    /// <returns><see langword="true"/> while playing; <see langword="false"/> when finished (or never started).</returns>
    public bool Tick(double elapsedSeconds) {
        if (!m_isPlaying) {
            return false;
        }

        var step = Math.Max(val1: 0.0, val2: elapsedSeconds);

        m_accumulator += step;
        m_elapsedSeconds += step;

        if (m_accumulator < m_updateIntervalSeconds) {
            return true;
        }

        m_accumulator = 0.0;

        var seconds = m_elapsedSeconds;

        if (seconds < SweepSeconds) {
            ComposeSweep(seconds: ((float)seconds));
        } else if (seconds < (SweepSeconds + GlowSeconds)) {
            ComposeGlow(seconds: ((float)(seconds - SweepSeconds)));
        } else if (seconds < ((SweepSeconds + GlowSeconds) + FadeSeconds)) {
            ComposeFade(seconds: ((float)(seconds - (SweepSeconds + GlowSeconds))));
        } else {
            m_isPlaying = false;
            m_device.UpdateAllLamps(color: LampColor.Off);

            return false;
        }

        m_device.UpdateLamps(colors: m_frame, lampIds: m_lampIds);

        return true;
    }

    // Phase 1: a wavefront crosses the array once per pass (two passes), with a faint trail that brightens as the
    // show progresses so the second pass rides over a warming board.
    private void ComposeSweep(float seconds) {
        var pass = (seconds / ((float)SweepPassSeconds));
        var fraction = (pass - MathF.Floor(x: pass));
        // The front starts one wave-width off the left edge and exits one off the right, so the sweep visibly
        // enters and leaves instead of popping.
        var front = (-WaveWidth + ((1f + (2f * WaveWidth)) * fraction));
        var trail = (SweepTrailScale * (seconds / ((float)SweepSeconds)));

        for (var index = 0; (index < m_frame.Length); index++) {
            var distance = MathF.Abs(x: (m_lampX[index] - front));
            var crest = Math.Clamp(value: (1f - (distance / WaveWidth)), min: 0f, max: 1f);

            // Squared falloff keeps the crest tight; the trail keeps passed keys faintly lit.
            m_frame[index] = m_tint.Scale(scale: MathF.Max(x: (crest * crest), y: trail));
        }
    }

    // Phase 2: the whole board breathes in the tier color.
    private void ComposeGlow(float seconds) {
        var brightness = (0.45f + (0.35f * MathF.Sin(x: ((MathF.Tau * seconds) / ((float)GlowPulseSeconds)))));
        var color = m_tint.Scale(scale: brightness);

        Array.Fill(array: m_frame, value: color);
    }

    // Phase 3: fade to black; the caller's legend repaints from a clean board.
    private void ComposeFade(float seconds) {
        var brightness = (0.45f * (1f - Math.Clamp(value: (seconds / ((float)FadeSeconds)), min: 0f, max: 1f)));
        var color = m_tint.Scale(scale: brightness);

        Array.Fill(array: m_frame, value: color);
    }
    private static LampColor TintFor(int referenceScore, int score) {
        var ratio = (score / ((double)Math.Max(val1: referenceScore, val2: 1)));

        return ratio switch {
            >= 0.98 => LampColor.Rgb(red: 40, green: 255, blue: 90),
            >= 0.90 => LampColor.Rgb(red: 255, green: 170, blue: 30),
            _ => LampColor.Rgb(red: 255, green: 60, blue: 25),
        };
    }
}
