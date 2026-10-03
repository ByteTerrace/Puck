using System.Collections.Immutable;

namespace Puck.State;

/// <summary>Owns the lists a state declaration keeps. A record that holds a caller's collection holds an alias the
/// caller can still mutate, so every list a state record exposes is copied into an <see cref="ImmutableArray{T}"/> as
/// the record is built or re-initialized, and a cache keyed by the record (an encoding, a validation verdict) stays
/// true for the record's lifetime.</summary>
public static class StateLists {
    /// <summary>Returns a list no caller holds a mutable alias of: <paramref name="items"/> itself when it is already an
    /// <see cref="ImmutableArray{T}"/>, box and all, so a <c>with</c> that leaves a list alone neither copies nor
    /// re-boxes it, else an immutable copy of it.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The list to own, or <see langword="null"/>.</param>
    /// <returns>The owned list, or <see langword="null"/> when <paramref name="items"/> is
    /// <see langword="null"/>.</returns>
    public static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? items) => (items switch {
        null => null,
        ImmutableArray<T> => items,
        _ => items.ToImmutableArray(),
    });
}
