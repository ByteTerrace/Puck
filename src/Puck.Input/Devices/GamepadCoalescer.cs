using System.Numerics;

namespace Puck.Input.Devices;

/// <summary>
/// Bridges a device's high-rate I/O loop to the per-frame consumer. The I/O loop calls <see cref="Update"/>
/// as reports arrive; the frame thread calls <see cref="Drain"/> once per frame. Continuous axes keep their
/// latest value, button press/release edges are accumulated so taps falling entirely between two frames are
/// never lost, and gyro samples are averaged so the reported angular velocity is frame-rate independent. All
/// access is guarded so the two threads never tear a read.
/// </summary>
public sealed class GamepadCoalescer
{
    private readonly object m_gate = new();
    private Vector3 m_gyro;
    private int m_gyroSamples;
    private bool m_hasSample;
    private GamepadState m_latest = GamepadState.Neutral;
    private GamepadButtons m_pressed;
    private GamepadButtons m_released;
    private GamepadButtons m_previousButtons;

    /// <summary>Records a freshly parsed report (called on the device I/O loop).</summary>
    /// <param name="state">The normalized state decoded from the report.</param>
    public void Update(in GamepadState state) {
        lock (m_gate) {
            // Prime the button baseline on the very first report so buttons already held at connect time do not
            // register as spurious edges; only diff against a real prior state thereafter. Both press and
            // release edges are accumulated — a release is inert by default at the binding
            // (CommandBinding.ActivateOn ignores Completed), so it updates held state without re-firing a
            // press-driven handler.
            if (m_hasSample) {
                m_pressed |= (state.Buttons & ~m_previousButtons);
                m_released |= (~state.Buttons & m_previousButtons);
            }

            m_previousButtons = state.Buttons;
            m_gyro += state.Gyro;
            ++m_gyroSamples;
            m_latest = state;
            m_hasSample = true;
        }
    }

    /// <summary>Takes this frame's coalesced view and resets the transient edge/motion accumulators.</summary>
    /// <param name="latest">The most recent normalized state.</param>
    /// <param name="pressed">Buttons that transitioned to pressed since the last drain.</param>
    /// <param name="released">Buttons that transitioned to released since the last drain.</param>
    /// <param name="gyro">
    /// The mean gyro angular velocity (radians/second) over the reports seen since the last drain. Averaging
    /// (rather than summing) keeps the value a true angular velocity, independent of how many reports happened
    /// to land in the frame.
    /// </param>
    /// <returns><see langword="true"/> once at least one report has been seen; otherwise <see langword="false"/>.</returns>
    public bool Drain(
        out GamepadState latest,
        out GamepadButtons pressed,
        out GamepadButtons released,
        out Vector3 gyro
    ) {
        lock (m_gate) {
            latest = m_latest;
            pressed = m_pressed;
            released = m_released;
            gyro = ((m_gyroSamples > 0)
                ? (m_gyro / m_gyroSamples)
                : Vector3.Zero);

            m_gyro = Vector3.Zero;
            m_gyroSamples = 0;
            m_pressed = GamepadButtons.None;
            m_released = GamepadButtons.None;

            return m_hasSample;
        }
    }
}
