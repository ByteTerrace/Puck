// Puck.BareMetal — IEnumerable<T> / collection-resolution spike.
//
// Models the DI scenario the engine wants: a consumer that depends on IEnumerable<IService>,
// several IService implementers "registered", and the consumer iterating the whole set with
// foreach. This exercises GENERIC interface dispatch (IEnumerable<IService>.GetEnumerator,
// IEnumerator<IService>.MoveNext / Current) on top of the non-generic interface dispatch that
// already works, plus the per-element IService.Id() call (itself interface dispatch).
//
// The enumerable is backed by a CUSTOM collection (ServiceList), the way a DI container hands
// back its own collection typed as IEnumerable<T>. (Raw T[] also works as IEnumerable<T> here —
// see ArraySpike — but a custom type exercises an ordinary user-defined enumerator.)
//
// IEnumerable<T> / IEnumerator<T> live in the corelib (compat/Collections.Compat.cs).
//
// Exit code: 0 on success; a non-zero code identifies which check failed.

using System;
using System.Collections;          // non-generic IEnumerable/IEnumerator (corelib: compat/Collections.Compat.cs)
using System.Collections.Generic;  // IEnumerable<T>/IEnumerator<T> (corelib: compat/Collections.Compat.cs)
using System.Runtime.InteropServices;

// ---- the "service" contract and a few implementers (what a DI container would register) ----
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

// ---- a custom array-backed collection a container could return as IEnumerable<IService> ----
internal sealed class ServiceList : IEnumerable<IService>
{
    private readonly IService[] _items;
    private int _count;

    public ServiceList(int capacity) => _items = new IService[capacity];

    public void Add(IService item) => _items[_count++] = item;

    public IEnumerator<IService> GetEnumerator() => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Enumerator : IEnumerator<IService>
    {
        private readonly ServiceList _list;
        private int _index = -1;

        public Enumerator(ServiceList list) => _list = list;

        public bool MoveNext() => ++_index < _list._count;

        public IService Current => _list._items[_index];

        object IEnumerator.Current => Current;

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}

internal static unsafe class Program
{
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr hFile, byte* buffer, int numberOfBytesToWrite, int* numberOfBytesWritten, IntPtr overlapped);

    // The consumer: depends only on IEnumerable<IService>, iterates the whole set. The foreach
    // goes through generic interface dispatch; each Id() is non-generic interface dispatch.
    private static int SumIds(IEnumerable<IService> services)
    {
        int sum = 0;
        foreach (IService s in services)
            sum += s.Id();
        return sum;
    }

    private static int Main()
    {
        // "Register" three implementers, hand them to the consumer as IEnumerable<IService>.
        ServiceList list = new ServiceList(3);
        list.Add(new ServiceA());
        list.Add(new ServiceB());
        list.Add(new ServiceC());

        if (SumIds(list) != 1 + 2 + 4) // distinct bits: any missed/duplicated element changes it
            return 1;

        // Iterate a second time to confirm a fresh enumerator restarts correctly.
        if (SumIds(list) != 7)
            return 2;

        const string message = "Puck.BareMetal: IEnumerable<T> resolves N registrations (sum=7).\r\n";

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
