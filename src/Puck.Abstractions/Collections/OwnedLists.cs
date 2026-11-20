using System.Collections.Immutable;

namespace Puck.Abstractions.Collections;

/// <summary>Owns the lists an immutable record keeps. A record that holds a caller's collection holds an alias the caller
/// can still mutate, so every mutable list such a record exposes is copied into an <see cref="ImmutableArray{T}"/> as the
/// record is built or re-initialized, and a cache keyed by the record (an encoding, a validation verdict, a cached state
/// row) stays true for the record's lifetime. A member owns its list through
/// <c>{ get => field; init => field = OwnedLists.Freeze(items: value); } = OwnedLists.Freeze(items: Member);</c>.</summary>
public static class OwnedLists {
    /// <summary>Returns a list no caller holds a mutable alias of: <paramref name="items"/> itself when it is already an
    /// <see cref="ImmutableArray{T}"/> or <see cref="ImmutableList{T}"/>, box and all, so a <c>with</c> that leaves a list
    /// alone neither copies nor re-boxes it, else an immutable copy of it.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The list to own, or <see langword="null"/>.</param>
    /// <returns>The owned list, or <see langword="null"/> when <paramref name="items"/> is
    /// <see langword="null"/>.</returns>
    public static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? items) => (items switch {
        null => null,
        ImmutableArray<T> => items,
        ImmutableList<T> => items,
        _ => items.ToImmutableArray(),
    });
}
