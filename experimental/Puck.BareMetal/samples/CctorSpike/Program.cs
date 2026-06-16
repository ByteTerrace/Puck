// Puck.BareMetal — lazy static-constructor spike.
//
// A type with an explicit static .cctor whose work the ILC cannot fold away (it calls a
// runtime-only method) gets a real .cctor that must run LAZILY on first static access, exactly
// once. Because the type also has a reference-typed (GC) static, that access routes through the
// GC ClassConstructorRunner variant (compat/ClassConstructorRunner.Compat.cs).
//
// Laziness is observed via a separate, cctor-less Probe type: LazyState's .cctor sets a flag on
// Probe, so the flag is false until LazyState is first touched and true afterwards.
//
// Exit code: 0 on success; a non-zero code identifies which check failed.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// No .cctor of its own (plain zero-init statics), so reading these does not trigger any class
// construction — it just observes whether LazyState's .cctor has run.
internal static class Probe
{
    internal static bool CctorRan;
    internal static int Runs;
}

internal static class LazyState
{
    internal static byte[] Buffer; // reference-typed (GC) static -> exercises the GC variant

    static LazyState()
    {
        Probe.CctorRan = true;
        Probe.Runs++;
        Buffer = new byte[Program.RuntimeLength()]; // runtime size -> not preinitializable -> real lazy .cctor
    }
}

internal static unsafe class Program
{
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr hFile, byte* buffer, int numberOfBytesToWrite, int* numberOfBytesWritten, IntPtr overlapped);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern uint GetTickCount();

    // Opaque to the ILC's preinitialization interpreter (a P/Invoke), so any .cctor that calls
    // it stays a runtime .cctor instead of being folded into frozen data.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int RuntimeLength() => (int)(GetTickCount() & 7) + 1; // 1..8

    private static int Main()
    {
        // Lazy: LazyState's .cctor must not have run before we touch the type.
        if (Probe.CctorRan)
            return 1;

        byte[] buffer = LazyState.Buffer; // first access -> runs LazyState.cctor via the GC variant
        if (!Probe.CctorRan)
            return 2;
        if (buffer == null)
            return 3;
        if (buffer.Length < 1 || buffer.Length > 8)
            return 4;
        if (Probe.Runs != 1) // ran exactly once
            return 5;

        // Second access must not re-run the .cctor.
        byte[] again = LazyState.Buffer;
        if (Probe.Runs != 1)
            return 6;
        if (again == null)
            return 7;

        const string message = "Puck.BareMetal: lazy static .cctor runs once on first access (GC statics).\r\n";

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
