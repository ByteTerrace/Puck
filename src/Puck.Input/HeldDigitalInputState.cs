using Puck.Commands;

namespace Puck.Input;

/// <summary>
/// Owns the physical held state for edge-reported window controls. Controls retain first-down order so a frame's
/// digital <see cref="CommandPhase.Active"/> reassertions rebuild ordered modifier chords exactly as pressed.
/// </summary>
/// <remarks>Window-pump-thread only. OS repeat does not reorder a control, and a control first pressed during the
/// current frame is omitted from that frame's reassertions because its real <see cref="CommandPhase.Started"/> edge
/// already carried the state.</remarks>
public sealed class HeldDigitalInputState {
    private readonly List<HeldControl> m_controls = [];

    private readonly record struct HeldControl(InputDeviceId Device, string Source, ulong PressedFrame);

    /// <summary>Gets the number of controls currently held.</summary>
    public int Count => m_controls.Count;

    /// <summary>Applies one mapped signal to the physical held set.</summary>
    /// <param name="signal">The mapped window signal.</param>
    /// <param name="frameKey">The current host-frame key.</param>
    public void Observe(in InputSignal signal, ulong frameKey) {
        // Text input is represented by a digital Started signal for command-shape compatibility, but it is a
        // completed payload rather than a physical control and has no matching release edge to clear a hold.
        if ((signal.Value.Kind != CommandValueKind.Digital) || (signal.Text is not null)) {
            return;
        }

        var index = IndexOf(device: signal.DeviceId, source: signal.Source);

        if (signal.Phase == CommandPhase.Started) {
            if (index < 0) {
                m_controls.Add(item: new HeldControl(Device: signal.DeviceId, Source: signal.Source, PressedFrame: frameKey));
            }
        } else if ((signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) && (index >= 0)) {
            m_controls.RemoveAt(index: index);
        }
    }

    /// <summary>Builds one held control's ordered digital reassertion unless it was first pressed this frame.</summary>
    /// <param name="index">The held-order index from 0 through <see cref="Count"/> minus one.</param>
    /// <param name="frameKey">The current host-frame key.</param>
    /// <param name="captureTick">The monotonic capture time to stamp.</param>
    /// <param name="signal">The reassertion when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a reassertion was produced.</returns>
    public bool TryReassert(int index, ulong frameKey, ulong captureTick, out InputSignal signal) {
        var control = m_controls[index];

        if (control.PressedFrame == frameKey) {
            signal = default;
            return false;
        }

        signal = InputSignal.Reassert(source: control.Source, deviceId: control.Device, captureTick: captureTick);
        return true;
    }

    /// <summary>Drops every physically held control, such as on OS focus loss.</summary>
    public void Clear() {
        m_controls.Clear();
    }

    private int IndexOf(InputDeviceId device, string source) {
        for (var index = 0; (index < m_controls.Count); index++) {
            var control = m_controls[index];

            if ((control.Device == device) && string.Equals(a: control.Source, b: source, comparisonType: StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }
}
