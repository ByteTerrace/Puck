namespace Puck;

/// <summary>
/// Marks a member, or every member of a type, as outside every format that calls it: its behaviour sets no byte of the
/// wire. <c>puck formats</c> does not follow a call into a seam, and refuses a seam whose reason is empty, so each seam is
/// a claim a reviewer can check.
/// </summary>
/// <remarks>
/// A codec that calls engine behaviour is doing engine work inside a codec; prefer decoding to data and applying it
/// outside the codec to marking a seam. This type is deliberately <c>internal</c> and linked into every project as source
/// (see <c>Directory.Build.props</c>).
/// </remarks>
[AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property | AttributeTargets.Event, Inherited = false, AllowMultiple = false)]
internal sealed class FormatSeamAttribute : Attribute {
    /// <summary>Names the attribute's reason.</summary>
    /// <param name="reason">Why this member's behaviour sets no byte of any format that calls it: <c>its behaviour sets no byte because …</c>.</param>
    public FormatSeamAttribute(string reason) {
        Reason = reason;
    }

    /// <summary>Why this member's behaviour sets no byte of any format that calls it.</summary>
    public string Reason { get; }
}
