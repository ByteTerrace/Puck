namespace Puck.Input.Hid;

/// <summary>
/// A transport-neutral handle to an opened HID device: report-length and identity metadata plus the async
/// read/write and feature-report operations the gamepad parsers need. The concrete implementation is supplied by
/// a platform backend (e.g. the Windows <c>Win32</c> transport in <c>Puck.Platform</c>); nothing in
/// <c>Puck.Input</c> binds to a specific OS.
/// </summary>
public interface IHidDevice : IDisposable {
    /// <summary>The device interface path that opened this device (a transport-unique identity).</summary>
    string DevicePath { get; }

    /// <summary>The USB vendor id.</summary>
    ushort VendorId { get; }

    /// <summary>The USB product id.</summary>
    ushort ProductId { get; }

    /// <summary>The HID usage page of the device's top-level collection (e.g. <c>0x01</c> Generic Desktop).</summary>
    ushort UsagePage { get; }

    /// <summary>The HID usage of the device's top-level collection (e.g. <c>0x05</c> gamepad, <c>0x04</c> joystick).</summary>
    ushort Usage { get; }

    /// <summary>The transport (USB / Bluetooth) this device is connected over, inferred from its path.</summary>
    HidTransport Transport { get; }

    /// <summary>The declared input report length in bytes (zero if the device does not declare one).</summary>
    int InputReportByteLength { get; }

    /// <summary>The declared output report length in bytes (zero if the device does not declare one).</summary>
    int OutputReportByteLength { get; }

    /// <summary>The declared feature report length in bytes (zero if the device does not declare one).</summary>
    int FeatureReportByteLength { get; }

    /// <summary>
    /// Reads the next input report, awaiting until a report arrives or the token is cancelled (no internal
    /// timeout). The steady-state path for a continuously streaming device.
    /// </summary>
    /// <param name="buffer">The buffer that receives the report.</param>
    /// <param name="cancellationToken">A token that cancels the pending read.</param>
    /// <returns>The number of bytes read, or zero if the device has no open stream.</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the next input report, bounding the wait by <paramref name="timeoutInMilliseconds"/>. Returns zero
    /// on timeout unless <paramref name="throwOnTimeout"/> is set.
    /// </summary>
    /// <param name="buffer">The buffer that receives the report.</param>
    /// <param name="throwOnTimeout">When <see langword="true"/>, a timeout throws instead of returning zero.</param>
    /// <param name="timeoutInMilliseconds">The maximum time to wait for a report.</param>
    /// <param name="cancellationToken">A token that cancels the pending read.</param>
    /// <returns>The number of bytes read, or zero on timeout (or if the device has no open stream).</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, bool throwOnTimeout = false, int timeoutInMilliseconds = 120, CancellationToken cancellationToken = default);

    /// <summary>Writes an output report to the device.</summary>
    /// <param name="buffer">The report payload, including the leading report id byte.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the report has been written.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a HID feature report (a synchronous control transfer, for one-time setup such as reading
    /// calibration data). The caller sets <c>buffer[0]</c> to the requested report id before calling.
    /// </summary>
    /// <param name="buffer">The buffer whose first byte is the requested report id and which receives the report.</param>
    /// <returns><see langword="true"/> if the report was read; otherwise <see langword="false"/>.</returns>
    bool TryGetFeatureReport(Span<byte> buffer);
}
