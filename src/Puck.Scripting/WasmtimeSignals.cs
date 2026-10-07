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
/// accessor and the native setter is taken from the wasmtime library loaded for the binding assembly (the binding's
/// own library while no import resolver is registered for it); a binding that renames either fails
/// every engine's construction rather than leaving the handlers in.</summary>
internal static class WasmtimeSignals {
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NativeHandle")]
    [return: UnsafeAccessorType("Wasmtime.Config+Handle, Wasmtime.Dotnet")]
    private static extern object NativeHandle(Config config);
    // The native library loaded by name in the binding assembly's context, with the binding's own search paths.
    // NativeLibrary.Load bypasses a DllImportResolver, so this is the library the binding loads only while no resolver
    // is registered for the binding assembly, which nothing in Puck does.
    private static unsafe delegate* unmanaged<nint, byte, void> SignalsBasedTrapsSet() => ((delegate* unmanaged<nint, byte, void>)NativeLibrary.GetExport(
        handle: NativeLibrary.Load(
            assembly: typeof(Config).Assembly,
            libraryName: "wasmtime",
            searchPath: null
        ),
        name: "wasmtime_config_signals_based_traps_set"
    ));

    /// <summary>Turns signal-based traps off on a config that has not yet built its engine.</summary>
    /// <param name="config">The config.</param>
    /// <returns>The same config.</returns>
    public static Config WithoutSignalHandlers(Config config) {
        var handle = ((SafeHandle)NativeHandle(config: config));
        var added = false;

        try {
            handle.DangerousAddRef(success: ref added);
            unsafe {
                SignalsBasedTrapsSet()(handle.DangerousGetHandle(), 0);
            }
        } finally {
            if (added) {
                handle.DangerousRelease();
            }
        }

        return config;
    }
}
