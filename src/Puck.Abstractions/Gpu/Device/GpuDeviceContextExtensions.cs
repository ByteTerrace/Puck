namespace Puck.Abstractions.Gpu;

/// <summary>Convenience helpers over <see cref="IGpuDeviceContext"/>.</summary>
public static class GpuDeviceContextExtensions {
    /// <summary>Drains the device, tolerating an already-LOST or already-DISPOSED device: a wait-for-idle then
    /// raises <see cref="DeviceLostException"/> or <see cref="ObjectDisposedException"/>, which during TEARDOWN just
    /// means "there is nothing left to drain" (the DI container may dispose the context before a late GPU-resource
    /// owner drains through it). For use in Dispose / device-loss paths only — the frame loop calls the throwing
    /// <see cref="IGpuDeviceContext.WaitIdle"/> directly so a genuine loss surfaces and triggers recovery. A
    /// <see langword="null"/> context is a no-op.</summary>
    /// <param name="deviceContext">The device context to drain, or <see langword="null"/>.</param>
    public static void TryWaitIdle(this IGpuDeviceContext? deviceContext) {
        if (deviceContext is null) {
            return;
        }

        try {
            deviceContext.WaitIdle();
        } catch (Exception exception) when (exception is DeviceLostException or ObjectDisposedException) {
            // The device is already lost or torn down; nothing in flight will ever complete, so nothing to drain.
        }
    }
}
