using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

// A zeroed array on the pinned object heap whose usable elements start on a 64-byte boundary: the cache-line size of
// x86 cores and the width of a 512-bit vector, so whole-vector accesses at multiples of 64 bytes from the start never
// span two lines. Managed arrays align only to eight bytes. The pinned heap never moves the array, so the alignment
// found at allocation holds for its lifetime, and the garbage collector frees it like any other array.
internal readonly struct CacheAlignedArray<T> where T : unmanaged {
    private const int Alignment = 64;

    private readonly T[] m_array;
    private readonly int m_start;

    internal int Length { get; }
    internal Span<T> Span => m_array.AsSpan(length: Length, start: m_start);
    internal ref T Reference => ref Unsafe.Add(elementOffset: m_start, source: ref MemoryMarshal.GetArrayDataReference(array: m_array));

    internal unsafe CacheAlignedArray(int length) {
        // Array data is eight-byte aligned and every element size used here divides eight, so the distance to the next
        // boundary is a whole number of elements.
        m_array = GC.AllocateArray<T>(length: (length + (Alignment / sizeof(T))), pinned: true);

        var address = ((nuint)Unsafe.AsPointer(value: ref MemoryMarshal.GetArrayDataReference(array: m_array)));

        m_start = ((int)((address.AlignUp(alignment: ((nuint)Alignment)) - address) / ((nuint)sizeof(T))));
        Length = length;
    }
}
