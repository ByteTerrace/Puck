namespace Puck.Platform.Switch;

/// <summary>The Nintendo Switch native-window seam, quarantined here as prior art — no live
/// <c>Puck.Platform.*</c> project registers a window backend for <c>NativeDisplayKind.Vi</c>. The
/// <c>nn::vi</c> / NVN APIs that open a VI layer and hand back an <c>nn::vi::NativeWindowHandle</c> live
/// behind Nintendo's NDA SDK, so the concrete implementation (a P/Invoke shim over the licensed SDK) shipped
/// only in the licensed Switch build. The matching Vulkan path (<c>vkCreateViSurfaceNN</c>) is still
/// implemented in <c>Puck.Vulkan</c> and consumes <c>NativeWindowHandle</c> via a
/// <c>ViNativeSurfaceBinding</c> — that half is open-source and did not move here.</summary>
public interface ISwitchViWindowBackend : IDisposable {
    /// <summary>The current VI layer height in pixels.</summary>
    uint Height { get; }

    /// <summary>True until the layer is closed.</summary>
    bool IsOpen { get; }

    /// <summary>The <c>nn::vi::NativeWindowHandle</c> (an <c>nvnWindow*</c> / native window
    /// pointer) used to create the Vulkan VI surface.</summary>
    nint NativeWindowHandle { get; }

    /// <summary>The current VI layer width in pixels.</summary>
    uint Width { get; }

    /// <summary>Closes the VI layer.</summary>
    void Close();

    /// <summary>Pumps any pending VI/system events (e.g. operation-mode/resolution change,
    /// focus, requested exit).</summary>
    void Poll();

    /// <summary>Makes the VI layer visible / brings it to the foreground.</summary>
    void Show();
}
