using Windows.Win32.Foundation;

namespace Puck.DirectX.Interop;

/// <summary>
/// Extension methods on the native <c>HRESULT</c> for interpreting and acting on DirectX result codes.
/// </summary>
public static class HResultExtensions {
    // DXGI device-removal HRESULTs. A removed/reset device is a RECOVERABLE signal, surfaced as the neutral
    // DeviceLostException so the host pump's device-loss recovery catches it uniformly (rather than a DirectXException).
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    /// <summary>Throws on a failing result: a <see cref="DeviceLostException"/> for a removed/reset device (recoverable),
    /// otherwise a <see cref="DirectXException"/>.</summary>
    /// <param name="result">The result code to check.</param>
    /// <param name="operation">The name of the operation that produced the result, included in the exception message.</param>
    /// <exception cref="DeviceLostException"><paramref name="result"/> is <c>DXGI_ERROR_DEVICE_REMOVED</c>/<c>_RESET</c>.</exception>
    /// <exception cref="DirectXException"><paramref name="result"/> is any other failure code.</exception>
    public static void ThrowIfFailed(this HRESULT result, string operation) {
        if (!result.Failed) {
            return;
        }

        if (
            (result.Value == DxgiErrorDeviceRemoved) ||
            (result.Value == DxgiErrorDeviceReset)
        ) {
            throw new DeviceLostException(
                message: $"{operation} failed: 0x{result.Value:X8} (graphics device removed/reset).",
                reasonCode: result.Value
            );
        }

        throw new DirectXException(
            operation: operation,
            result: result.Value
        );
    }
}
