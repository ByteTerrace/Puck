using Windows.Win32.Foundation;

namespace Puck.DirectX.Interop;

/// <summary>
/// Extension methods on the native <c>HRESULT</c> for interpreting and acting on DirectX result codes.
/// </summary>
public static class HResultExtensions {
    /// <summary>Throws a <see cref="DirectXException"/> if a result code indicates failure (a negative value).</summary>
    /// <param name="result">The result code to check.</param>
    /// <param name="operation">The name of the operation that produced the result, included in the exception message.</param>
    /// <exception cref="DirectXException"><paramref name="result"/> is a failure code.</exception>
    public static void ThrowIfFailed(this HRESULT result, string operation) {
        if (result.Failed) {
            throw new DirectXException(
                operation: operation,
                result: result.Value
            );
        }
    }
}
