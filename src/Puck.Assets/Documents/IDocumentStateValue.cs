namespace Puck.Assets.Documents;

/// <summary>A literal document value or an unresolved symbolic reference to one.</summary>
/// <remarks>
/// A containing document which owns a reference vocabulary resolves <see cref="Reference"/> after the complete
/// document is available. The reference remains attached after resolution so canonical write-back preserves the
/// authored indirection.
/// </remarks>
public interface IDocumentStateValue {
    /// <summary>The authored symbolic reference, or <see langword="null"/> for a literal.</summary>
    string? Reference { get; }
    /// <summary>A short description of the value shape required from the referenced Text state cell.</summary>
    string ExpectedValue { get; }

    /// <summary>
    /// Drops the authored reference, leaving the resolved literal — the flattening a document performs when it
    /// crosses a boundary that does not carry the reference's own vocabulary.
    /// </summary>
    /// <remarks>
    /// Never legal on a live document: the reference is the authored single source of truth canonical write-back
    /// preserves. An unresolved value refuses with the same message reading it would.
    /// </remarks>
    void Detach();
    /// <summary>Resolves this value from the referenced Text state cell.</summary>
    bool TryResolve(string text, out string reason);
}
