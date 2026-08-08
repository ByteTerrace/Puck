namespace Puck;

/// <summary>
/// Marks a method, constructor, class, or struct as verified: its source has been hand-reviewed (and, where a
/// <see cref="Laws"/> id names one, proven by a law case in <c>tests/Puck.Maths.Tests</c>), and a token fingerprint
/// of the source that review read is recorded under <see cref="Id"/> in the repository-root
/// <c>VerifiedCode.json</c> manifest.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint covers the branded declaration's own tokens plus the tokens of every declaration the manifest
/// entry names under <c>dependencies</c> — the constants the body reads, the representation it is written against.
/// That list goes exactly one level deep and is written by hand: what a dependency in turn rests on is not covered,
/// and neither is anything the entry does not name. It is a seal over source, not a proof of behaviour; the proof is
/// the <see cref="Basis"/> and the argument the entry records.
/// </para>
/// <para>
/// <c>Puck.Analyzers</c> recomputes the fingerprint on every build and fails the build (VER001) the moment any of
/// those tokens drift from the recorded hash. To change branded code, a change must recompute the hash, update the
/// manifest entry, and re-verify — that update is then visible in the diff — or remove the attribute and the
/// manifest entry together. Deleting the brand without deleting the manifest entry fails the build too (VER002), so
/// the brand cannot silently disappear. An entry naming a dependency the compilation cannot resolve to one walkable
/// declaration is refused (VER010) rather than sealed as though the name covered something.
/// </para>
/// <para>
/// <c>AttributeTargets.Method</c> also admits local functions and lambdas, which have no documentation-comment id
/// and so can never be named by a manifest entry. A brand there is refused (VER007) rather than left standing with
/// nothing behind it: a marker that asserts a proof nothing checks is worse than no marker at all.
/// </para>
/// <para>
/// This type is deliberately <c>internal</c>: <c>Puck.Maths</c>'s coverage ratchet classifies its PUBLIC surface,
/// and a public attribute here would need its own law coverage classification for a marker that carries none of
/// its own semantics. It is linked into every project as source (see <c>Directory.Build.props</c>), not shared via
/// a project reference — <c>Puck.Maths</c> deliberately carries zero <c>ProjectReference</c>s.
/// </para>
/// </remarks>
[AttributeUsage(validOn: (AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class | AttributeTargets.Struct), Inherited = false, AllowMultiple = false)]
internal sealed class VerifiedCodeAttribute : Attribute {
    /// <summary>Names the manifest entry this declaration's fingerprint is recorded under.</summary>
    /// <param name="id">The manifest entry id, keyed in the repository-root <c>VerifiedCode.json</c>.</param>
    public VerifiedCodeAttribute(string id) {
        Id = id;
    }

    /// <summary>The manifest entry id this declaration's fingerprint is recorded under in <c>VerifiedCode.json</c>.</summary>
    public string Id { get; }

    /// <summary>
    /// Why this declaration can be branded at all: <c>exhaustive</c> when every input has been decided by execution,
    /// <c>exact-by-construction</c> when the body is a primitive operation on the representation and carries no
    /// algorithm of its own, or <c>exact-by-proof</c> when a mathematical argument covers every input. More than one
    /// may apply, comma-separated, and the strongest brands carry two.
    /// </summary>
    /// <remarks>
    /// The three are not interchangeable. Exhaustive and exact-by-construction are self-evidencing — the evidence is
    /// mechanical and re-runnable. A proof is a claim ABOUT the code that nothing executes, so an entry resting on
    /// <c>exact-by-proof</c> alone owes the manifest an <c>argument</c> naming the reasoning and any external result it
    /// leans on: what would have to be false for the brand to be wrong.
    /// </remarks>
    public string? Basis { get; init; }

    /// <summary>Optional free-text note on which law ids justify this brand (informational; the manifest's <c>laws</c> array is authoritative).</summary>
    public string? Laws { get; init; }
}
