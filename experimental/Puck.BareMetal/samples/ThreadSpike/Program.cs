// Puck.BareMetal — multi-threaded hardening stress test.
//
// N OS threads are created paused at a spin barrier, then released simultaneously. Each thread:
//   (1) first-accesses LazyShared.Instance — a lazy static .cctor with a reference-typed (GC)
//       static, so all N threads race the same first-access; and
//   (2) allocates and verifies many small buffers — exercising mimalloc's per-thread heap init
//       and concurrent allocation on fresh threads that never ran CRT/runtime startup.
//
// Correctness checks after all threads join:
//   * the .cctor ran exactly once (CtorRuns == 1), and
//   * every thread observed the SAME, non-null, fully-constructed instance (a double-run or an
//     early-proceed-before-construction would show a different/null reference), and
//   * every thread's allocations were valid (zeroed + writable).
//
// Exit code: 0 on success; a non-zero code identifies which check/step failed.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal sealed class Token
{
    public int Value;
}

internal static class LazyShared
{
    // Runtime-seeded so the ILC can't fold/freeze it -> a real lazy runtime .cctor.
    internal static readonly Token Instance = Make();

    private static Token Make()
    {
        Program.CtorRuns++; // only the single winning thread runs this
        return new Token { Value = (int)(Program.Seed() & 0xFF) | 0x100 };
    }
}

internal static unsafe class Program
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const int ThreadCount = 8;
    private const uint INFINITE = 0xFFFFFFFFu;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr hFile, byte* buffer, int numberOfBytesToWrite, int* numberOfBytesWritten, IntPtr overlapped);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern uint GetTickCount();

    [DllImport("kernel32")]
    private static extern IntPtr CreateThread(IntPtr lpThreadAttributes, nuint dwStackSize, delegate* unmanaged<void*, uint> lpStartAddress, void* lpParameter, uint dwCreationFlags, uint* lpThreadId);

    [DllImport("kernel32")]
    private static extern uint WaitForMultipleObjects(uint nCount, IntPtr* lpHandles, int bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32")]
    private static extern int CloseHandle(IntPtr hObject);

    internal static int CtorRuns;
    private static volatile int s_go;
    private static Token[] s_observed;
    private static bool[] s_allocOk;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static uint Seed() => GetTickCount();

    [UnmanagedCallersOnly]
    private static uint ThreadProc(void* param)
    {
        int index = (int)(nint)param;

        while (s_go == 0) // spin until all threads are released together (maximize the race)
        {
        }

        Token observed = LazyShared.Instance; // the contended first-access

        bool allocOk = true;
        for (int i = 0; i < 256; i++)
        {
            byte[] b = new byte[256];
            if (b[0] != 0 || b[255] != 0) // mimalloc hands back zeroed storage
            {
                allocOk = false;
                break;
            }
            b[0] = 0x5A;
            b[255] = 0xA5;
            if (b[0] != 0x5A || b[255] != 0xA5)
            {
                allocOk = false;
                break;
            }
        }

        s_observed[index] = observed;
        s_allocOk[index] = allocOk;
        return 0;
    }

    private static int Main()
    {
        s_observed = new Token[ThreadCount];
        s_allocOk = new bool[ThreadCount];

        IntPtr* handles = stackalloc IntPtr[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            uint threadId;
            handles[i] = CreateThread(default, 0, &ThreadProc, (void*)(nint)i, 0, &threadId);
            if (handles[i] == 0)
                return 10;
        }

        s_go = 1; // release all threads at once
        WaitForMultipleObjects(ThreadCount, handles, 1, INFINITE);

        for (int i = 0; i < ThreadCount; i++)
            CloseHandle(handles[i]);

        if (CtorRuns != 1) // .cctor must have run exactly once across all threads
            return 1;

        Token first = s_observed[0];
        if (first == null)
            return 2;
        if (first.Value != ((first.Value & 0xFF) | 0x100)) // sanity: properly constructed
            return 3;

        for (int i = 0; i < ThreadCount; i++)
        {
            if ((object)s_observed[i] != (object)first) // every thread saw the same instance
                return 4;
            if (!s_allocOk[i]) // every thread's concurrent allocations were valid
                return 5;
        }

        const string message = "Puck.BareMetal: 8 threads raced a lazy .cctor + allocated concurrently - one .cctor, all consistent.\r\n";

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
