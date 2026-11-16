namespace Puck.Abstractions.Collections;

/// <summary>Receives each slot an item takes as a <see cref="BinaryHeap"/> moves it, so an indexed heap can keep a
/// position table for decrease-key. Implemented by a struct so the heap's generic instantiation inlines it.</summary>
/// <typeparam name="T">The heap's item type.</typeparam>
public interface IBinaryHeapSlots<T> {
    /// <summary>Records that an item now sits at a heap slot.</summary>
    /// <param name="item">The item moved.</param>
    /// <param name="slot">The item's new slot, in [0, count).</param>
    void Moved(T item, int slot);
}
/// <summary>The one binary heap over a caller's span: a min-heap in a caller-supplied order, whose first item is the
/// least. A max-heap, such as a bounded top-k that keeps the least items seen, is the same heap in the reversed order.
/// </summary>
/// <remarks>
/// <para>The order is a struct <see cref="IComparer{T}"/>, so each instantiation inlines its comparison and allocates
/// nothing. The heap breaks no tie itself: an order that is total (one that breaks ties by a slot or ordinal) gives
/// every operation one deterministic result whatever the input order, and a total order is what a deterministic caller
/// supplies.</para>
/// <para>The span and count are the caller's, so the storage may be pooled, stack-allocated or a field. An indexed
/// heap passes an <see cref="IBinaryHeapSlots{T}"/> that each overload taking one tells of every move, including the
/// final slot of the item it placed.</para>
/// </remarks>
public static class BinaryHeap {
    /// <summary>Adds an item.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TOrder">The order; a negative comparison means the left item comes first.</typeparam>
    /// <param name="heap">The heap's storage, longer than <paramref name="count"/>.</param>
    /// <param name="count">The number of items in the heap, advanced by one.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="order">The order.</param>
    public static void Push<T, TOrder>(Span<T> heap, ref int count, T item, TOrder order) where TOrder : struct, IComparer<T> {
        var slots = default(NoSlots<T>);

        Push(count: ref count, heap: heap, item: item, order: order, slots: ref slots);
    }
    /// <summary>Adds an item and reports every slot change.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TOrder">The order; a negative comparison means the left item comes first.</typeparam>
    /// <typeparam name="TSlots">The slot observer.</typeparam>
    /// <param name="heap">The heap's storage, longer than <paramref name="count"/>.</param>
    /// <param name="count">The number of items in the heap, advanced by one.</param>
    /// <param name="item">The item to add.</param>
    /// <param name="order">The order.</param>
    /// <param name="slots">The observer told of every item's new slot.</param>
    public static void Push<T, TOrder, TSlots>(Span<T> heap, ref int count, T item, TOrder order, ref TSlots slots)
        where TOrder : struct, IComparer<T> where TSlots : struct, IBinaryHeapSlots<T> =>
        SiftUp(heap: heap, index: count++, item: item, order: order, slots: ref slots);
    /// <summary>Removes and returns the first item.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TOrder">The order; a negative comparison means the left item comes first.</typeparam>
    /// <param name="heap">The heap's storage.</param>
    /// <param name="count">The number of items in the heap, at least one, reduced by one.</param>
    /// <param name="order">The order.</param>
    /// <returns>The item that came first; the slot at the new <paramref name="count"/> is free for the caller.</returns>
    public static T Pop<T, TOrder>(Span<T> heap, ref int count, TOrder order) where TOrder : struct, IComparer<T> {
        var slots = default(NoSlots<T>);

        return Pop(count: ref count, heap: heap, order: order, slots: ref slots);
    }
    /// <summary>Removes and returns the first item and reports every slot change.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TOrder">The order; a negative comparison means the left item comes first.</typeparam>
    /// <typeparam name="TSlots">The slot observer.</typeparam>
    /// <param name="heap">The heap's storage.</param>
    /// <param name="count">The number of items in the heap, at least one, reduced by one.</param>
    /// <param name="order">The order.</param>
    /// <param name="slots">The observer told of every remaining item's new slot; the removed item is not reported.</param>
    /// <returns>The item that came first.</returns>
    public static T Pop<T, TOrder, TSlots>(Span<T> heap, ref int count, TOrder order, ref TSlots slots)
        where TOrder : struct, IComparer<T> where TSlots : struct, IBinaryHeapSlots<T> {
        var first = heap[0];
        var last = heap[--count];

        if (count != 0) {
            SiftDown(count: count, heap: heap, index: 0, item: last, order: order, slots: ref slots);
        }
        return first;
    }
    /// <summary>Replaces the first item and restores the heap.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TOrder">The order; a negative comparison means the left item comes first.</typeparam>
    /// <param name="heap">The heap's storage.</param>
    /// <param name="count">The number of items in the heap, at least one.</param>
    /// <param name="item">The item that takes the first item's place.</param>
    /// <param name="order">The order.</param>
    public static void ReplaceFirst<T, TOrder>(Span<T> heap, int count, T item, TOrder order) where TOrder : struct, IComparer<T> {
        var slots = default(NoSlots<T>);

        SiftDown(count: count, heap: heap, index: 0, item: item, order: order, slots: ref slots);
    }
    /// <summary>Moves a queued item toward the first slot after its key decreased.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TOrder">The order; a negative comparison means the left item comes first.</typeparam>
    /// <typeparam name="TSlots">The slot observer.</typeparam>
    /// <param name="heap">The heap's storage.</param>
    /// <param name="index">The item's current slot.</param>
    /// <param name="order">The order.</param>
    /// <param name="slots">The observer told of every item's new slot.</param>
    public static void Decrease<T, TOrder, TSlots>(Span<T> heap, int index, TOrder order, ref TSlots slots)
        where TOrder : struct, IComparer<T> where TSlots : struct, IBinaryHeapSlots<T> =>
        SiftUp(heap: heap, index: index, item: heap[index], order: order, slots: ref slots);
    /// <summary>Sorts a heap's items in place, last in the order first, emptying the heap: in a reversed order the
    /// result reads first to last.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TOrder">The heap's order.</typeparam>
    /// <param name="heap">The heap's storage.</param>
    /// <param name="count">The number of items in the heap.</param>
    /// <param name="order">The heap's order.</param>
    public static void SortDescending<T, TOrder>(Span<T> heap, int count, TOrder order) where TOrder : struct, IComparer<T> {
        while (count > 1) {
            var first = Pop(count: ref count, heap: heap, order: order);

            heap[count] = first;
        }
    }

    // Each sift carries the moving item in a hole, writing every displaced item once and the moving item at the end.
    private static void SiftDown<T, TOrder, TSlots>(Span<T> heap, int count, int index, T item, TOrder order, ref TSlots slots)
        where TOrder : struct, IComparer<T> where TSlots : struct, IBinaryHeapSlots<T> {
        while (true) {
            var child = ((index * 2) + 1);

            if (child >= count) {
                break;
            }
            if (((child + 1) < count) && (order.Compare(x: heap[(child + 1)], y: heap[child]) < 0)) {
                child++;
            }
            if (order.Compare(x: heap[child], y: item) >= 0) {
                break;
            }
            heap[index] = heap[child];
            slots.Moved(item: heap[index], slot: index);
            index = child;
        }
        heap[index] = item;
        slots.Moved(item: item, slot: index);
    }
    private static void SiftUp<T, TOrder, TSlots>(Span<T> heap, int index, T item, TOrder order, ref TSlots slots)
        where TOrder : struct, IComparer<T> where TSlots : struct, IBinaryHeapSlots<T> {
        while (index > 0) {
            var parent = ((index - 1) / 2);

            if (order.Compare(x: item, y: heap[parent]) >= 0) {
                break;
            }
            heap[index] = heap[parent];
            slots.Moved(item: heap[index], slot: index);
            index = parent;
        }
        heap[index] = item;
        slots.Moved(item: item, slot: index);
    }

    private struct NoSlots<T> : IBinaryHeapSlots<T> {
        public readonly void Moved(T item, int slot) { }
    }
}
