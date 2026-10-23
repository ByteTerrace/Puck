using Puck.Assets;

namespace Puck.Scripting;

/// <summary>A named addon refusal discovered before native compilation or store creation.</summary>
public sealed class AddonModuleLoadException : IOException {
    /// <summary>Creates a refusal attributed to the bytes that were read.</summary>
    /// <param name="kind">The sticky addon fault classification.</param>
    /// <param name="hash">The full source identity.</param>
    /// <param name="message">The violated load contract.</param>
    public AddonModuleLoadException(AddonFaultKind kind, ContentPin hash, string message) : base(message) {
        Kind = kind;
        Hash = hash;
    }

    /// <summary>Gets the sticky addon fault classification.</summary>
    public AddonFaultKind Kind { get; }
    /// <summary>Gets the source's full identity.</summary>
    public ContentPin Hash { get; }
}
