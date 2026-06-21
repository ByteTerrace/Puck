using Puck.Commands;
using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// A connected controller from any acquisition transport (HID stream or XInput poll), presenting the uniform
/// surface the manager drives: a stable identity, a player slot, a coalescer the frame thread drains, an
/// output handle, and a fault flag for pruning. The transport owns how the coalescer is fed.
/// </summary>
public interface IGamepadConnection : IDisposable
{
    /// <summary>The device's stable, content-addressed identity.</summary>
    InputDeviceId DeviceId { get; }

    /// <summary>The zero-based player slot assigned to this device.</summary>
    int PlayerIndex { get; }

    /// <summary>Whether the connection has stopped due to a disconnect or I/O error (eligible for pruning).</summary>
    bool IsFaulted { get; }

    /// <summary>The coalescer the frame thread drains each frame.</summary>
    GamepadCoalescer Coalescer { get; }

    /// <summary>The output (haptics) handle for this device.</summary>
    IGamepadOutput Output { get; }

    /// <summary>A transport-unique key used to avoid reopening an already-tracked device (e.g. HID path).</summary>
    string Key { get; }

    /// <summary>The controller family.</summary>
    GamepadType Type { get; }

    /// <summary>The optional input features this device provides (gyro, analog triggers).</summary>
    GamepadInputCapabilities InputCapabilities { get; }

    /// <summary>Starts any per-connection work (a HID device starts its read loop; XInput devices are driven externally).</summary>
    void Start();
}
