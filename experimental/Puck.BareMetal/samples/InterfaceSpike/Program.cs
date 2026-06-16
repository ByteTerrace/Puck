// Puck.BareMetal — polymorphic interface-dispatch spike.
//
// Goal: prove that an interface with 2+ implementers, called THROUGH the interface, works on
// a freestanding puck build. Single-implementer interface calls already work because the
// JIT devirtualizes them; the moment a second type implements the interface the JIT must emit
// a real interface dispatch, which stock ILC lowers to RhpInitialDynamicInterfaceDispatch and
// a per-call dispatch cell resolved by compat/native/puck-rt.c and
// compat/native/interface-dispatch-x64.asm.
//
// To FORCE a real interface dispatch (and defeat devirtualization), each instance's concrete
// type is chosen behind a NoInlining factory, and one of them additionally depends on a
// runtime value the compiler cannot see through. The interface has TWO methods so dispatch is
// exercised at interface slot 0 AND slot 1 (the slot travels through the dispatch cell). Each
// (type, method) returns a unique value, so any mis-dispatch changes the checked result.
//
// Exit code: 0 on success; a non-zero code identifies which check failed. Also writes a
// one-line banner via WriteFile (same approach as the Hello sample).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal interface IFoo
{
    int Bar(); // interface slot 0
    int Baz(); // interface slot 1
}

internal sealed class FooA : IFoo
{
    public int Bar() => 0x11;
    public int Baz() => 0xA1;
}

internal sealed class FooB : IFoo
{
    public int Bar() => 0x22;
    public int Baz() => 0xB2;
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

    // NoInlining + a runtime-derived selector => the call sites below cannot know the concrete
    // type, so the JIT must emit a true interface dispatch instead of devirtualizing.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IFoo Make(bool which) => which ? new FooA() : new FooB();

    private static int Main()
    {
        IFoo a = Make(true);   // FooA
        IFoo b = Make(false);  // FooB

        // Slot 0 (Bar) across two implementers. If either dispatched to the wrong type the sum
        // changes (e.g. both -> FooA gives 0x22, both -> FooB gives 0x44).
        if (a.Bar() + b.Bar() != 0x11 + 0x22)
            return 1;

        // Slot 1 (Baz) across two implementers — exercises a non-zero interface slot.
        if (a.Baz() + b.Baz() != 0xA1 + 0xB2)
            return 2;

        // Exact per-(type, slot) mapping, not just a valid-looking sum.
        if (a.Bar() != 0x11 || a.Baz() != 0xA1)
            return 3;
        if (b.Bar() != 0x22 || b.Baz() != 0xB2)
            return 4;

        // Runtime-selected concrete type: GetTickCount() is opaque to the JIT.
        bool pickA = (GetTickCount() & 1u) != 0u;
        IFoo c = Make(pickA);
        int expectedBar = pickA ? 0x11 : 0x22;
        int expectedBaz = pickA ? 0xA1 : 0xB2;
        if (c.Bar() != expectedBar || c.Baz() != expectedBaz)
            return 5;

        const string message = "Puck.BareMetal: polymorphic interface dispatch works (2 types, 2 slots).\r\n";

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
