using System.Runtime.InteropServices;

namespace Puck.Abstractions.Collections;

/// <summary>
/// Appends to a queue that is published as read-only windows (<see cref="ReadOnlyMemory{T}"/>) which readers keep: a
/// checkpoint, a replay tape or a hash may hold any window the queue ever published. An append writes only past the end
/// of every window published from the storage, so a window a reader holds never changes, and costs amortised constant
/// time: the storage regrows, copying only the live window, when the end reaches its length.
/// </summary>
/// <remarks>
/// <para>The invariant: every slot of the storage below its end belongs to some published window and is never written
/// again. What enforces it: this method is the storage's only writer, its end only ever moves forward within one storage,
/// and a window that is not the storage's own tail (one restored from a checkpoint, or older than the tail) is copied
/// into fresh storage before anything is appended to it.</para>
/// <para>The caller keeps the storage and its end private to the one owner of the queue, and consumes from the front by
/// slicing the window it publishes, never by writing the storage.</para>
/// </remarks>
public static class AppendOnlyWindows {
    /// <summary>Returns <paramref name="window"/> with <paramref name="item"/> appended.</summary>
    /// <typeparam name="T">The entry.</typeparam>
    /// <param name="storage">The queue's storage, or <see langword="null"/> before its first append.</param>
    /// <param name="end">The first slot of <paramref name="storage"/> no window covers.</param>
    /// <param name="window">The queue's current window.</param>
    /// <param name="item">The entry to append.</param>
    /// <param name="least">The fewest slots a new storage holds.</param>
    /// <returns>The queue's new window.</returns>
    public static ReadOnlyMemory<T> Append<T>(ref T[]? storage, ref int end, ReadOnlyMemory<T> window, T item, int least = 16) {
        var tail = (window.IsEmpty
            ? ((storage is not null) && (end < storage.Length))
            : (MemoryMarshal.TryGetArray(memory: window, segment: out var segment) && ReferenceEquals(objA: segment.Array, objB: storage) &&
                ((segment.Offset + segment.Count) == end) && (end < storage!.Length)));

        if (!tail) {
            var grown = new T[Math.Max(val1: least, val2: ((window.Length + 1) * 2))];

            window.Span.CopyTo(destination: grown);
            storage = grown;
            end = window.Length;
        }

        storage![end] = item;
        end++;

        return storage.AsMemory(start: (end - (window.Length + 1)), length: (window.Length + 1));
    }
}
