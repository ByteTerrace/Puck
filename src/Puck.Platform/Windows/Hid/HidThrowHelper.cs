using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Puck.Platform.Windows.Hid;

internal static class HidThrowHelper {
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static Win32Exception CreateWin32Exception(
        string source,
        int? errorCode = default
    ) {
        if (!errorCode.HasValue) {
            errorCode = Marshal.GetLastPInvokeError();
        }

        return new Win32Exception(message: $"Unhandled exception from {source}: [{errorCode}] \"{Marshal.GetPInvokeErrorMessage(error: errorCode.Value)}\".");
    }

    [DoesNotReturn]
    internal static void ThrowLastWin32Exception(string source, WIN32_ERROR? errorCode = default) {
        throw CreateWin32Exception(
            errorCode: ((int?)errorCode),
            source: source
        );
    }
}
