using System.Runtime.Versioning;

namespace Puck.DirectX;

/// <summary>
/// The table every Direct3D 12 image view handle names: a slot index and the slot's generation, packed into one
/// nonzero handle. Releasing a view advances its slot's generation before the slot is reused, so a destroyed view's
/// handle never names a view created after it, and resolving it finds nothing. An exhausted slot is never reused.
/// Registering reserves the room a later release needs, so <see cref="Release"/> allocates nothing and cannot fail, and a
/// registration that throws leaves the table as it was.
/// Handles belong to this process lifetime and are never persisted or exchanged between processes.
/// A <see cref="System.Runtime.InteropServices.GCHandle"/>
/// cannot serve here: the runtime reissues a freed handle's value to the next allocation, so a destroyed view's handle
/// would name whichever object took its slot.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static class DirectXImageViews {
    private static readonly List<DirectXImageView?> Views = [];
    private static readonly List<uint> Generations = [];
    private static readonly Stack<int> Free = new();
    private static readonly Lock Gate = new();

    /// <summary>Registers a view and returns the handle that names it until <see cref="Release"/>.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The view's handle, never zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="view"/> is <see langword="null"/>.</exception>
    public static nint Register(DirectXImageView view) {
        ArgumentNullException.ThrowIfNull(argument: view);

        lock (Gate) {
            if (!Free.TryPop(result: out var index)) {
                index = Views.Count;
                Views.EnsureCapacity(capacity: (index + 1));
                Generations.EnsureCapacity(capacity: (index + 1));
                _ = Free.EnsureCapacity(capacity: (index + 1));
                Views.Add(item: null);
                Generations.Add(item: 0u);
            }

            Views[index] = view;

            return Pack(
                generation: Generations[index],
                index: index
            );
        }
    }
    /// <summary>Releases the view a handle names, so the handle names no view from then on. Releasing a handle that
    /// names no view does nothing.</summary>
    /// <param name="handle">The view's handle.</param>
    public static void Release(nint handle) {
        lock (Gate) {
            if (!TryIndex(handle: handle, index: out var index)) {
                return;
            }

            Views[index] = null;
            if (Generations[index] < uint.MaxValue) {
                Generations[index]++;
                Free.Push(item: index);
            }
        }
    }
    /// <summary>Returns the view a handle names, or <see langword="null"/> when the handle is zero, was never issued,
    /// or names a view that has been released.</summary>
    /// <param name="handle">The view's handle.</param>
    /// <returns>The view, or <see langword="null"/>.</returns>
    public static DirectXImageView? Resolve(nint handle) {
        lock (Gate) {
            return (TryIndex(handle: handle, index: out var index)
                ? Views[index]
                : null
            );
        }
    }

    // The low 32 bits hold the slot index plus one, so no handle is zero; the high 32 bits hold the slot's generation.
    private static nint Pack(int index, uint generation) =>
        unchecked((nint)((((long)generation) << 32) | ((long)((uint)(index + 1)))));
    // The slot a handle names while the slot's generation still matches it and the slot holds a view.
    private static bool TryIndex(nint handle, out int index) {
        var bits = ((long)handle);

        index = (unchecked((int)((uint)bits)) - 1);

        return (
            (index >= 0) &&
            (index < Views.Count) &&
            (Generations[index] == unchecked((uint)(bits >> 32))) &&
            (Views[index] is not null)
        );
    }
}
