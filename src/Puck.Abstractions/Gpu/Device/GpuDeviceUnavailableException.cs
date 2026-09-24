namespace Puck.Abstractions.Gpu;

/// <summary>
/// Raised by a graphics backend's device bring-up when this host has no usable device for it: no loader or driver, no
/// adapter that satisfies the backend's requirements, or a device creation the driver refused. The backend classifies
/// the failure where it happens and raises this INSTEAD of its native exception, which rides along as the inner
/// exception, so nothing above the backends names a backend's exception types to tell an environment that cannot run
/// the backend apart from an ordinary failure. Its message reads <c>&lt;backend&gt; device unavailable: &lt;reason&gt;</c>.
/// A device that was running and then disappeared is <see cref="DeviceLostException"/> instead.
/// </summary>
public sealed class GpuDeviceUnavailableException : HostResourceUnavailableException {
    /// <summary>Initializes a new instance of the <see cref="GpuDeviceUnavailableException"/> class.</summary>
    /// <param name="backend">The backend token, <c>vulkan</c> or <c>directx</c>.</param>
    /// <param name="reason">What the bring-up found missing or refused, in one line.</param>
    /// <param name="innerException">The native failure the backend classified, if any.</param>
    /// <exception cref="ArgumentException"><paramref name="backend"/> or <paramref name="reason"/> is <see langword="null"/>, empty, or white space.</exception>
    public GpuDeviceUnavailableException(string backend, string reason, Exception? innerException = null)
        : base(
        innerException: innerException,
        reason: reason,
        resource: $"{backend} device"
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        Backend = backend;
    }

    /// <summary>Gets the backend token whose device could not be brought up.</summary>
    public string Backend { get; }
}
