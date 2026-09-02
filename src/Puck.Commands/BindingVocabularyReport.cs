using System.Collections.Immutable;

namespace Puck.Commands;

/// <summary>What <see cref="BindingVocabularyCheck.Validate"/> found: every refusal line the document earned, in
/// document order, or none at all.</summary>
/// <remarks>A report is the whole answer, not a delta a caller merges into state it already holds — the check
/// appends to nothing and owns nothing, so the same document checked twice reports the same lines twice rather
/// than accumulating them. A caller collecting refusals from several doors concatenates the reports it gets.</remarks>
/// <param name="Errors">The refusal lines, in the order the document produced them.</param>
public readonly record struct BindingVocabularyReport(ImmutableArray<string> Errors) {
    /// <summary>Gets whether the document earned no refusal at all under the lookups it was checked with. A clean
    /// report is never a claim that the checks a caller withheld would have passed — see
    /// <see cref="BindingVocabularyLookups"/>.</summary>
    public bool IsClean => Errors.IsDefaultOrEmpty;
}
