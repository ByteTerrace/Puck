namespace Puck.Commands;

/// <summary>One locked-in capture of a binding session: a command and the source that confirmed onto it.</summary>
/// <param name="Command">The command that was bound.</param>
/// <param name="Source">The provider-neutral input source id the player confirmed.</param>
/// <param name="MatchedSuggestion"><see langword="true"/> when the player confirmed the suggested default; <see langword="false"/> when they deviated to their own choice.</param>
/// <param name="ActivateOn">The phase the resulting binding fires on (carried from the step).</param>
/// <param name="Label">The step's display label; opaque to the engine.</param>
/// <param name="Icon">The step's display icon id; opaque to the engine.</param>
public sealed record BindingSessionCapture(
    string Command,
    string Source,
    bool MatchedSuggestion,
    CommandPhase? ActivateOn = null,
    string? Label = null,
    string? Icon = null
);

/// <summary>
/// The outcome of a binding session: the confirmed captures, in step order. <see cref="Apply"/> folds them back
/// into a <see cref="BindingProfileDocument"/> so the result round-trips through the same storage and
/// <see cref="BindingProfile.Compile"/> path every profile takes — the session never invents a parallel format.
/// </summary>
/// <param name="Captures">The confirmed captures, in step order. A partial list (an abandoned session) applies cleanly — unconfirmed steps keep their existing bindings.</param>
public sealed record BindingSessionResult(
    IReadOnlyList<BindingSessionCapture> Captures
) {
    /// <summary>
    /// Applies the captures to one page of a profile document, returning the rewritten document. An entry whose
    /// command was captured moves to its confirmed source; an entry the plan did not cover but whose source a
    /// capture claimed is <em>displaced</em> — dropped from the page and reported, never silently kept as a
    /// duplicate source. A captured command with no existing entry is appended, so a session can bind brand-new
    /// actions. Other pages are untouched (pages are modal; a source may serve different commands per page).
    /// </summary>
    /// <param name="document">The profile document to rewrite.</param>
    /// <param name="pageId">The id of the page the captures target.</param>
    /// <param name="displaced">The uncaptured entries dropped because a capture claimed their source.</param>
    /// <returns>The rewritten document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="pageId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="pageId"/> names no page in <paramref name="document"/>.</exception>
    public BindingProfileDocument Apply(BindingProfileDocument document, string pageId, out IReadOnlyList<BindingPageEntryDefinition> displaced) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageId);

        if (!document.Pages.Any(predicate: page => string.Equals(a: page.Id, b: pageId, comparisonType: StringComparison.Ordinal))) {
            throw new ArgumentException(message: $"the document has no page \"{pageId}\"", paramName: nameof(pageId));
        }

        var captureByCommand = new Dictionary<string, BindingSessionCapture>(comparer: StringComparer.Ordinal);
        var capturedSources = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var capture in Captures) {
            captureByCommand[capture.Command] = capture;
            _ = capturedSources.Add(item: capture.Source);
        }

        var displacedEntries = new List<BindingPageEntryDefinition>();
        var pages = new List<BindingPageDefinition>(capacity: document.Pages.Count);

        foreach (var page in document.Pages) {
            if (!string.Equals(a: page.Id, b: pageId, comparisonType: StringComparison.Ordinal)) {
                pages.Add(item: page);

                continue;
            }

            var entries = new List<BindingPageEntryDefinition>(capacity: page.Entries.Count);
            var appliedCommands = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var entry in page.Entries) {
                if (captureByCommand.TryGetValue(
                    key: entry.Command,
                    value: out var capture
                )) {
                    entries.Add(item: entry with { Source = capture.Source, });
                    _ = appliedCommands.Add(item: entry.Command);
                } else if (capturedSources.Contains(item: entry.Source)) {
                    displacedEntries.Add(item: entry);
                } else {
                    entries.Add(item: entry);
                }
            }

            // A capture whose command had no entry binds a brand-new action onto the page.
            foreach (var capture in Captures) {
                if (!appliedCommands.Contains(item: capture.Command)) {
                    entries.Add(item: new BindingPageEntryDefinition(
                        ActivateOn: capture.ActivateOn,
                        Command: capture.Command,
                        Icon: capture.Icon,
                        Label: capture.Label,
                        Source: capture.Source
                    ));
                    _ = appliedCommands.Add(item: capture.Command);
                }
            }

            pages.Add(item: page with { Entries = entries, });
        }

        displaced = displacedEntries;

        return document with { Pages = pages, };
    }
}
