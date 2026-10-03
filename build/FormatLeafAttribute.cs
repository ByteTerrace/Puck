namespace Puck;

/// <summary>
/// Marks a member, or every member of a type, as part of a format's wire: the code a codec's read and write paths call
/// whose behaviour decides a byte of the format. <c>puck formats</c> covers a marked member in the shape of every format
/// whose codec calls it, so editing it moves their fingerprints.
/// </summary>
/// <remarks>
/// This type is deliberately <c>internal</c> and linked into every project as source (see <c>Directory.Build.props</c>),
/// like <c>VerifiedCodeAttribute</c>: <c>Puck.Maths</c> carries zero <c>ProjectReference</c>s, and the CLI reads the marker
/// from syntax by name. A call a codec makes into a repository member that is neither marked <c>[FormatLeaf]</c> nor
/// <see cref="FormatSeamAttribute"/> is open: the shape cannot see it, and <c>puck formats</c> records every open call
/// and refuses a new one.
/// </remarks>
[AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property | AttributeTargets.Event, Inherited = false, AllowMultiple = false)]
internal sealed class FormatLeafAttribute : Attribute;
