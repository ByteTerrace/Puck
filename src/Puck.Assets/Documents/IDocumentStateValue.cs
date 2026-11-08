namespace Puck.Assets.Documents;

/// <summary>A literal document value or an unresolved symbolic reference to one.</summary>
/// <remarks>
/// A value never changes once built: a containing document which owns a reference vocabulary resolves
/// <see cref="Reference"/> after the complete document is available by building a resolved copy
/// (<see cref="TryResolved"/>) or a live one (<see cref="Bound"/>) and placing it where the original stood, so a value
/// shared by two documents answers both the same. Every copy keeps the reference, unless it was resolved to detach,
/// so canonical write-back preserves the authored indirection.
/// </remarks>
public interface IDocumentStateValue {
    /// <summary>Gets the authored symbolic reference, or <see langword="null"/> for a literal.</summary>
    string? Reference { get; }
    /// <summary>Gets a short description of the value shape required from the referenced Text state cell.</summary>
    string ExpectedValue { get; }
    /// <summary>Gets the owner of the live store a <see cref="Bound"/> copy reads, or <see langword="null"/> for a
    /// literal, a resolved value or an unresolved reference.</summary>
    object? Binder { get; }

    /// <summary>Returns a copy of this value resolved from the referenced cell's text, leaving this value as it is.</summary>
    /// <param name="text">The text the referenced cell holds.</param>
    /// <param name="detach">Whether the copy drops the reference and holds the resolved literal alone, as a document
    /// crossing a boundary that does not carry the reference's own vocabulary does; never legal on a live
    /// document, whose reference is the authored single source of truth canonical write-back preserves.</param>
    /// <param name="resolved">The resolved copy, or this value when <paramref name="text"/> is not one.</param>
    /// <param name="reason">Why <paramref name="text"/> is not a value of this shape, or empty.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> resolved.</returns>
    bool TryResolved(string text, bool detach, out IDocumentStateValue resolved, out string reason);
    /// <summary>Returns a copy of this value that reads the referenced cell through <paramref name="read"/> each time
    /// it is read, leaving this value as it is. Binding never authorizes that read.</summary>
    /// <param name="binder">The owner of the store <paramref name="read"/> reads, which <see cref="Binder"/>
    /// answers.</param>
    /// <param name="read">Reads the referenced cell's text, or <see langword="null"/> when it holds none.</param>
    /// <returns>The bound copy.</returns>
    IDocumentStateValue Bound(object binder, Func<string?> read);
}
