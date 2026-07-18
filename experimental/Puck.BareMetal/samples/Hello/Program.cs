// Part of Puck.BareMetal. Proof-of-compile boot/hello stub.
//
// Runs with NO .NET runtime: no GC, no BCL — only the Puck.Runtime core library (runtime/),
// whose object allocator is backed by mimalloc (mimalloc/, compat/native/mimalloc-glue.c).
// Compiled by NativeAOT's ILC with IlcSystemModule=Puck.Runtime and linked freestanding.
//
// The binary's OS entry is the native PuckStart (compat/native/puck-rt.c): it initializes GC
// statics, then calls the managed __managed__Main, which runs the entry point below. It
// exercises the mimalloc-backed heap from managed code:
//
//   * a large byte[] (a different mimalloc size class than a tiny object) is allocated,
//     checked to be zero-initialized (mi_zalloc contract), written with a pattern, and read
//     back — proving real allocation + data integrity through mimalloc;
//   * a second byte[] holds the banner, built from a string literal (a frozen object), which
//     is then written with one P/Invoke.
//
// `new byte[]` lowers to RhpNewArrayFast -> PuckAllocZeroed -> mi_process_init()/mi_zalloc.
// Output goes through WriteFile rather than Console (WriteConsoleW), because a boot host's
// stdout may be a pipe/file/serial, not a real console screen buffer.

using System;
using System.Runtime.InteropServices;

internal static unsafe class Program {
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr hFile, byte* buffer, int numberOfBytesToWrite, int* numberOfBytesWritten, IntPtr overlapped);
    private static int Main() {
        // Exercise mimalloc with a non-trivial allocation: zero-init check, then a
        // write/read round-trip over the whole buffer. Any failure returns non-zero.
        const int probeLength = (256 * 1024);
        byte[] probe = new byte[probeLength];

        for (int i = 0; (i < probeLength); i++)
            if (probe[i] != 0)
                return 1; // mi_zalloc must hand back zeroed storage

        for (int i = 0; (i < probeLength); i++)
            probe[i] = (byte)((i * 31) + 7);

        for (int i = 0; (i < probeLength); i++)
            if (probe[i] != (byte)((i * 31) + 7))
                return 2; // heap storage must survive read-back intact

        const string message = "Puck.BareMetal: hello from puck + mimalloc - NativeAOT, no GC, no .NET runtime.\r\n";

        int length = message.Length;
        byte[] line = new byte[length];

        for (int i = 0; (i < length); i++)
            line[i] = (byte)message[i];

        int written;

        fixed (byte* p = line)
            WriteFile(GetStdHandle(nStdHandle: STD_OUTPUT_HANDLE), p, length, &written, default);

        return 0;
    }
}
