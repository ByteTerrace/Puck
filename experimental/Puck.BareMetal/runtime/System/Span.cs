// Span<T> / ReadOnlySpan<T> over a (ref, length) pair, plus the byte-level move/clear helpers.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    public readonly ref struct Span<T>
    {
        internal readonly ref T _reference;
        private readonly int _length;

        public Span(ref T reference, int length) { _reference = ref reference; _length = length; }
        public Span(T[] array) { _reference = ref MemoryMarshal.GetArrayDataReference(array); _length = array.Length; }
        public unsafe Span(void* pointer, int length) { _reference = ref Unsafe.AsRef<T>(pointer); _length = length; }

        public int Length => _length;
        public bool IsEmpty => _length == 0;

        public ref T this[int index] => ref Unsafe.Add(ref _reference, index);

        public ref T GetPinnableReference()
            => ref _length != 0 ? ref _reference : ref Unsafe.NullRef<T>();

        public Span<T> Slice(int start) => new Span<T>(ref Unsafe.Add(ref _reference, start), _length - start);
        public Span<T> Slice(int start, int length) => new Span<T>(ref Unsafe.Add(ref _reference, start), length);

        public void CopyTo(Span<T> destination)
            => SpanHelpers.Memmove(ref Unsafe.As<T, byte>(ref destination._reference),
                                   ref Unsafe.As<T, byte>(ref _reference),
                                   (nuint)_length * (nuint)Unsafe.SizeOf<T>());

        public void Clear()
            => SpanHelpers.ClearWithoutReferences(ref Unsafe.As<T, byte>(ref _reference),
                                                  (nuint)_length * (nuint)Unsafe.SizeOf<T>());

        public T[] ToArray()
        {
            T[] result = new T[_length];
            CopyTo(result);
            return result;
        }

        public static implicit operator Span<T>(T[] array) => new Span<T>(array);
        public static implicit operator ReadOnlySpan<T>(Span<T> span) => new ReadOnlySpan<T>(ref span._reference, span._length);
    }

    public readonly ref struct ReadOnlySpan<T>
    {
        internal readonly ref T _reference;
        private readonly int _length;

        public ReadOnlySpan(ref T reference, int length) { _reference = ref reference; _length = length; }
        public ReadOnlySpan(T[] array) { _reference = ref MemoryMarshal.GetArrayDataReference(array); _length = array.Length; }
        public unsafe ReadOnlySpan(void* pointer, int length) { _reference = ref Unsafe.AsRef<T>(pointer); _length = length; }

        public int Length => _length;
        public bool IsEmpty => _length == 0;

        public ref readonly T this[int index] => ref Unsafe.Add(ref _reference, index);

        public ref readonly T GetPinnableReference()
            => ref _length != 0 ? ref _reference : ref Unsafe.NullRef<T>();

        public ReadOnlySpan<T> Slice(int start) => new ReadOnlySpan<T>(ref Unsafe.Add(ref _reference, start), _length - start);
        public ReadOnlySpan<T> Slice(int start, int length) => new ReadOnlySpan<T>(ref Unsafe.Add(ref _reference, start), length);

        public void CopyTo(Span<T> destination)
            => SpanHelpers.Memmove(ref Unsafe.As<T, byte>(ref destination._reference),
                                   ref Unsafe.As<T, byte>(ref _reference),
                                   (nuint)_length * (nuint)Unsafe.SizeOf<T>());

        public static implicit operator ReadOnlySpan<T>(T[] array) => new ReadOnlySpan<T>(array);
    }

    internal static class SpanHelpers
    {
        // Overlap-safe byte move (the JIT's BulkMoveWithWriteBarrier routes here via compat/Buffer).
        public static void Memmove(ref byte destination, ref byte source, nuint byteCount)
        {
            if (Unsafe.AreSame(ref destination, ref source) || byteCount == 0)
                return;

            // Copy backward when the destination overlaps ahead of the source.
            if ((nuint)Unsafe.ByteOffset(ref source, ref destination) < byteCount)
            {
                for (nuint i = byteCount; i != 0; i--)
                    Unsafe.Add(ref destination, (nint)(i - 1)) = Unsafe.Add(ref source, (nint)(i - 1));
            }
            else
            {
                for (nuint i = 0; i < byteCount; i++)
                    Unsafe.Add(ref destination, (nint)i) = Unsafe.Add(ref source, (nint)i);
            }
        }

        public static void ClearWithoutReferences(ref byte destination, nuint byteCount)
            => Fill(ref destination, byteCount, 0);

        // The JIT routes initblk / Span.Fill here (the cpblk counterpart of Memmove).
        public static void Fill(ref byte destination, nuint byteCount, byte value)
        {
            for (nuint i = 0; i < byteCount; i++)
                Unsafe.Add(ref destination, (nint)i) = value;
        }
    }
}
