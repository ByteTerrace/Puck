using Puck.DirectX.Apis;
using Windows.Win32.Foundation;

namespace Puck.DirectX.Interop;

/// <summary>
/// Extension methods on the native <c>HRESULT</c> for interpreting and acting on DirectX result codes.
/// </summary>
public static class HResultExtensions {
    // DXGI device-removal HRESULTs. A removed, reset or hung device is a recoverable signal, surfaced as the neutral
    // DeviceLostException so the host's device-loss recovery catches it rather than a DirectXException.
    private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    /// <summary>Returns whether a result reports a removed device: <c>DXGI_ERROR_DEVICE_REMOVED</c>, <c>_RESET</c> or
    /// <c>_HUNG</c>.</summary>
    /// <param name="result">The result code to classify.</param>
    /// <returns><see langword="true"/> when the device the call ran on was removed.</returns>
    public static bool IsDeviceRemoval(HRESULT result) =>
        (result.Value is DxgiErrorDeviceRemoved or DxgiErrorDeviceReset or DxgiErrorDeviceHung);
    /// <summary>Throws on a failing result: a <see cref="DeviceLostException"/> for a removed device (recoverable),
    /// carrying the result itself as its reason, otherwise a <see cref="DirectXException"/>. A call made on a device
    /// passes the device's calls instead, so the loss carries the device's own removal reason.</summary>
    /// <param name="result">The result code to check.</param>
    /// <param name="operation">The name of the operation that produced the result, included in the exception message.</param>
    /// <exception cref="DeviceLostException"><paramref name="result"/> reports a removed device.</exception>
    /// <exception cref="DirectXException"><paramref name="result"/> is any other failure code.</exception>
    public static void ThrowIfFailed(this HRESULT result, string operation) {
        if (!result.Failed) {
            return;
        }

        if (IsDeviceRemoval(result: result)) {
            throw new DeviceLostException(
                message: $"{operation} failed: 0x{result.Value:X8} (graphics device removed).",
                reasonCode: result.Value
            );
        }

        throw new DirectXException(
            operation: operation,
            result: result.Value
        );
    }
    /// <summary>Throws on a failing result of a call made on a device: a <see cref="DeviceLostException"/> for a removed
    /// device, whose <see cref="DeviceLostException.ReasonCode"/> is the device's
    /// <see cref="IDirectXCommandCalls.DeviceRemovedReason"/> (the result itself when the device reports none),
    /// otherwise a <see cref="DirectXException"/>.</summary>
    /// <typeparam name="TCalls">The device's calls' answerer.</typeparam>
    /// <param name="result">The result code to check.</param>
    /// <param name="calls">The calls of the device the result came from, which read its removal reason.</param>
    /// <param name="operation">The name of the operation that produced the result, included in the exception message.</param>
    /// <exception cref="DeviceLostException"><paramref name="result"/> reports a removed device.</exception>
    /// <exception cref="DirectXException"><paramref name="result"/> is any other failure code.</exception>
    public static void ThrowIfFailed<TCalls>(this HRESULT result, TCalls calls, string operation) where TCalls : IDirectXCommandCalls {
        if (!result.Failed) {
            return;
        }

        if (!IsDeviceRemoval(result: result)) {
            throw new DirectXException(
                operation: operation,
                result: result.Value
            );
        }

        var reason = calls.DeviceRemovedReason();
        var reasonCode = (reason.Failed
            ? reason.Value
            : result.Value
        );

        throw new DeviceLostException(
            message: $"{operation} failed: 0x{result.Value:X8}; the graphics device was removed (reason 0x{reasonCode:X8}).",
            reasonCode: reasonCode
        );
    }
}
