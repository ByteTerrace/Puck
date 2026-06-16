// Puck.BareMetal — raw T[] as a first-class IEnumerable<T> / IReadOnlyList<T>.
//
// Demonstrates the "powerful primitive": a single mimalloc-backed array that is ALSO a
// first-class generic collection — no wrapper object. A consumer depends on
// IEnumerable<IService> (and IReadOnlyList<IService>) and is handed a raw IService[].
//
// What makes this work:
//   * the collection interface family lives in the corelib (compat/Collections.Compat.cs), so
//     Roslyn grants the built-in T[] -> IEnumerable<T> conversion;
//   * the array type implements them (compat/Array.Compat.cs: Array<T> + SZGenericArrayEnumerator<T>),
//     so the stock ILC emits the array's interface dispatch map (into the array's SEALED vtable,
//     via the shared Array<T> methods that recover T from `this`);
//   * the freestanding interface-dispatch resolver follows that sealed vtable
//     (compat/native/puck-rt.c + interface-dispatch-x64.asm).
//
// Exit code: 0 on success; a non-zero code identifies which check failed.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal interface IService
{
    int Id();
}

internal sealed class ServiceA : IService
{
    public int Id() => 1;
}

internal sealed class ServiceB : IService
{
    public int Id() => 2;
}

internal sealed class ServiceC : IService
{
    public int Id() => 4;
}

internal static unsafe class Program
{
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr hFile, byte* buffer, int numberOfBytesToWrite, int* numberOfBytesWritten, IntPtr overlapped);

    // Consumers depend only on the interfaces; they never see the concrete array type. NoInlining
    // so the JIT can't devirtualize back to the array and must use real interface dispatch.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SumViaEnumerable(IEnumerable<IService> services)
    {
        int sum = 0;
        foreach (IService s in services) // IEnumerable<T>.GetEnumerator + IEnumerator<T>.MoveNext/Current
            sum += s.Id();
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SumViaReadOnlyList(IReadOnlyList<IService> services)
    {
        int sum = 0;
        for (int i = 0; i < services.Count; i++) // IReadOnlyList<T>.Count + indexer, on the array
            sum += services[i].Id();
        return sum;
    }

    private static int Main()
    {
        // A raw array — fast mimalloc storage — handed out as the generic collection interfaces.
        IService[] arr = new IService[3];
        arr[0] = new ServiceA();
        arr[1] = new ServiceB();
        arr[2] = new ServiceC();

        if (SumViaEnumerable(arr) != 1 + 2 + 4) // foreach over IEnumerable<IService>
            return 1;

        if (SumViaEnumerable(arr) != 7) // a fresh enumerator restarts correctly
            return 2;

        if (SumViaReadOnlyList(arr) != 7) // Count + indexer through IReadOnlyList<IService>
            return 3;

        // Array covariance still holds: a reference array IS an object, storable in an object[]
        // slot (exercises the array-aware assignability check in TypeCast.StelemRef — a store
        // that wrongly failed would fail-fast here; a wrong success would change the read-back).
        object[] holder = new object[1];
        holder[0] = arr;
        if (holder[0] != (object)arr)
            return 4;

        const string message = "Puck.BareMetal: raw T[] is a first-class IEnumerable<T> (sum=7).\r\n";

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
