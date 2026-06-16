// Part of Puck.BareMetal. Makes a single-dimensional array (T[]) implement IEnumerable<T> /
// ICollection<T> / IList<T> / IReadOnlyList<T> so it can pass wherever IEnumerable<T> is expected
// (e.g. DI resolving IEnumerable<TService>) with no wrapper object.
//
// The ILC models T[] as the generic instantiation System.Array<T> (the array's MethodTable is
// emitted as __Array<T>). Declaring the collection interfaces with bodies on that type makes the
// ILC point the array's interface-dispatch map at these bodies; at runtime an interface call lands
// here with `this` being the array itself. The runtime-side resolution of array interface dispatch
// is in compat/native/puck-rt.c.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System
{
    // The other part (runtime/System/CoreTypes.cs) declares `partial class Array<T> : Array`.
    partial class Array<T> : IEnumerable<T>, ICollection<T>, IList<T>, IReadOnlyList<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            T[] self = Unsafe.As<T[]>(this); // `this` is the array; reinterpret as T[]
            return new SZGenericArrayEnumerator<T>(self, self.Length);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ((IList<T>)array).IsReadOnly is true even though array[i] = x works (the array's own
        // indexer is writable; the IList<T> contract reports fixed-size storage as read-only).
        public int Count => Length;
        public bool IsReadOnly => true;

        public T this[int index]
        {
            get => Unsafe.As<T[]>(this)[index];
            set => Unsafe.As<T[]>(this)[index] = value;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            T[] self = Unsafe.As<T[]>(this);
            for (int i = 0; i < self.Length; i++)
                array[arrayIndex + i] = self[i];
        }

        // A fixed-size array genuinely cannot grow/shrink; the BCL throws NotSupportedException
        // here. Puck.Runtime has no exceptions, so fail fast.
        public void Add(T item) => Environment.FailFast(null);
        public void Clear() => Environment.FailFast(null);
        public bool Remove(T item) { Environment.FailFast(null); return false; }
        public void Insert(int index, T item) => Environment.FailFast(null);
        public void RemoveAt(int index) => Environment.FailFast(null);

        // Element search needs T equality (EqualityComparer<T>), which is not modelled yet
        // and which iteration/indexing do not require. Fail fast until implemented.
        public bool Contains(T item) { Environment.FailFast(null); return false; }
        public int IndexOf(T item) { Environment.FailFast(null); return -1; }
    }

    // The enumerator T[].GetEnumerator() hands back: the array plus a [-1, length) cursor.
    internal sealed class SZGenericArrayEnumerator<T> : IEnumerator<T>
    {
        private readonly T[] _array;
        private int _index;
        private readonly int _endIndex;

        internal SZGenericArrayEnumerator(T[] array, int length)
        {
            _array = array;
            _index = -1;
            _endIndex = length;
        }

        public bool MoveNext()
        {
            int next = _index + 1;
            if ((uint)next < (uint)_endIndex)
            {
                _index = next;
                return true;
            }

            _index = _endIndex;
            return false;
        }

        public T Current => _array[_index];

        object IEnumerator.Current => Current; // boxes value-typed T; reference T is unaffected

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}
