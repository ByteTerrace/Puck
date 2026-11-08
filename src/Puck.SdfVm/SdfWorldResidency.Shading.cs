namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    // Kept outside device-owned tables. A lost device must not discard the identities needed for quarantine.
    private readonly List<SdfShadeSubmission> m_shadeSubmissions = [];

    /// <summary>Gets the shading identities of every submission recorded on this residency whose completion has not been
    /// proven (<see cref="SdfShadeSlots"/>), in ordinal order without duplicates. A submission a device loss interrupted
    /// stays here: neither a lost nor an abandoned submission is ever read as completed.</summary>
    public IReadOnlyList<string> InFlightShadeIdentities => m_shadeSubmissions.SelectMany(static s => s.Identities).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal SdfShadeSubmission? SubmittedShade(IReadOnlyList<string> identities, SdfShadePipelines? pipelines) {
        if (identities.Count == 0 && pipelines is null) { return null; }
        var submission = new SdfShadeSubmission(residency: this, identities: identities, pipelines: pipelines);

        m_shadeSubmissions.Add(item: submission);
        return submission;
    }
    internal void RetireShade(SdfShadeSubmission submission) => _ = m_shadeSubmissions.Remove(item: submission);
    // A loss proves nothing completed: every submission still held stays reported.
    private void LoseShade() {
        foreach (var submission in m_shadeSubmissions) { submission.Lose(); }
    }
}
