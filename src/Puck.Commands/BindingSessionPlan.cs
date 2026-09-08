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
    /// source is the entry's current source, and every source that drives page selection is reserved (capturing one
    /// would break page selection for the whole profile). That is every declared
    /// <see cref="BindingProfileDocument.Modifiers"/> source PLUS every raw chord member
    /// <see cref="BindingProfile.Compile"/> would turn into an implicit modifier — a chord row may name a source id
    /// directly instead of a modifier id, and such a source flips the page just as hard as a declared one.
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

        // The EFFECTIVE page, with its inheritance chain flattened exactly as BindingProfile.Compile flattens it: a
        // page that inherits presents its overrides PLUS everything it merely keeps at runtime, so a session that
        // walked the authored entries alone prompted for the overrides and silently dropped the rest.
        var page = (BindingProfile.EffectivePage(
            document: document,
            pageId: pageId
        )
            ?? throw new ArgumentException(
            message: $"the document has no page \"{pageId}\"",
            paramName: nameof(pageId)
        ));

        if (page.Entries.Count == 0) {
            throw new ArgumentException(
                message: $"page \"{pageId}\" has no entries to walk",
                paramName: nameof(pageId)
            );
        }

        // An activator-triggered entry (BindingPageEntryDefinition.Activator, no Sources) has no single "suggested
        // source" this simple capture-one-press model can prompt for — a guided session walks sourced entries only
        // (suggesting the row's FIRST source), so an activator row is skipped rather than reduced to a misleading
        // suggestion.
        var sourcedEntries = page.Entries.Where(predicate: static entry => (entry.Sources is { Count: > 0 })).ToList();

        if (sourcedEntries.Count == 0) {
            throw new ArgumentException(
                message: $"page \"{pageId}\" has no sourced entries to walk",
                paramName: nameof(pageId)
            );
        }

        return new BindingSessionPlan(
            RequiredPresses: requiredPresses,
            ReservedSources: [.. ReservedSourcesOf(document: document)],
            Steps: [.. sourcedEntries.Select(selector: static entry => new BindingSessionStep(
                    ActivateOn: entry.ActivateOn,
                    Channel: entry.Channel,
                    Command: ((entry.Channel is { } channel)
            ? BindingProfile.ChannelCommandName(channel: channel)
            : entry.Command!),
                    Label: entry.Label,
                    SuggestedSource: entry.Sources![0]
                ))]
        );
    }

    // Every source a capture must refuse because binding it would move a PAGE rather than a command: the declared
    // modifiers' own sources, plus each chord/held member that resolves to neither a declared modifier id nor a
    // declared modifier source. BindingProfile.Compile mints an implicit modifier (default thresholds, the member
    // as its single source) for exactly that last set, so leaving them out would let a guided session capture a
    // page selector onto an ordinary command and quietly make the source flip pages instead of firing.
    private static IEnumerable<string> ReservedSourcesOf(BindingProfileDocument document) {
        // Both sets are OrdinalIgnoreCase because BindingProfile.Compile's modifierIndexById/modifierIndexBySource
        // are: a member differing from a declared modifier's id — or from one of its sources — only by case IS that
        // modifier there and mints nothing, so reserving the raw member string would reserve a control name no
        // catalog declares. When that phantom name collides with a real source the walked page binds, the session
        // refuses the very capture its own step suggests.
        var modifierIds = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var reserved = new List<string>();
        var seen = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        // Null ELEMENTS are skipped as carefully as null collections: a hole anywhere in these four lists is
        // BindingProfile.Compile's refusal to make, in its own words, and crashing here would deny the caller both
        // the plan AND that refusal. A null row reserves nothing, which is exactly what it selects.
        foreach (var modifier in (document.Modifiers ?? [])) {
            if (modifier is null) {
                continue;
            }

            _ = modifierIds.Add(item: modifier.Id);

            foreach (var source in (modifier.Sources ?? [])) {
                if (
                    (source is not null) &&
                    seen.Add(item: source)
                ) {
                    reserved.Add(item: source);
                }
            }
        }

        foreach (var row in (document.Chords ?? [])) {
            if (row is null) {
                continue;
            }

            foreach (var member in row.Members) {
                if (
                    (member is not null) &&
                    !modifierIds.Contains(item: member) &&
                    seen.Add(item: member)
                ) {
                    reserved.Add(item: member);
                }
            }
        }

        return reserved;
    }
}
