namespace Puck.Abstractions.Gpu;

/// <summary>Convenience helpers over <see cref="IGpuDeviceContext"/>.</summary>
public static class GpuDeviceContextExtensions {
    /// <summary>Drains the device, tolerating an already-LOST device: a wait-for-idle on a lost device raises
    /// <see cref="DeviceLostException"/>, which during TEARDOWN just means "there is nothing left to drain". For use in
    /// Dispose / device-loss paths only — the frame loop calls the throwing <see cref="IGpuDeviceContext.WaitIdle"/>
    /// directly so a genuine loss surfaces and triggers recovery. A <see langword="null"/> context is a no-op.</summary>
    /// <param name="deviceContext">The device context to drain, or <see langword="null"/>.</param>
    public static void TryWaitIdle(this IGpuDeviceContext? deviceContext) {
        if (deviceContext is null) {
            return;
        }

        try {
            deviceContext.WaitIdle();
        } catch (DeviceLostException) {
            // The device is already lost; nothing in flight will ever complete, so there is nothing to drain.
        }
    }
}
