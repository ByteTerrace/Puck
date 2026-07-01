using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Thrown by a presenter (or surfaced from a render node's GPU work) when the graphics device has been LOST and the
/// current frame cannot complete — DirectX <c>DXGI_ERROR_DEVICE_REMOVED</c>/<c>DEVICE_RESET</c> or Vulkan
/// <c>VK_ERROR_DEVICE_LOST</c>/<c>VK_ERROR_SURFACE_LOST_KHR</c>. It is a RECOVERABLE signal, distinct from an ordinary
/// failure: the host pump catches it, asks an <see cref="IDeviceLostRecoverable"/> presenter to rebuild the device and
/// its resources, tells the render tree to release device-derived resources (each node implements Puck.Hosting's
/// <c>IRenderNode.OnDeviceLost</c>; no callback registration is involved), and resumes — without disturbing the
/// fixed-step simulation. A backend raises this INSTEAD of its
/// native exception once it has classified the result as a device loss.
/// </summary>
public sealed class DeviceLostException : Exception {
    /// <summary>A backend-specific reason code (e.g. the DXGI device-removed reason HRESULT, or the VkResult), for
    /// diagnostics; <c>0</c> when none was available.</summary>
    public long ReasonCode { get; }

    /// <summary>Initializes a new instance of the <see cref="DeviceLostException"/> class.</summary>
    /// <param name="message">A description of where and how the device loss was observed.</param>
    /// <param name="reasonCode">A backend-specific reason code for diagnostics, or <c>0</c> when unavailable.</param>
    /// <param name="innerException">The native exception that surfaced the loss, if any.</param>
    public DeviceLostException(string message, long reasonCode = 0, Exception? innerException = null)
        : base(message: message, innerException: innerException) {
        ReasonCode = reasonCode;
    }
}
