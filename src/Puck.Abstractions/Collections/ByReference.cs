using System.Runtime.CompilerServices;

namespace Puck.Abstractions.Collections;

/// <summary>Compares an object by reference identity when used as a key or part of a key.</summary>
/// <typeparam name="T">The reference type.</typeparam>
/// <param name="value">The referenced object.</param>
public readonly struct ByReference<T>(T? value) : IEquatable<ByReference<T>> where T : class {
    /// <summary>Gets the referenced object.</summary>
    public T? Value { get; } = value;

    /// <summary>Wraps a reference for identity comparison.</summary>
    public static implicit operator ByReference<T>(T? value) => new(value);

    /// <inheritdoc/>
    public bool Equals(ByReference<T> other) => ReferenceEquals(Value, other.Value);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => (obj is ByReference<T> other) && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => Value is { } owner ? RuntimeHelpers.GetHashCode(owner) : 0;
}

/// <summary>Identifies one revision within an object's lifetime.</summary>
/// <param name="Owner">The object that owns the revision, compared by reference.</param>
/// <param name="Revision">The revision within that owner.</param>
public readonly record struct ReferenceRevision(ByReference<object> Owner, long Revision);
