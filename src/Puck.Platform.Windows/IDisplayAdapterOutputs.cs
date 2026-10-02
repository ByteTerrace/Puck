namespace Puck.Platform.Windows;

/// <summary>One DXGI factory's enumeration of the system's adapter outputs, the seam a
/// <see cref="Win32DisplayColorSpaceProbe"/> reads displays through.</summary>
public interface IDisplayAdapterOutputs : IDisposable {
    /// <summary>Gets whether the display configuration the enumeration was taken from is still the system's
    /// (<c>IDXGIFactory1::IsCurrent</c>). A display change, an HDR toggle among them, makes it stale, and only a new
    /// factory reports the new configuration.</summary>
    bool IsCurrent { get; }

    /// <summary>Finds the output that drives a monitor, enumerating every adapter and output.</summary>
    /// <param name="monitorHandle">The monitor.</param>
    /// <returns>The output, owned by the caller, or null when no output drives the monitor or enumeration fails.</returns>
    IDisplayAdapterOutput? FindOutput(nint monitorHandle);
}
