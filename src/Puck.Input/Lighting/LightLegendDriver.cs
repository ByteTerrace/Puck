using Puck.Abstractions.Lighting;

namespace Puck.Input.Lighting;

/// <summary>
/// Drives a <see cref="LightLegendComposer"/> onto one <see cref="ILampArrayDevice"/> at a throttled cadence.
/// Lighting writes are HID feature reports, so the driver composes at most once per update interval (defaulting
/// to 30 Hz, never faster than the device's own minimum interval) and writes only the lamps whose color actually
/// changed since the last write — a steady legend costs nothing on the wire. It takes host control of the device
/// on first tick and restores autonomous mode on <see cref="Dispose"/>.
/// </summary>
public sealed class LightLegendDriver : IDisposable {
    private readonly LightLegendComposer m_composer;
    private readonly ILampArrayDevice m_device;
    private readonly double m_flashDecaySeconds;
    private readonly double m_updateIntervalSeconds;
    private readonly LampColor[] m_composed;
    private readonly LampColor[] m_written;
    private readonly int[] m_dirtyIds;
    private readonly LampColor[] m_dirtyColors;
    private double m_accumulator;
    private bool m_hasWritten;
    private bool m_isDisposed;
    private bool m_isStarted;

    /// <summary>Initializes a new driver over a device.</summary>
    /// <param name="device">The lamp array to drive.</param>
    /// <param name="composer">The composer to paint with; defaults to a fresh composer over the default palette.</param>
    /// <param name="updateHz">The maximum write cadence in hertz; clamped by the device's minimum update interval. Defaults to 30.</param>
    /// <param name="flashDecaySeconds">How long an activation flash takes to fully decay. Defaults to 0.35 s.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public LightLegendDriver(
        ILampArrayDevice device,
        LightLegendComposer? composer = null,
        double updateHz = 30.0,
        double flashDecaySeconds = 0.35
    ) {
        ArgumentNullException.ThrowIfNull(device);

        m_device = device;
        m_composer = (composer ?? new LightLegendComposer());
        m_flashDecaySeconds = ((flashDecaySeconds <= 0.0) ? 0.0001 : flashDecaySeconds);

        var requested = ((updateHz <= 0.0) ? (1.0 / 30.0) : (1.0 / updateHz));
        var deviceFloor = (device.MinUpdateIntervalInMilliseconds / 1000.0);

        m_updateIntervalSeconds = Math.Max(val1: requested, val2: deviceFloor);

        var count = Math.Max(val1: device.LampCount, val2: 1);

        m_composed = new LampColor[count];
        m_written = new LampColor[count];
        m_dirtyIds = new int[count];
        m_dirtyColors = new LampColor[count];
    }

    /// <summary>Gets the composer this driver paints with (for palette access or a flash reset).</summary>
    public LightLegendComposer Composer => m_composer;

    /// <summary>Gets the device this driver drives.</summary>
    public ILampArrayDevice Device => m_device;

    /// <summary>
    /// Forgets the last written colors, forcing the next composed frame to repaint every lamp. Call after
    /// something else wrote to the device directly (e.g. a <see cref="LightCelebration"/>) so the legend
    /// restores completely instead of trusting a stale dirty-cache.
    /// </summary>
    public void MarkDirty() {
        m_hasWritten = false;
    }

    /// <summary>Takes host control of the device (clears autonomous mode) so composed colors take effect.</summary>
    public void Start() {
        if (m_isStarted || m_isDisposed) {
            return;
        }

        m_isStarted = true;
        _ = m_device.TrySetAutonomousMode(enabled: false);
    }

    /// <summary>
    /// Advances the driver by <paramref name="elapsedSeconds"/>. Composes and writes at most one frame per update
    /// interval; sub-interval calls just accumulate time. Takes host control on the first call.
    /// </summary>
    /// <param name="state">This tick's legend.</param>
    /// <param name="elapsedSeconds">The wall/render time since the previous call, in seconds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    public void Tick(LightLegendState state, double elapsedSeconds) {
        ArgumentNullException.ThrowIfNull(state);

        if (m_isDisposed) {
            return;
        }

        Start();

        m_accumulator += Math.Max(val1: 0.0, val2: elapsedSeconds);

        if ((m_accumulator < m_updateIntervalSeconds) && m_hasWritten) {
            return;
        }

        var sinceLastCompose = m_accumulator;

        m_accumulator = 0.0;

        var flashDecay = ((float)(sinceLastCompose / m_flashDecaySeconds));

        m_composer.Compose(
            destination: m_composed,
            device: m_device,
            flashDecay: flashDecay,
            state: state
        );

        FlushDirty();
    }

    // Writes only the lamps whose composed color changed since the last write (all of them on the first write).
    private void FlushDirty() {
        var count = m_device.LampCount;
        var dirty = 0;

        for (var index = 0; (index < count); index++) {
            var color = m_composed[index];

            if (!m_hasWritten || (m_written[index] != color)) {
                m_dirtyIds[dirty] = index;
                m_dirtyColors[dirty] = color;
                m_written[index] = color;
                dirty++;
            }
        }

        if (dirty == 0) {
            return;
        }

        m_device.UpdateLamps(
            colors: m_dirtyColors.AsSpan(start: 0, length: dirty),
            lampIds: m_dirtyIds.AsSpan(start: 0, length: dirty)
        );

        m_hasWritten = true;
    }

    /// <summary>Restores the device to autonomous mode and stops driving it.</summary>
    public void Dispose() {
        if (m_isDisposed) {
            return;
        }

        m_isDisposed = true;

        if (m_isStarted) {
            _ = m_device.TrySetAutonomousMode(enabled: true);
        }
    }
}
