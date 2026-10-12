using System.Globalization;
using Puck.Assets;

namespace Puck.World.Authoring;

/// <summary>
/// Identifies the code that produces creation bakes: the content fingerprint of <see cref="CreationBaker"/>'s assembly
/// and every Puck assembly it reaches (<see cref="AssemblyReach.ContentFingerprint"/>), computed from the running build
/// when first read. Any change to the baker's code, or to any Puck code it can reach, moves it, and with it every bake
/// key (<see cref="CreationBakeKey"/>) and the <c>BAKE</c> chunk's version, so a bake made by other code is never served,
/// while two checkouts of one source share every bake. A change to Puck code the baker does not run moves it too, which
/// costs a rebake and never a wrong one. Outside code (the runtime and packages) is not covered.
/// </summary>
public static class CreationBakeDerivation {
    private static readonly Lazy<string> LazyFingerprint = new(valueFactory: static () => AssemblyReach.ContentFingerprint(root: typeof(CreationBaker).Assembly));

    /// <summary>Gets the fingerprint: sixty-four lowercase hexadecimal digits.</summary>
    public static string Fingerprint => LazyFingerprint.Value;
    /// <summary>Gets the fingerprint's first four bytes, read as an unsigned big-endian integer: the version a
    /// <c>BAKE</c> chunk and each bake pack entry carry.</summary>
    public static uint ChunkVersion => uint.Parse(
        provider: CultureInfo.InvariantCulture,
        s: Fingerprint[..8],
        style: NumberStyles.AllowHexSpecifier
    );
}
