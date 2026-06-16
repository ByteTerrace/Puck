using System.Runtime.InteropServices;

namespace Puck.Platform.Linux.Interop;

/// <summary>Bindings for the few libc entry points the Linux backends need. libxcb returns
/// events and replies allocated with malloc, which must be released with libc free.</summary>
internal static partial class Libc {
    [LibraryImport("libc")]
    public static partial void free(nint pointer);
}
