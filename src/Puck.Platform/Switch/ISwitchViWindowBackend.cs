namespace Puck.Platform.Switch;

/// <summary>The Nintendo Switch native-window seam. The <c>nn::vi</c> / NVN APIs that open a
/// VI layer and hand back an <c>nn::vi::NativeWindowHandle</c> live behind Nintendo's NDA
/// SDK, so the concrete implementation (a P/Invoke shim over the licensed SDK) ships only in
/// the licensed Switch build and registers itself in DI. With no implementation registered,
/// <see cref="NativeWindowFactory"/> reports the missing shim instead of opening a VI
/// window. The matching Vulkan path (<c>vkCreateViSurfaceNN</c>) is fully implemented and
/// consumes <see cref="NativeWindowHandle"/> via a <see cref="ViNativeSurfaceBinding"/>.</summary>
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
