namespace Puck.Commands;

/// <summary>
/// The data a guided binding session runs over: the ordered steps to walk the player through, how many presses
/// lock a capture in, the sources the session must refuse (page modifiers, movement axes — anything the host
/// reserves), and the hysteresis thresholds that turn an analog source into a press. Pure data — build one by
/// hand for a bespoke tutorial, or from a profile page via <see cref="FromPage"/>.
/// </summary>
/// <param name="Steps">The ordered steps; at least one.</param>
/// <param name="RequiredPresses">The total presses of one source that confirm a step (the first capture plus the confirmations); at least 1. The default 3 is the calibration-wizard triple-press lock.</param>
/// <param name="ReservedSources">Sources the session refuses to capture (a press is reported, never bound); typically the profile's page-modifier sources.</param>
/// <param name="PressThreshold">The value at or above which an analog source counts as pressed.</param>
/// <param name="ReleaseThreshold">The value at or below which a pressed analog source releases; at most <paramref name="PressThreshold"/>.</param>
public sealed record BindingSessionPlan(
    IReadOnlyList<BindingSessionStep> Steps,
    int RequiredPresses = 3,
    IReadOnlyList<string>? ReservedSources = null,
    float PressThreshold = 0.5f,
    float ReleaseThreshold = 0.4f
) {
    /// <summary>
    /// Builds a plan from one page of a binding profile document: every entry becomes a step whose suggested
    /// source is the entry's current source, and every declared modifier source is reserved (capturing one would
    /// break page selection for the whole profile).
    /// </summary>
    /// <param name="document">The profile document.</param>
    /// <param name="pageId">The id of the page to walk.</param>
    /// <param name="requiredPresses">The total presses that confirm a step; at least 1.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="pageId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="pageId"/> names no page in <paramref name="document"/>, or the page has no entries.</exception>
    public static BindingSessionPlan FromPage(BindingProfileDocument document, string pageId, int requiredPresses = 3) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageId);

        var page = (document.Pages.FirstOrDefault(predicate: page => string.Equals(a: page.Id, b: pageId, comparisonType: StringComparison.Ordinal))
            ?? throw new ArgumentException(message: $"the document has no page \"{pageId}\"", paramName: nameof(pageId)));

        if (page.Entries.Count == 0) {
            throw new ArgumentException(message: $"page \"{pageId}\" has no entries to walk", paramName: nameof(pageId));
        }

        return new BindingSessionPlan(
            RequiredPresses: requiredPresses,
            ReservedSources: [.. document.Modifiers.Select(selector: static modifier => modifier.Source)],
            Steps: [.. page.Entries.Select(selector: static entry => new BindingSessionStep(
                ActivateOn: entry.ActivateOn,
                Command: entry.Command,
                Icon: entry.Icon,
                Label: entry.Label,
                SuggestedSource: entry.Source
            ))]
        );
    }
}
