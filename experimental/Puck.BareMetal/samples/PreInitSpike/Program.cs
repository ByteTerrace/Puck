// Puck.BareMetal — frozen constant GC-static spike.
//
// A reference-typed static initialized to a compile-time-constant value (the common "static
// table of data" shape). The stock ILC PREINITIALIZES it and freezes the result into the image:
// the array and the static's base object both live in the frozen segment, so no runtime spine is
// allocated and no .cctor runs — the static is simply read. This verifies that frozen
// reference-typed statics read back their real values on the freestanding puck build.
//
// (Note: this does NOT exercise the runtime HASPREINIT copy in compat/native/puck-rt.c — in
// this toolchain config the ILC fully freezes constant statics and runs any non-foldable .cctor
// entirely at runtime, so HASPREINIT is not emitted. See CctorSpike for the lazy runtime-.cctor
// path. The HASPREINIT copy remains as a documented safety-net in puck-rt.c.)
//
// Exit code: 0 on success; a non-zero code identifies which check failed.

using System;
using System.Runtime.InteropServices;

internal static class Config
{
    internal static readonly int[] Values = { 0x11, 0x22, 0x33 };
}

internal static unsafe class Program
{
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr hFile, byte* buffer, int numberOfBytesToWrite, int* numberOfBytesWritten, IntPtr overlapped);

    private static int Main()
    {
        int[] values = Config.Values;

        if (values == null)
            return 1;

        if (values.Length != 3)
            return 2;

        int sum = 0;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];
        if (sum != 0x11 + 0x22 + 0x33)
            return 3;

        if (values[0] != 0x11 || values[1] != 0x22 || values[2] != 0x33)
            return 4;

        const string message = "Puck.BareMetal: frozen constant GC static reads correctly (sum=0x66).\r\n";

        int length = message.Length;
        byte[] line = new byte[length];
        for (int i = 0; i < length; i++)
            line[i] = (byte)message[i];

        int written;
        fixed (byte* p = line)
            WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), p, length, &written, default);

        return 0;
    }
}
