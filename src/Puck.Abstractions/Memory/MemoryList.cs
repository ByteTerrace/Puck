using System.Collections;

namespace Puck.Abstractions.Memory;

/// <summary>A read-only list over a run of memory, managed or native, so storage an owner keeps in an
/// <see cref="OwnedBuffer{T}"/> stands where a reader takes a list. Every read goes through the memory's span, so a list
/// over storage its owner has freed throws <see cref="ObjectDisposedException"/> as the storage's own span does, rather
/// than reading it.</summary>
/// <typeparam name="T">The element.</typeparam>
/// <param name="memory">The memory, whose length is the list's count.</param>
public sealed class MemoryList<T>(ReadOnlyMemory<T> memory) : IReadOnlyList<T> {
    /// <summary>Gets the element count: the memory's length, readable after its storage is freed.</summary>
    public int Count => memory.Length;
    /// <summary>Gets an element.</summary>
    /// <param name="index">The element's index, below <see cref="Count"/>.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is negative or not below
    /// <see cref="Count"/>.</exception>
    /// <exception cref="ObjectDisposedException">The storage the memory views was freed.</exception>
    public T this[int index] => memory.Span[index];
    /// <summary>Gets a borrow of the elements, which ends when the storage is freed.</summary>
    /// <exception cref="ObjectDisposedException">The storage the memory views was freed.</exception>
    public ReadOnlySpan<T> Span => memory.Span;

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() {
        for (var index = 0; (index < memory.Length); index++) { yield return memory.Span[index]; }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
