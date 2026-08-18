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

    /// <summary>Resolves this value from the referenced Text state cell.</summary>
    bool TryResolve(string text, out string reason);
}
