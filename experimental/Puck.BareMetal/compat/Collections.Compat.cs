// Part of Puck.BareMetal. The well-known collection interface family. These must live in the CORE
// LIBRARY (not just any assembly) because:
//   * Roslyn synthesizes the interfaces a single-dimensional array implements from the well-known
//     IList<T> / IReadOnlyList<T> (and their bases). Without them in corelib, even
//     `IEnumerable<T> e = myArray;` is a compile error (CS0029).
//   * `foreach` over an IEnumerable<T> binds to IEnumerable<T>.GetEnumerator / IEnumerator<T>.

namespace System.Collections {
    public interface IEnumerator {
        bool MoveNext();

        object Current { get; }

        void Reset();
    }
    public interface IEnumerable {
        IEnumerator GetEnumerator();
    }
}
namespace System.Collections.Generic {
    public interface IEnumerator<out T> : System.Collections.IEnumerator, System.IDisposable {
        new T Current { get; }
    }
    public interface IEnumerable<out T> : System.Collections.IEnumerable {
        new IEnumerator<T> GetEnumerator();
    }
    public interface IReadOnlyCollection<out T> : IEnumerable<T> {
        int Count { get; }
    }
    public interface IReadOnlyList<out T> : IReadOnlyCollection<T> {
        T this[int index] { get; }
    }
    public interface ICollection<T> : IEnumerable<T> {
        int Count { get; }
        bool IsReadOnly { get; }

        void Add(T item);
        void Clear();
        bool Contains(T item);
        void CopyTo(T[] array, int arrayIndex);
        bool Remove(T item);
    }
    public interface IList<T> : ICollection<T> {
        T this[int index] { get; set; }

        int IndexOf(T item);
        void Insert(int index, T item);
        void RemoveAt(int index);
    }
}
