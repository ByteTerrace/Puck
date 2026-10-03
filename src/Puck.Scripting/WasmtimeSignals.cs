using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Wasmtime;

namespace Puck.Scripting;

/// <summary>Keeps Wasmtime from installing signal handlers in the host process. By default Wasmtime traps a guest's
/// out-of-bounds access, division fault and stack exhaustion through handlers for SIGSEGV, SIGILL, SIGFPE and SIGBUS
/// that it installs for the whole process, running on an alternate signal stack. A .NET hardware exception raised
/// afterwards on a thread that never ran a guest (an integer division fault in managed code, say) then reaches the
/// runtime's own handler on that thread's small alternate stack, and the runtime's exception dispatch overflows it:
/// the process aborts with "Stack overflow." Addons share the World process with all of its managed code, so the
/// engine turns signal-based traps off, and Wasmtime checks memory bounds, division and stack depth explicitly in
/// generated code instead. Traps keep their kinds; guest code pays an explicit bounds check on each memory access.
/// The binding exposes no setter for the native option, so the config's handle is reached through its internal
/// accessor; a binding that renames it fails every engine's construction rather than leaving the handlers in.</summary>
internal static partial class WasmtimeSignals {
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NativeHandle")]
    [return: UnsafeAccessorType("Wasmtime.Config+Handle, Wasmtime.Dotnet")]
    private static extern object NativeHandle(Config config);
    [LibraryImport("wasmtime")]
    private static partial void wasmtime_config_signals_based_traps_set(nint config, [MarshalAs(UnmanagedType.U1)] bool enable);

    /// <summary>Turns signal-based traps off on a config that has not yet built its engine.</summary>
    /// <param name="config">The config.</param>
    /// <returns>The same config.</returns>
    public static Config WithoutSignalHandlers(Config config) {
        var handle = ((SafeHandle)NativeHandle(config: config));
        var added = false;

        try {
            handle.DangerousAddRef(success: ref added);
            wasmtime_config_signals_based_traps_set(
                config: handle.DangerousGetHandle(),
                enable: false
            );
        } finally {
            if (added) {
                handle.DangerousRelease();
            }
        }

        return config;
    }
}
