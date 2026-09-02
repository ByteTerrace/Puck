using System.Collections.Immutable;

namespace Puck.Commands;

/// <summary>What <see cref="BindingVocabularyCheck.Validate"/> found: every refusal line the document earned, in
/// document order, or none at all.</summary>
/// <remarks>A report is the whole answer, not a delta a caller merges into state it already holds — the check
/// appends to nothing and owns nothing, so the same document checked twice reports the same lines twice rather
/// than accumulating them. A caller collecting refusals from several doors concatenates the reports it gets.</remarks>
/// <param name="Errors">The refusal lines, in the order the document produced them.</param>
public readonly record struct BindingVocabularyReport(ImmutableArray<string> Errors) {
    private readonly ImmutableArray<string> m_errors = (Errors.IsDefault ? [] : Errors);

    /// <summary>Gets the refusal lines, in the order the document produced them; empty when the document earned
    /// none.</summary>
    /// <remarks>A struct's <see langword="default"/> is always reachable — an unassigned field, a <c>new T[n]</c>
    /// slot, a <see langword="default"/> switch arm — and it skips every initializer, so the array behind an
    /// unfilled report is itself <see cref="ImmutableArray{T}.IsDefault"/>. Reading <c>Length</c> off one of those
    /// throws a bare <see cref="NullReferenceException"/>, which is a crash where the honest answer is "no refusals
    /// were recorded". This accessor answers <see cref="ImmutableArray{T}.Empty"/> for that case, so a report is
    /// readable however it was made.</remarks>
    public ImmutableArray<string> Errors {
        get => (m_errors.IsDefault ? [] : m_errors);
        init => m_errors = (value.IsDefault ? [] : value);
    }
    /// <summary>Gets whether the document earned no refusal at all under the lookups it was checked with. A clean
    /// report is never a claim that the checks a caller withheld would have passed — see
    /// <see cref="BindingVocabularyLookups"/>.</summary>
    public bool IsClean => Errors.IsEmpty;
}
