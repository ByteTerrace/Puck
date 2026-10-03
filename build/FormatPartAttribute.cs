namespace Puck;

/// <summary>
/// Declares a type or member part of a named format's wire although no code the format's own files contain calls it: a
/// payload codec that a protocol's version key governs, for instance, which the handshake never invokes. <c>puck formats</c>
/// makes every unit of the declaration a root of that format's closure, so editing it moves the format's shape.
/// </summary>
/// <remarks>
/// The argument is the format's ledger id, <c>Type.Member</c> as <c>FormatVersions.json</c> spells it. This type is
/// deliberately <c>internal</c> and linked into every project as source (see <c>Directory.Build.props</c>), like
/// <see cref="FormatLeafAttribute"/>; the CLI reads it from syntax by name.
/// </remarks>
[AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property | AttributeTargets.Event, Inherited = false, AllowMultiple = true)]
internal sealed class FormatPartAttribute : Attribute {
    /// <summary>Names the format.</summary>
    /// <param name="format">The format's ledger id, <c>Type.Member</c>.</param>
    public FormatPartAttribute(string format) {
        Format = format;
    }

    /// <summary>The format's ledger id.</summary>
    public string Format { get; }
}
