// Puck.BareMetal — ArrayPool<T> spike (hosted).
//
// Verifies System.Buffers.ArrayPool<T>: that Rent hands back an array at least as large as
// requested, that Return + Rent REUSES the same backing array (the whole point under no-GC),
// that clearArray zeroes a returned array, and that the static Shared pool works (which on the
// hosted build exercises GC-static init + the lazy static constructor).
//
// Exit code: 0 on success; a non-zero code identifies which check failed.

using System;
using System.Buffers;
using System.Runtime.InteropServices;

internal static unsafe class Program
{
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr hFile, byte* buffer, int numberOfBytesToWrite, int* numberOfBytesWritten, IntPtr overlapped);

    private static int Main()
    {
        ArrayPool<int> pool = ArrayPool<int>.Shared; // exercises the static + its lazy .cctor

        int[] a = pool.Rent(100);
        if (a.Length < 100)
            return 1;
        int capacity = a.Length; // a power-of-two bucket capacity (128)
        a[0] = 42;
        pool.Return(a);

        // Return + Rent of the same size must hand back the SAME array (recycled, not reallocated).
        int[] b = pool.Rent(100);
        if ((object)b != (object)a)
            return 2;
        if (b[0] != 42) // not cleared: the old contents are still there
            return 3;

        // clearArray zeroes the array on return.
        pool.Return(b, clearArray: true);
        int[] c = pool.Rent(100);
        if ((object)c != (object)a)
            return 4;
        if (c[0] != 0)
            return 5;

        // A larger request comes from a different bucket -> a different array.
        int[] big = pool.Rent(5000);
        if (big.Length < 5000)
            return 6;
        if ((object)big == (object)a)
            return 7;

        // Returning more than the per-bucket cap must not crash; subsequent rents stay bucket-sized.
        pool.Return(c);
        for (int i = 0; i < 20; i++)
            pool.Return(new int[capacity]);
        for (int i = 0; i < 8; i++)
            if (pool.Rent(100).Length != capacity)
                return 8;

        // Distinct element types get distinct pools (distinct Shared instantiations).
        byte[] bytes = ArrayPool<byte>.Shared.Rent(64);
        if (bytes.Length < 64)
            return 9;

        const string message = "Puck.BareMetal: ArrayPool<T> rents, recycles, clears, and pools per type.\r\n";

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
