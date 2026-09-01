using System.Collections.Frozen;
using System.Collections.Immutable;
using Puck.Assets.Documents;

namespace Puck.Commands;

/// <summary>
/// Validates a <see cref="BindingProfileDocument"/> and compiles it into the runtime
/// <see cref="CompiledBindingProfile"/>: one compiled chord row per document row (a binding table and a
/// precomputed <see cref="BindingPageView"/> for a page meaning; edge payloads for a command meaning), plus the
/// group table the per-slot resolution scopes to.
/// </summary>
/// <remarks>
/// The key uniqueness rules a document must satisfy, rejected loudly otherwise: exactly one meaning per
/// <c>(group, ordered chord)</c>, exactly one resting (empty-chord) page per group, and no repeated non-null entry id
/// within an effective page. The resting row must be a page, since an empty chord has no completion edge to fire a
/// command with. Page ids are unique across the whole document (they address pages in editors and guided sessions).
/// The first row's group is the default group.
/// </remarks>
public static class BindingProfile {
    /// <summary>The command-name prefix a channel destination compiles down to (see
    /// <see cref="BindingPageEntryDefinition.Channel"/>/<see cref="BindingCommandDefinition.Channel"/>). Compiling a
    /// channel destination to a synthesized command keeps the runtime dispatch machinery here (<see cref="CommandBinding"/>,
    /// <see cref="InputRouter"/>, <see cref="CommandRegistry"/>) entirely command-shaped — this project never learns
    /// what a "channel" is. This portable name-shaped form is the default; <see cref="Compile"/> also accepts a host
    /// lowering callback so a fixed command vocabulary can represent a per-seat or remotely discovered table.</summary>
    public const string ChannelCommandPrefix = "channel.";
    /// <summary>The group (and resting page id) the empty profile — a document with no chord rows — compiles to.
    /// Anonymous by design: it is the shape a slot with no bindings resolves in, never authored content.</summary>
    public const string EmptyGroup = "$empty";
    /// <summary>The maximum UTF-16 length of a constant text payload authored on a page entry or chord command.
    /// The payload is copied into a deterministic input snapshot when its binding fires, so the document gate bounds
    /// it before it reaches that per-tick transport.</summary>
    public const int MaxTextPayloadLength = 1024;

    private static string ActivatorIdentity(BindingActivatorDefinition activator) => $"{activator.Mode}\0{string.Join(
        separator: ',',
        values: (activator.Sequence ?? [])
    )}";
    private static (FrozenDictionary<string, IReadOnlyList<CommandBinding>> Table, List<CompiledBindingProfile.CompiledActivatorEntry> Activators) BuildTable(BindingPageDefinition page, Func<ChannelRef, string> channelCommandName, ref int nextActivatorIndex) {
        var entries = (page.Entries ?? []);
        // Group by source into the runtime source→commands table, carrying each entry's full CommandBinding
        // expressiveness (activation edge, constant value). An entry triggered by an ACTIVATOR instead of a plain
        // source (BindingPageEntryDefinition.Activator) is excluded from this table entirely and collected into
        // `activators` — PagedInputBindings evaluates those out-of-band, one RowActivatorTracker per entry.
        var grouped = new Dictionary<string, List<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);
        var activators = new List<CompiledBindingProfile.CompiledActivatorEntry>();
        // OrdinalIgnoreCase to match how a sequence member is actually compared at runtime (RowActivatorTracker,
        // BindingSourceComponent) — a case-variant duplicate ("Gamepad.LeftTrigger" vs "gamepad.leftTrigger") is
        // the SAME shadowed activator there and must be refused here too, not admitted as two distinct rows.
        var seenActivatorKeys = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var seenEntryIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var entryIndex = 0; (entryIndex < entries.Count); entryIndex++) {
            var entry = (entries[entryIndex]
                ?? throw new ArgumentException(
                message: $"Page \"{page.Id}\" entry {entryIndex} is null.",
                paramName: nameof(page)
            ));

            if (
                (entry.Id is { } entryId) &&
                (
                    (entryId.Length == 0) ||
                    !seenEntryIds.Add(item: entryId)
                )
            ) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" entry {entryIndex} id \"{entryId}\" must be non-empty and unique within the page.",
                    paramName: nameof(page)
                );
            }

            if (
                !Enum.IsDefined(value: entry.Mode) ||
                ((entry.ActivateOn is { } activateOn) && !Enum.IsDefined(value: activateOn))
            ) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" entry {entryIndex} carries an invalid mode or activation phase.",
                    paramName: nameof(page)
                );
            }
            var hasSource = (entry.Sources is { Count: > 0 });
            var hasActivator = (entry.Activator is not null);

            if (hasSource == hasActivator) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" carries an entry that must name exactly one trigger — a source or an activator.",
                    paramName: nameof(page)
                );
            }

            var label = entry.TriggerLabel;

            if ((entry.Command is null) == (entry.Channel is null)) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" entry for {label} must carry exactly one destination — a command or a channel.",
                    paramName: nameof(page)
                );
            }

            if (
                (entry.Text is not null) &&
                (
                    (entry.Channel is not null) ||
                    (entry.ActivateOn is not (null or CommandPhase.Started))
                )
            ) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" entry for {label} carries text outside a command press — text is only meaningful on a command destination that activates on Started.",
                    paramName: nameof(page)
                );
            }

            ValidateTextPayload(
                text: entry.Text,
                path: $"Page \"{page.Id}\" entry for {label}",
                paramName: nameof(page)
            );

            ValidateValue(
                value: entry.Value,
                path: $"Page \"{page.Id}\" entry for {label}",
                isChannel: (entry.Channel is not null),
                paramName: nameof(page)
            );

            if (
                (entry.Mode == BindingEntryMode.Toggle) &&
                (entry.Channel is null)
            ) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" entry for {label} sets mode Toggle on a command destination — toggle is only meaningful on a channel destination.",
                    paramName: nameof(page)
                );
            }

            string effectiveCommand;
            CommandValue? effectiveValue;
            float? channelScale;

            if (entry.Channel is { } channel) {
                ValidateChannelRef(
                    channel: channel,
                    path: $"Page \"{page.Id}\" entry for {label}",
                    paramName: nameof(page)
                );

                ValidateChannelScale(
                    channel: channel,
                    path: $"Page \"{page.Id}\" entry for {label}",
                    paramName: nameof(page),
                    scale: entry.Scale
                );

                effectiveCommand = channelCommandName(arg: channel);
                // The scale rides ChannelScale, never Value: Value is an UNCONDITIONAL override (see CommandBinding's
                // own remarks), which would replace an analog source's live sample with the constant scale (the B2
                // defect). InputRouter decides constant-vs-multiply from the live signal's OWN value kind.
                effectiveValue = null;
                channelScale = (entry.Scale ?? 1f);
            } else if (string.IsNullOrEmpty(value: entry.Command)) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" carries an entry without a command or channel.",
                    paramName: nameof(page)
                );
            } else {
                effectiveCommand = entry.Command;
                effectiveValue = entry.Value;
                channelScale = null;
            }

            if (hasActivator) {
                var activator = entry.Activator!;

                if (!Enum.IsDefined(value: activator.Mode)) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" entry for {label} carries an invalid activator mode.",
                        paramName: nameof(page)
                    );
                }

                if (entry.ActivateOn is not null) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" entry for {label} carries ActivateOn beside an activator — the activator's own transition is the entry's edge.",
                        paramName: nameof(page)
                    );
                }

                if (activator.Sequence is not { Count: > 0 }) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" entry for {label} activator sequence must be non-empty.",
                        paramName: nameof(page)
                    );
                }

                foreach (var step in activator.Sequence) {
                    if (string.IsNullOrEmpty(value: step)) {
                        throw new ArgumentException(
                            message: $"Page \"{page.Id}\" entry for {label} activator sequence carries an empty control name.",
                            paramName: nameof(page)
                        );
                    }
                }

                if (activator.Mode == BindingActivatorMode.Held) {
                    var seenSteps = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

                    foreach (var step in activator.Sequence) {
                        if (!seenSteps.Add(item: step)) {
                            throw new ArgumentException(
                                message: $"Page \"{page.Id}\" entry for {label} repeats control \"{step}\" in a Held activator sequence — a simultaneous hold cannot distinguish a repeat.",
                                paramName: nameof(page)
                            );
                        }
                    }

                    if (activator.TimeoutTicks is not null) {
                        throw new ArgumentException(
                            message: $"Page \"{page.Id}\" entry for {label} sets timeoutTicks on a Held activator — timeout only applies to a Tapped sequence.",
                            paramName: nameof(page)
                        );
                    }
                } else if (
                    (activator.TimeoutTicks is { } timeout) &&
                    (timeout <= 0)
                ) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" entry for {label} activator timeoutTicks must be positive.",
                        paramName: nameof(page)
                    );
                }

                var shadowKey = $"{activator.Mode}\0{string.Join(
                    separator: ',',
                    values: activator.Sequence
                )}";

                if (!seenActivatorKeys.Add(item: shadowKey)) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" declares two activators for the same {activator.Mode} sequence [{string.Join(
                            separator: ", ",
                            values: activator.Sequence
                        )}] — the second can never fire (shadowed).",
                        paramName: nameof(page)
                    );
                }

                var pressValue = CompiledBindingProfile.PressValue(
                    channelScale: channelScale,
                    explicitValue: entry.Value
                );

                activators.Add(item: new CompiledBindingProfile.CompiledActivatorEntry(
                    ActivatorIndex: nextActivatorIndex++,
                    Activator: activator with { Sequence = activator.Sequence.ToImmutableArray(), },
                    Edge: new CompiledBindingProfile.CompiledCommandEdge(
                        Command: effectiveCommand,
                        // A channel destination must dispatch its release edge: CommandRegistry.ApplySnapshot skips any
                        // entry whose Dispatch is false, and only the channel verb's handler calls seat.ReleaseChannel —
                        // without dispatch, a closed gate or completed tap would hold the channel forever. A command
                        // destination keeps HoldRelease's own default (momentary; no release needed).
                        DispatchRelease: (channelScale is not null),
                        PressValue: pressValue,
                        ReleaseValue: CommandValue.Inactive(kind: pressValue.Kind),
                        Reassertable: ((channelScale is not null) && (entry.Mode == BindingEntryMode.Hold)),
                        Mode: entry.Mode,
                        Source: BindingSourceIdentity.ForCommand(command: effectiveCommand),
                        Text: entry.Text
                    )
                ));

                continue;
            }

            var seenEntrySources = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            // One row expands to one CommandBinding per listed source, so a single destination reaches every
            // physical control the row names (a gamepad button AND a keyboard key both driving "jump").
            foreach (var rawSource in entry.Sources!) {
                if (string.IsNullOrEmpty(value: rawSource)) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" entry for {label} carries an empty source.",
                        paramName: nameof(page)
                    );
                }

                if (!seenEntrySources.Add(item: rawSource)) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" entry for {label} repeats source \"{rawSource}\".",
                        paramName: nameof(page)
                    );
                }

                // A plain-source entry may name an axis COMPONENT (gamepad.leftStick.x) instead of a bare control —
                // the table key is always the BASE source (what a raw InputSignal actually carries); the component
                // rides the compiled CommandBinding and is extracted at resolve time (see InputRouter's ResolveValue).
                if (!BindingSourceComponent.TrySplit(
                    baseSource: out var baseSource,
                    component: out var component,
                    source: rawSource
                )) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" entry for {rawSource} names a malformed axis component — the final segment must be \"x\" or \"y\".",
                        paramName: nameof(page)
                    );
                }

                if (
                    (component is not null) &&
                    (channelScale is null)
                ) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" entry for {rawSource} names an axis component, which is only meaningful on a channel destination.",
                        paramName: nameof(page)
                    );
                }

                if (!grouped.TryGetValue(
                    key: baseSource,
                    value: out var list
                )) {
                    list = [];
                    grouped[baseSource] = list;
                }

                list.Add(item: new CommandBinding(
                    ActivateOn: entry.ActivateOn,
                    ChannelScale: channelScale,
                    Command: effectiveCommand,
                    Value: effectiveValue,
                    Component: component,
                    Mode: entry.Mode,
                    Text: entry.Text
                ));
            }
        }

        var table = new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var (source, list) in grouped) {
            table[source] = list.ToImmutableArray();
        }

        // Frozen, not Dictionary: this table is built once per compiled profile and then read once per raw input
        // signal for the lifetime of that profile, which is exactly the shape FrozenDictionary optimizes for. The
        // comparer stays OrdinalIgnoreCase — a source id's case is authored-document noise, never identity.
        return (table.ToFrozenDictionary(comparer: StringComparer.OrdinalIgnoreCase), activators);
    }
    private static BindingPageView BuildView(
        string group,
        IReadOnlyList<BindingChordCommandView> hints,
        IReadOnlyList<BindingModifierDefinition> modifiers,
        BindingPageDefinition page,
        HashSet<int> required,
        Func<ChannelRef, string> channelCommandName
    ) {
        var buttons = new BindingPageButtonView[(page.Entries?.Count ?? 0)];
        // Every source the page binds → the button that source triggers. Built here, once per compiled profile, so a
        // presentation layer joining physical slots against a page (the binding bar's twelve sockets, one lookup per
        // slot per plate per bank per seat per frame) never re-derives it by scanning Buttons. Keyed by each source
        // the entry actually LISTS rather than by the entry's comma-joined trigger label, so a row reachable from a
        // gamepad button AND a keyboard key is found under both — a scan over the label found it under neither.
        var buttonsBySource = new Dictionary<string, BindingPageButtonView>(comparer: StringComparer.OrdinalIgnoreCase);

        for (var entryIndex = 0; (entryIndex < buttons.Length); entryIndex++) {
            var entry = page.Entries![entryIndex];
            var button = new BindingPageButtonView(
                Command: ((entry.Channel is { } channel)
                ? channelCommandName(arg: channel)
                : entry.Command!),
                Action: ((entry.Channel is ChannelRef.Name named)
                ? named.Value
                : entry.Command),
                Id: entry.Id,
                Label: entry.Label,
                Toggle: (entry.Mode == BindingEntryMode.Toggle),
                // An activator entry has no Sources — its synthetic "activator[...]" label stands in, so a
                // binding-bar consumer never renders a null/blank chip for it.
                Source: entry.TriggerLabel,
                Sources: ((entry.Sources is { } sources)
                ? sources.ToImmutableArray()
                : [])
            );

            buttons[entryIndex] = button;

            // First entry wins, matching the profile order a consumer would have read by scanning Buttons.
            foreach (var source in (entry.Sources ?? [])) {
                if (!string.IsNullOrEmpty(value: source)) {
                    _ = buttonsBySource.TryAdd(
                        key: source,
                        value: button
                    );
                }
            }
        }

        var modifierViews = new BindingModifierView[modifiers.Count];

        for (var modifierIndex = 0; (modifierIndex < modifiers.Count); modifierIndex++) {
            var modifier = modifiers[modifierIndex];

            modifierViews[modifierIndex] = new BindingModifierView(
                Icon: modifier.Icon,
                Id: modifier.Id,
                Label: modifier.Label,
                Required: required.Contains(item: modifierIndex),
                Sources: modifier.Sources
            );
        }

        return new BindingPageView(
            Buttons: buttons.ToImmutableArray(),
            ButtonsBySource: buttonsBySource.ToFrozenDictionary(comparer: StringComparer.OrdinalIgnoreCase),
            CommandChords: hints.ToImmutableArray(),
            Group: group,
            Icon: page.Icon,
            Label: page.Label,
            Modifiers: modifierViews.ToImmutableArray(),
            PageId: page.Id
        );
    }
    // Page inheritance is authoring-only. Flattening it here keeps the input fold at one table lookup while giving
    // a modal page source-level overrides instead of forcing it to duplicate a resting page's unrelated controls.
    private static BindingPageDefinition OverlayInheritedPage(BindingPageDefinition inherited, BindingPageDefinition page) {
        var pageEntries = (page.Entries ?? []);
        var claimedSources = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var claimedActivators = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pageEntries) {
            if (entry?.Activator is { } activator) {
                _ = claimedActivators.Add(item: ActivatorIdentity(activator: activator));

                continue;
            }

            foreach (var source in (entry?.Sources ?? [])) {
                _ = claimedSources.Add(item: source);
            }
        }

        var entries = new List<BindingPageEntryDefinition>();

        foreach (var entry in (inherited.Entries ?? [])) {
            if (entry is null) {
                entries.Add(item: entry!);

                continue;
            }

            if (entry.Activator is { } activator) {
                if (!claimedActivators.Contains(item: ActivatorIdentity(activator: activator))) {
                    entries.Add(item: entry);
                }

                continue;
            }

            if (entry.Sources is not { Count: > 0 } sources) {
                entries.Add(item: entry);

                continue;
            }

            var survivingSources = sources.Where(predicate: source => !claimedSources.Contains(item: source)).ToImmutableArray();

            if (survivingSources.Length == 0) {
                continue;
            }

            entries.Add(item: ((survivingSources.Length == sources.Count)
                ? entry
                : (entry with { Sources = survivingSources })));
        }

        entries.AddRange(collection: pageEntries);

        return page with {
            Entries = entries.ToImmutableArray(),
            Inherits = null,
        };
    }
    // A row member resolves to a modifier index: a declared modifier by id, else the declared modifier owning that
    // source, else an implicit single-source digital modifier appended for it. A member may appear once per row.
    private static int[] ResolveMembers(BindingProfileDocument document, string group, IReadOnlyList<string> members, Dictionary<string, int> modifierIndexById, Dictionary<string, int> modifierIndexBySource, List<BindingModifierDefinition> modifiers, int rowIndex, HashSet<int> rowMembers) {
        var resolved = new int[members.Count];

        for (var memberIndex = 0; (memberIndex < members.Count); memberIndex++) {
            var member = members[memberIndex];

            if (string.IsNullOrEmpty(value: member)) {
                throw new ArgumentException(
                    message: $"Chord row {rowIndex} (group \"{group}\") carries an empty member.",
                    paramName: nameof(document)
                );
            }

            if (
                !modifierIndexById.TryGetValue(
                key: member,
                value: out var modifierIndex
            ) &&
                !modifierIndexBySource.TryGetValue(
                key: member,
                value: out modifierIndex
            )
            ) {
                modifierIndex = modifiers.Count;
                modifiers.Add(item: new BindingModifierDefinition(
                    Id: member,
                    Sources: [member]
                ));
                modifierIndexById[member] = modifierIndex;
                modifierIndexBySource[member] = modifierIndex;
            }

            if (!rowMembers.Add(item: modifierIndex)) {
                throw new ArgumentException(
                    message: $"Chord row {rowIndex} (group \"{group}\") lists member \"{member}\" more than once.",
                    paramName: nameof(document)
                );
            }

            resolved[memberIndex] = modifierIndex;
        }

        return resolved;
    }
    private static void ValidateChannelRef(ChannelRef channel, string path, string paramName) {
        switch (channel) {
            case ChannelRef.Name { Value.Length: > 0 }:
                return;
            case ChannelRef.Name:
                throw new ArgumentException(
                    message: $"{path} channel name must be non-empty.",
                    paramName: paramName
                );
            default:
                throw new ArgumentException(
                    message: $"{path} channel reference is not a declared name variant.",
                    paramName: paramName
                );
        }
    }
    // A channel destination's scale must be a finite value in [-1, 1]; an omitted scale is the default (+1) and
    // always valid. The one check both a chord-command channel and a page-entry channel run.
    private static void ValidateChannelScale(float? scale, ChannelRef channel, string path, string paramName) {
        if (
            (scale is { } value) &&
            (!float.IsFinite(f: value) || (value < -1f) || (value > 1f))
        ) {
            throw new ArgumentException(
                message: $"{path} channel {channel.Describe()} scale must be in [-1, 1].",
                paramName: paramName
            );
        }
    }
    private static void ValidateValue(CommandValue? value, string path, bool isChannel, string paramName) {
        if (value is not { } constant) {
            return;
        }
        if (isChannel) {
            throw new ArgumentException(
                message: $"{path} carries a Value on a channel destination; Scale is the channel constant.",
                paramName: paramName
            );
        }
        if (!Enum.IsDefined(value: constant.Kind)) {
            throw new ArgumentException(
                message: $"{path} Value kind {((int)constant.Kind)} is not declared.",
                paramName: paramName
            );
        }
        if (
            !float.IsFinite(f: constant.Raw.X) ||
            !float.IsFinite(f: constant.Raw.Y) ||
            !float.IsFinite(f: constant.Raw.Z) ||
            !float.IsFinite(f: constant.Raw.W)
        ) {
            throw new ArgumentException(
                message: $"{path} Value components must be finite.",
                paramName: paramName
            );
        }
    }
    private static void ValidateTextPayload(string? text, string path, string paramName) {
        if (text is null) {
            return;
        }
        if (string.IsNullOrWhiteSpace(value: text)) {
            throw new ArgumentException(
                message: $"{path} text payload must contain a non-whitespace argument.",
                paramName: paramName
            );
        }
        if (text.Length > MaxTextPayloadLength) {
            throw new ArgumentException(
                message: $"{path} text payload exceeds the {MaxTextPayloadLength}-character bound.",
                paramName: paramName
            );
        }
        if (text.IndexOfAny(anyOf: ['\r', '\n', '\u0085', '\u2028', '\u2029']) >= 0) {
            throw new ArgumentException(
                message: $"{path} text payload must be a single line.",
                paramName: paramName
            );
        }
    }

    // A DocumentIdentifier-typed field can reach a document gate in two shapes that are not an identifier at all:
    // JSON null (the converter is never asked, so the property arrives null) and an unresolved "state.<row>"
    // reference no containing document has bound yet. Reading either through the implicit string conversion throws a
    // NullReferenceException or an InvalidOperationException — past Compile's every caller, all of which catch
    // ArgumentException only, and past BindingVocabularyCheck's promise to answer malformed documents with refusal
    // lines rather than exceptions. Both read identifiers through here and refuse the row by name instead. The catch
    // is the honest shape: DocumentIdentifier publishes no "is resolved" predicate, and a resolved reference keeps
    // its Reference, so the two cannot be told apart from outside. It never runs on a well-formed document.
    internal static string? ResolveIdentifier(DocumentIdentifier? identifier) {
        if (identifier is null) {
            return null;
        }

        try {
            return identifier.Value;
        } catch (InvalidOperationException) {
            return null;
        }
    }

    /// <summary>The synthesized command name a channel destination named <paramref name="channel"/> compiles down to.</summary>
    public static string ChannelCommandName(ChannelRef channel) => channel switch {
        ChannelRef.Name name => $"{ChannelCommandPrefix}name.{name.Value}",
        _ => throw new ArgumentException(
        message: "A channel reference must be a declared name.",
        paramName: nameof(channel)
    ),
    };
    /// <summary>Validates and compiles a profile document.</summary>
    /// <param name="document">The profile document to compile.</param>
    /// <param name="channelCommandName">Optional runtime lowering for an authored channel reference. When omitted,
    /// the portable name-shaped command returned by <see cref="ChannelCommandName"/> is used. A host whose channel
    /// vocabulary changes per seat may instead lower the already-validated name to a fixed ordinal command, keeping
    /// the command registry immutable while the authored document remains name-shaped.</param>
    /// <returns>The compiled profile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="document"/> is invalid.</exception>
    public static CompiledBindingProfile Compile(BindingProfileDocument document, Func<ChannelRef, string>? channelCommandName = null) {
        ArgumentNullException.ThrowIfNull(document);

        channelCommandName ??= ChannelCommandName;

        if (document.Version != BindingProfileDocument.CurrentVersion) {
            throw new ArgumentException(
                message: $"Unsupported binding profile version \"{document.Version}\"; expected \"{BindingProfileDocument.CurrentVersion}\".",
                paramName: nameof(document)
            );
        }

        // Both member lookups are OrdinalIgnoreCase, and they must agree: a member that differs from a declared
        // modifier's id only by case has to RESOLVE to that modifier. Under an Ordinal id lookup it missed, missed
        // the source lookup too (the modifier's sources are its own, not its id), and was minted as an implicit
        // single-source modifier over a control name that does not exist — a permanently dead row, authored in good
        // faith and silently accepted.
        var modifierIndexById = new Dictionary<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
        var modifierIndexBySource = new Dictionary<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
        // Declared modifiers first, then one implicit modifier per raw source a row names (appended in first-use
        // order), so every member resolves to a modifier index.
        var modifiers = new List<BindingModifierDefinition>(collection: (document.Modifiers ?? []));

        for (var modifierIndex = 0; (modifierIndex < modifiers.Count); modifierIndex++) {
            var modifier = modifiers[modifierIndex];

            if (modifier is null) {
                throw new ArgumentException(
                    message: $"Modifier {modifierIndex} is null.",
                    paramName: nameof(document)
                );
            }

            if (string.IsNullOrEmpty(value: modifier.Id)) {
                throw new ArgumentException(
                    message: $"Modifier {modifierIndex} must carry a non-empty id.",
                    paramName: nameof(document)
                );
            }

            if (modifier.Sources is not { Count: > 0 }) {
                throw new ArgumentException(
                    message: $"Modifier \"{modifier.Id}\" must name at least one source.",
                    paramName: nameof(document)
                );
            }

            if (
                !float.IsFinite(f: modifier.PressThreshold) ||
                !float.IsFinite(f: modifier.ReleaseThreshold) ||
                (modifier.ReleaseThreshold > modifier.PressThreshold)
            ) {
                throw new ArgumentException(
                    message: $"Modifier \"{modifier.Id}\" must carry finite thresholds with release at or below press.",
                    paramName: nameof(document)
                );
            }

            if (!modifierIndexById.TryAdd(
                key: modifier.Id,
                value: modifierIndex
            )) {
                throw new ArgumentException(
                    message: $"Modifier {modifierIndex} re-declares id \"{modifier.Id}\" (ids are compared case-insensitively).",
                    paramName: nameof(document)
                );
            }

            var seenModifierSources = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            foreach (var modifierSource in modifier.Sources) {
                if (string.IsNullOrEmpty(value: modifierSource)) {
                    throw new ArgumentException(
                        message: $"Modifier \"{modifier.Id}\" carries an empty source.",
                        paramName: nameof(document)
                    );
                }

                if (!seenModifierSources.Add(item: modifierSource)) {
                    throw new ArgumentException(
                        message: $"Modifier \"{modifier.Id}\" repeats source \"{modifierSource}\".",
                        paramName: nameof(document)
                    );
                }

                if (!modifierIndexBySource.TryAdd(
                    key: modifierSource,
                    value: modifierIndex
                )) {
                    throw new ArgumentException(
                        message: $"Modifiers \"{modifiers[modifierIndexBySource[modifierSource]].Id}\" and \"{modifier.Id}\" share the source \"{modifierSource}\".",
                        paramName: nameof(document)
                    );
                }
            }
        }

        // No chord rows is the empty profile: one anonymous group whose resting page binds nothing, so every slot
        // resolves to a page (an empty one) rather than the group tables having no index to stand on.
        var documentRows = ((document.Chords is { Count: > 0 } chords)
            ? chords
            : [new BindingChordDefinition(
                    Group: EmptyGroup,
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: EmptyGroup,
                        Entries: []
                    )
                )]
        );
        // First pass: group registration, chord resolution, the uniqueness rules, and the raw row facts. Views are
        // built in a second pass so each page view can carry its whole group's command-chord hints.
        var groupIndexByName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var groupNames = new List<string>();
        // Page id → (owning group, chord row) — the uniqueness set AND the wheel section's hold-page resolver.
        var pageRowsById = new Dictionary<string, (int GroupIndex, int RowIndex)>(comparer: StringComparer.Ordinal);
        var restingByGroup = new List<int>();
        var rowChords = new int[documentRows.Count][];
        var rowHelds = new int[documentRows.Count][];
        var seenMemberSets = new Dictionary<string, bool>(comparer: StringComparer.Ordinal);
        var rowGroups = new int[documentRows.Count];
        var seenChordKeys = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
            var row = (documentRows[rowIndex]
                ?? throw new ArgumentException(
                message: $"Chord row {rowIndex} is null.",
                paramName: nameof(document)
            ));

            if (ResolveIdentifier(identifier: row.Group) is not { Length: > 0 } rowGroup) {
                throw new ArgumentException(
                    message: $"Chord row {rowIndex} must name a group (a resolved, non-empty identifier).",
                    paramName: nameof(document)
                );
            }

            if (!groupIndexByName.TryGetValue(
                key: rowGroup,
                value: out var groupIndex
            )) {
                groupIndex = groupNames.Count;
                groupIndexByName[rowGroup] = groupIndex;
                groupNames.Add(item: rowGroup);
                restingByGroup.Add(item: -1);
            }

            rowGroups[rowIndex] = groupIndex;

            var rowMembers = new HashSet<int>();
            var held = ResolveMembers(
                document: document,
                group: rowGroup,
                members: (row.Held ?? []),
                modifierIndexById: modifierIndexById,
                modifierIndexBySource: modifierIndexBySource,
                modifiers: modifiers,
                rowIndex: rowIndex,
                rowMembers: rowMembers
            );
            var chord = ResolveMembers(
                document: document,
                group: rowGroup,
                members: (row.Chord ?? []),
                modifierIndexById: modifierIndexById,
                modifierIndexBySource: modifierIndexBySource,
                modifiers: modifiers,
                rowIndex: rowIndex,
                rowMembers: rowMembers
            );

            Array.Sort(array: held);
            rowChords[rowIndex] = chord;
            rowHelds[rowIndex] = held;

            // Rule 1: exactly one meaning per identity (group, held, chord) — and no ordered path beside an unordered
            // set over the same members, which would both answer one press.
            var identity = $"{groupIndex}\0{string.Join(
                separator: ',',
                values: held
            )}|{string.Join(
                separator: ',',
                values: chord
            )}";
            var memberSet = $"{groupIndex}\0{string.Join(
                separator: ',',
                values: rowMembers.Order()
            )}";
            var chordOnly = (held.Length == 0);

            if (!seenChordKeys.Add(item: identity)) {
                throw new ArgumentException(
                    message: $"Group \"{rowGroup}\" declares two meanings for held [{string.Join(
                        separator: ", ",
                        values: (row.Held ?? [])
                    )}] chord [{string.Join(
                        separator: ", ",
                        values: (row.Chord ?? [])
                    )}] — exactly one meaning per (group, held, chord).",
                    paramName: nameof(document)
                );
            }

            if (seenMemberSets.TryGetValue(
                key: memberSet,
                value: out var priorChordOnly
            )) {
                if (!(chordOnly && priorChordOnly)) {
                    throw new ArgumentException(
                        message: $"Group \"{rowGroup}\" declares two rows over the members [{string.Join(
                            separator: ", ",
                            values: row.Members
                        )}] and at least one is not chord-only — an unordered set and any other row over the same members would answer the same press.",
                        paramName: nameof(document)
                    );
                }
            } else {
                seenMemberSets[memberSet] = chordOnly;
            }

            if ((row.Page is null) == (row.Command is null)) {
                throw new ArgumentException(
                    message: $"Chord row {rowIndex} (group \"{rowGroup}\") must carry exactly one meaning — a page or a command.",
                    paramName: nameof(document)
                );
            }

            if (row.Page is { } page) {
                if (string.IsNullOrEmpty(value: page.Id)) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{rowGroup}\") carries a page with an empty id.",
                        paramName: nameof(document)
                    );
                }

                if (!pageRowsById.TryAdd(
                    key: page.Id,
                    value: (GroupIndex: groupIndex, RowIndex: rowIndex)
                )) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{rowGroup}\") re-declares page id \"{page.Id}\".",
                        paramName: nameof(document)
                    );
                }

                if (
                    (chord.Length == 0) &&
                    (held.Length == 0)
                ) {
                    restingByGroup[groupIndex] = rowIndex;
                }
            } else {
                var chordCommand = row.Command!;

                if (!Enum.IsDefined(value: chordCommand.Mode)) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{rowGroup}\") carries an invalid mode.",
                        paramName: nameof(document)
                    );
                }

                if ((chordCommand.Command is null) == (chordCommand.Channel is null)) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{rowGroup}\") must carry exactly one destination — a command or a channel.",
                        paramName: nameof(document)
                    );
                }

                if (
                    (chordCommand.Text is not null) &&
                    (chordCommand.Channel is not null)
                ) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{rowGroup}\") carries text on a channel destination — text is only meaningful on a command press.",
                        paramName: nameof(document)
                    );
                }

                ValidateTextPayload(
                    text: chordCommand.Text,
                    path: $"Chord row {rowIndex} (group \"{rowGroup}\")",
                    paramName: nameof(document)
                );

                if (
                    (chordCommand.Mode == BindingEntryMode.Toggle) &&
                    (chordCommand.Channel is null)
                ) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{rowGroup}\") sets mode Toggle on a command destination — toggle is only meaningful on a channel destination.",
                        paramName: nameof(document)
                    );
                }

                ValidateValue(
                    value: chordCommand.Value,
                    path: $"Chord row {rowIndex} (group \"{rowGroup}\")",
                    isChannel: (chordCommand.Channel is not null),
                    paramName: nameof(document)
                );

                if (chordCommand.Channel is { } channel) {
                    ValidateChannelRef(
                        channel: channel,
                        path: $"Chord row {rowIndex} (group \"{rowGroup}\")",
                        paramName: nameof(document)
                    );

                    ValidateChannelScale(
                        channel: channel,
                        path: $"Chord row {rowIndex} (group \"{rowGroup}\")",
                        paramName: nameof(document),
                        scale: chordCommand.Scale
                    );
                } else if (string.IsNullOrEmpty(value: chordCommand.Command)) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{rowGroup}\") must name the command or channel it fires.",
                        paramName: nameof(document)
                    );
                }

                // Rule 2's command half: a memberless row has no completion edge — the resting row must be a page.
                if (
                    (chord.Length == 0) &&
                    (held.Length == 0)
                ) {
                    throw new ArgumentException(
                        message: $"Group \"{rowGroup}\" binds a command to the empty chord — the resting row must be a page.",
                        paramName: nameof(document)
                    );
                }
            }
        }

        // Rule 2: exactly one resting page per group (uniqueness is rule 1's empty-chord case; presence is checked here).
        for (var groupIndex = 0; (groupIndex < groupNames.Count); groupIndex++) {
            if (restingByGroup[groupIndex] < 0) {
                throw new ArgumentException(
                    message: $"Group \"{groupNames[groupIndex]}\" has no resting (empty-chord) page.",
                    paramName: nameof(document)
                );
            }
        }

        // Resolve opt-in page inheritance after every page id and owning group is known. Cycles, missing pages,
        // and cross-group inheritance are refused before any runtime table is built.
        var effectivePages = new BindingPageDefinition?[documentRows.Count];
        // Marked while a row sits on the chain currently being walked; a chain that re-enters a marked row is a cycle.
        // A finished row needs no mark of its own — its effective page IS the record that it is done.
        var pageInProgress = new bool[documentRows.Count];

        // Walked as an explicit chain rather than by recursion: an inheritance depth is bounded only by the number of
        // authored pages, so a generated document a few thousand pages deep would have overflowed the stack — an
        // uncatchable process kill — before the cycle refusal below ever got to speak. The descent collects the chain
        // to the first page that inherits nothing (or is already resolved); the ascent applies the overlays outward.
        var chain = new List<int>();

        void ResolvePage(int startRowIndex) {
            chain.Clear();

            var rowIndex = startRowIndex;

            while (effectivePages[rowIndex] is null) {
                if (pageInProgress[rowIndex]) {
                    throw new ArgumentException(
                        message: $"Page inheritance contains a cycle at page \"{documentRows[rowIndex].Page!.Id}\".",
                        paramName: nameof(document)
                    );
                }

                pageInProgress[rowIndex] = true;
                chain.Add(item: rowIndex);

                var page = documentRows[rowIndex].Page!;

                if (page.Inherits is not { Length: > 0 } inheritedId) {
                    if (page.Inherits is not null) {
                        throw new ArgumentException(
                            message: $"Page \"{page.Id}\" carries an empty inherited page id.",
                            paramName: nameof(document)
                        );
                    }

                    effectivePages[rowIndex] = page;

                    break;
                }

                if (
                    !pageRowsById.TryGetValue(
                    key: inheritedId,
                    value: out var inheritedRow
                ) ||
                    (inheritedRow.GroupIndex != rowGroups[rowIndex])
                ) {
                    throw new ArgumentException(
                        message: $"Page \"{page.Id}\" inherits invalid page \"{inheritedId}\"; inherited pages must exist in the same group.",
                        paramName: nameof(document)
                    );
                }

                rowIndex = inheritedRow.RowIndex;
            }

            for (var chainIndex = (chain.Count - 1); (chainIndex >= 0); chainIndex--) {
                var chainRowIndex = chain[chainIndex];

                if (effectivePages[chainRowIndex] is not null) {
                    continue;
                }

                var page = documentRows[chainRowIndex].Page!;

                effectivePages[chainRowIndex] = OverlayInheritedPage(
                    inherited: effectivePages[pageRowsById[page.Inherits!].RowIndex]!,
                    page: page
                );
            }
        }

        for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
            if (documentRows[rowIndex].Page is not null) {
                ResolvePage(startRowIndex: rowIndex);
            }
        }

        // Context rows — the structural half only (shape, key uniqueness, group existence); family/state admission
        // against the engine's published registry is the host's vocabulary gate. Every refusal names the offending row.
        var contextRows = (document.Contexts ?? []);
        var seenContextKeys = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var contextIndex = 0; (contextIndex < contextRows.Count); contextIndex++) {
            var context = (contextRows[contextIndex]
                ?? throw new ArgumentException(
                message: $"Contexts row {contextIndex} is null.",
                paramName: nameof(document)
            ));

            if (string.IsNullOrEmpty(value: context.Family)) {
                throw new ArgumentException(
                    message: $"Contexts row {contextIndex} must name a family.",
                    paramName: nameof(document)
                );
            }

            if (string.IsNullOrEmpty(value: context.State)) {
                throw new ArgumentException(
                    message: $"Contexts row {contextIndex} (family \"{context.Family}\") must name a state.",
                    paramName: nameof(document)
                );
            }

            if (ResolveIdentifier(identifier: context.Group) is not { Length: > 0 } contextGroup) {
                throw new ArgumentException(
                    message: $"Contexts row {contextIndex} (family \"{context.Family}\", state \"{context.State}\") must name a group (a resolved, non-empty identifier).",
                    paramName: nameof(document)
                );
            }

            if (!seenContextKeys.Add(item: $"{context.Family}\0{context.State}")) {
                throw new ArgumentException(
                    message: $"Contexts row {contextIndex} re-declares (family \"{context.Family}\", state \"{context.State}\") — exactly one group per (family, state).",
                    paramName: nameof(document)
                );
            }

            if (!groupIndexByName.ContainsKey(key: contextGroup)) {
                throw new ArgumentException(
                    message: $"Contexts row {contextIndex} (family \"{context.Family}\", state \"{context.State}\") names group \"{contextGroup}\", which no chord row declares.",
                    paramName: nameof(document)
                );
            }
        }

        // Named radial presentations. A group may carry several and each may be selected by several page rows;
        // the row map is still the presenter's allocation-free active lookup. Sectors compile to opaque binding
        // activations rather than console text, preserving the slot's principal and ordinary command-map gates.
        var wheels = (document.Wheels ?? []);
        var wheelViewByRow = new Dictionary<int, BindingWheelView>();
        var wheelIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var wheelIndex = 0; (wheelIndex < wheels.Count); wheelIndex++) {
            var wheel = (wheels[wheelIndex]
                ?? throw new ArgumentException(
                message: $"Wheels row {wheelIndex} is null.",
                paramName: nameof(document)
            ));

            if (
                string.IsNullOrEmpty(value: wheel.Id) ||
                !wheelIds.Add(item: wheel.Id)
            ) {
                throw new ArgumentException(
                    message: $"Wheels row {wheelIndex} id \"{wheel.Id}\" must be non-empty and profile-unique.",
                    paramName: nameof(document)
                );
            }

            if (
                (ResolveIdentifier(identifier: wheel.Group) is not { Length: > 0 } wheelGroup) ||
                !groupIndexByName.TryGetValue(
                key: wheelGroup,
                value: out var wheelGroupIndex
            )
            ) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" must name a group (a resolved, non-empty identifier) that a chord row declares.",
                    paramName: nameof(document)
                );
            }

            var holdPages = (wheel.HoldPages ?? []);

            if (holdPages.Count == 0) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" must name at least one hold page.",
                    paramName: nameof(document)
                );
            }

            var holdRows = new int[holdPages.Count];
            var seenHoldPages = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var holdIndex = 0; (holdIndex < holdPages.Count); holdIndex++) {
                var holdPage = holdPages[holdIndex];

                if (
                    string.IsNullOrEmpty(value: holdPage) ||
                    !seenHoldPages.Add(item: holdPage) ||
                    !pageRowsById.TryGetValue(
                    key: holdPage,
                    value: out var holdRow
                ) ||
                    (holdRow.RowIndex < 0) ||
                    (holdRow.GroupIndex != wheelGroupIndex)
                ) {
                    throw new ArgumentException(
                        message: $"Wheel \"{wheel.Id}\" holds on invalid or repeated page \"{holdPage}\"; every hold page must be a distinct chord-row page of group \"{wheelGroup}\".",
                        paramName: nameof(document)
                    );
                }

                if (wheelViewByRow.ContainsKey(key: holdRow.RowIndex)) {
                    throw new ArgumentException(
                        message: $"Wheel \"{wheel.Id}\" hold page {holdIndex} (\"{holdPage}\") already presents another wheel.",
                        paramName: nameof(document)
                    );
                }

                holdRows[holdIndex] = holdRow.RowIndex;
            }

            var authoredStyle = (wheel.Style ?? new BindingWheelStyleDefinition());
            var style = authoredStyle with {
                Excursion = ((authoredStyle.Excursion is { } authoredExcursion)
                ? authoredExcursion with {
                    Thresholds = ((authoredExcursion.Thresholds is null)
                    ? null!
                    : authoredExcursion.Thresholds.ToImmutableArray()),
                }
                : null),
            };

            if (
                !Enum.IsDefined(value: style.PointerSelection) ||
                !Enum.IsDefined(value: style.Placement) ||
                !Enum.IsDefined(value: style.RingSelection)
            ) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" carries an invalid selection or placement policy.",
                    paramName: nameof(document)
                );
            }

            if (
                !float.IsFinite(f: style.DeadZoneFraction) ||
                (style.DeadZoneFraction < 0f) ||
                (style.DeadZoneFraction >= 0.5f) ||
                !float.IsFinite(f: style.RingWidthFraction) ||
                (style.RingWidthFraction <= 0f) ||
                (style.RingWidthFraction >= 0.5f) ||
                !float.IsFinite(f: style.OuterGraceRingFraction) ||
                (style.OuterGraceRingFraction < 0f) ||
                !float.IsFinite(f: style.SectorOffset)
            ) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" carries invalid style geometry.",
                    paramName: nameof(document)
                );
            }

            if ((style.SectorOffset < 0f) || (style.SectorOffset >= 1f)) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" sectorOffset {style.SectorOffset} is outside [0, 1): a whole sector of rotation is an entry reorder — move the entry instead.",
                    paramName: nameof(document)
                );
            }

            if (
                !float.IsFinite(f: style.AxisDeadZone) ||
                (style.AxisDeadZone < 0f) ||
                (style.AxisDeadZone >= 1f) ||
                !float.IsFinite(f: style.SelectionGraceSeconds) ||
                (style.SelectionGraceSeconds < 0f) ||
                !float.IsFinite(f: style.SwitchFraction) ||
                (style.SwitchFraction < 0f) ||
                (style.SwitchFraction > 1f) ||
                !float.IsFinite(f: style.FadeOutSeconds) ||
                (style.FadeOutSeconds < 0f) ||
                !float.IsFinite(f: style.FadeOutEase) ||
                (style.FadeOutEase <= 0f)
            ) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" carries invalid selector thresholds, timing, or fade.",
                    paramName: nameof(document)
                );
            }

            var ringCount = (wheel.Rings?.Count ?? 0);

            if (
                (ringCount < BindingWheelDefinition.MinRings) ||
                (ringCount > BindingWheelDefinition.MaxRings)
            ) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" declares {ringCount} rings; a wheel presents {BindingWheelDefinition.MinRings}..{BindingWheelDefinition.MaxRings}.",
                    paramName: nameof(document)
                );
            }

            if (
                (style.InitialRing < 0) ||
                (style.InitialRing >= ringCount)
            ) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" initial ring {style.InitialRing} is outside its {ringCount} rings.",
                    paramName: nameof(document)
                );
            }

            if ((style.DeadZoneFraction + ((ringCount + style.OuterGraceRingFraction) * style.RingWidthFraction)) >= 0.5f) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" style extends beyond half of the seat's smaller viewport extent.",
                    paramName: nameof(document)
                );
            }

            BindingWheelExcursionView? excursionView = null;

            if (style.RingSelection == BindingWheelRingSelectionMode.Explicit) {
                if (style.Excursion is not null) {
                    throw new ArgumentException(
                        message: $"Wheel \"{wheel.Id}\" carries excursion ranges while ring selection is Explicit.",
                        paramName: nameof(document)
                    );
                }
            } else {
                var excursion = (style.Excursion
                    ?? throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" selects rings by Excursion but declares no excursion policy.",
                    paramName: nameof(document)
                ));
                var thresholds = (excursion.Thresholds
                    ?? throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" excursion policy declares null thresholds.",
                    paramName: nameof(document)
                ));

                if (
                    !float.IsFinite(f: excursion.DeadZone) ||
                    (excursion.DeadZone < 0f) ||
                    !float.IsFinite(f: excursion.SpatialTravelFraction) ||
                    (excursion.SpatialTravelFraction <= 0f) ||
                    (excursion.SpatialTravelFraction > 1f) ||
                    !float.IsFinite(f: excursion.Hysteresis) ||
                    (excursion.Hysteresis < 0f)
                ) {
                    throw new ArgumentException(
                        message: $"Wheel \"{wheel.Id}\" carries invalid excursion geometry.",
                        paramName: nameof(document)
                    );
                }

                if (thresholds.Count != (ringCount - 1)) {
                    throw new ArgumentException(
                        message: $"Wheel \"{wheel.Id}\" has {ringCount} rings but {thresholds.Count} excursion thresholds; exactly {(ringCount - 1)} boundaries are required.",
                        paramName: nameof(document)
                    );
                }

                var thresholdSquares = new float[thresholds.Count];
                var outwardSquares = new float[thresholds.Count];
                var inwardSquares = new float[thresholds.Count];
                var previous = excursion.DeadZone;

                for (var thresholdIndex = 0; (thresholdIndex < thresholds.Count); thresholdIndex++) {
                    var threshold = thresholds[thresholdIndex];

                    if (
                        !float.IsFinite(f: threshold) ||
                        (threshold <= previous) ||
                        ((threshold - previous) <= (2f * excursion.Hysteresis))
                    ) {
                        throw new ArgumentException(
                            message: $"Wheel \"{wheel.Id}\" excursion threshold {thresholdIndex} must be finite, strictly ascending from the dead zone, and leave room for twice the authored hysteresis.",
                            paramName: nameof(document)
                        );
                    }

                    thresholdSquares[thresholdIndex] = (threshold * threshold);
                    outwardSquares[thresholdIndex] = ((threshold + excursion.Hysteresis) * (threshold + excursion.Hysteresis));
                    inwardSquares[thresholdIndex] = ((threshold - excursion.Hysteresis) * (threshold - excursion.Hysteresis));
                    previous = threshold;
                }

                excursionView = new BindingWheelExcursionView(
                    DeadZoneSquared: (excursion.DeadZone * excursion.DeadZone),
                    ThresholdsSquared: thresholdSquares.ToImmutableArray(),
                    OutwardThresholdsSquared: outwardSquares.ToImmutableArray(),
                    InwardThresholdsSquared: inwardSquares.ToImmutableArray(),
                    SpatialTravelFraction: excursion.SpatialTravelFraction
                );
            }

            var ringViews = new BindingWheelRingView[ringCount];

            for (var ringIndex = 0; (ringIndex < ringCount); ringIndex++) {
                var ring = (wheel.Rings![ringIndex]
                    ?? throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" ring {ringIndex} is null.",
                    paramName: nameof(document)
                ));

                if (string.IsNullOrEmpty(value: ring.Id)) {
                    throw new ArgumentException(
                        message: $"Wheel \"{wheel.Id}\" ring {ringIndex} must carry a page id.",
                        paramName: nameof(document)
                    );
                }

                // Ring pages share the document-wide page-id namespace (they ARE pages) — but hold no chord row,
                // so they enter the map with a sentinel row that nothing resolves.
                if (!pageRowsById.TryAdd(
                    key: ring.Id,
                    value: (GroupIndex: wheelGroupIndex, RowIndex: -1)
                )) {
                    throw new ArgumentException(
                        message: $"Wheel \"{wheel.Id}\" ring {ringIndex} re-declares page id \"{ring.Id}\".",
                        paramName: nameof(document)
                    );
                }

                var sectorCount = (ring.Entries?.Count ?? 0);

                if (
                    (sectorCount < BindingWheelDefinition.MinSectorsPerRing) ||
                    (sectorCount > BindingWheelDefinition.MaxSectorsPerRing)
                ) {
                    throw new ArgumentException(
                        message: $"Wheel ring \"{ring.Id}\" (group \"{wheelGroup}\") declares {sectorCount} sectors; a ring presents {BindingWheelDefinition.MinSectorsPerRing}..{BindingWheelDefinition.MaxSectorsPerRing}.",
                        paramName: nameof(document)
                    );
                }

                var sectorViews = new BindingWheelSectorView[sectorCount];
                var seenSectorIds = new HashSet<string>(comparer: StringComparer.Ordinal);

                for (var sectorIndex = 0; (sectorIndex < sectorCount); sectorIndex++) {
                    var sector = (ring.Entries![sectorIndex]
                        ?? throw new ArgumentException(
                        message: $"Wheel ring \"{ring.Id}\" (group \"{wheelGroup}\") sector {sectorIndex} is null.",
                        paramName: nameof(document)
                    ));
                    var sectorPath = $"Wheel ring \"{ring.Id}\" (group \"{wheelGroup}\") sector {sectorIndex}";

                    if (
                        (sector.Id is { } sectorId) &&
                        (
                            (sectorId.Length == 0) ||
                            !seenSectorIds.Add(item: sectorId)
                        )
                    ) {
                        throw new ArgumentException(
                            message: $"{sectorPath} id \"{sectorId}\" must be non-empty and unique within the ring page.",
                            paramName: nameof(document)
                        );
                    }

                    if (string.IsNullOrEmpty(value: sector.Command)) {
                        throw new ArgumentException(
                            message: $"{sectorPath} must name the command it commits.",
                            paramName: nameof(document)
                        );
                    }

                    // The narrowed sector shape — each foreign member refused BY NAME rather than ignored, so an
                    // authored field never silently means nothing.
                    if (sector.Sources is { Count: > 0 }) {
                        throw new ArgumentException(
                            message: $"{sectorPath} carries a source — the radial gesture is a sector's trigger.",
                            paramName: nameof(document)
                        );
                    }
                    if (sector.Activator is not null) {
                        throw new ArgumentException(
                            message: $"{sectorPath} carries an activator — the radial gesture is a sector's trigger.",
                            paramName: nameof(document)
                        );
                    }
                    if (sector.Channel is not null) {
                        throw new ArgumentException(
                            message: $"{sectorPath} carries a channel destination — a one-shot radial activation targets a command, never a folded channel.",
                            paramName: nameof(document)
                        );
                    }
                    if (sector.Scale is not null) {
                        throw new ArgumentException(
                            message: $"{sectorPath} carries a scale — a sector has no channel to scale.",
                            paramName: nameof(document)
                        );
                    }
                    if (sector.Mode != BindingEntryMode.Hold) {
                        throw new ArgumentException(
                            message: $"{sectorPath} sets mode {sector.Mode} — a sector carries no held state.",
                            paramName: nameof(document)
                        );
                    }

                    if (
                        !Enum.IsDefined(value: sector.Mode) ||
                        ((sector.ActivateOn is { } sectorPhase) && !Enum.IsDefined(value: sectorPhase))
                    ) {
                        throw new ArgumentException(
                            message: $"{sectorPath} carries an invalid mode or activation phase.",
                            paramName: nameof(document)
                        );
                    }

                    ValidateValue(
                        value: sector.Value,
                        path: sectorPath,
                        isChannel: false,
                        paramName: nameof(document)
                    );
                    var phase = (sector.ActivateOn ?? CommandPhase.Started);
                    var value = (sector.Value ?? ((phase is CommandPhase.Completed or CommandPhase.Canceled)
                        ? CommandValue.Digital(active: false)
                        : CommandValue.Digital(active: true)));

                    sectorViews[sectorIndex] = new BindingWheelSectorView(
                        Activation: new BindingActivation(
                            command: sector.Command!,
                            value: value,
                            phase: phase,
                            text: sector.Text
                        ),
                        Id: sector.Id
                    );
                }

                ringViews[ringIndex] = new BindingWheelRingView(
                    PageId: ring.Id,
                    Label: ring.Label,
                    Sectors: sectorViews.ToImmutableArray()
                );
            }

            var view = new BindingWheelView(
                Id: wheel.Id,
                Group: wheelGroup,
                LabelRow: wheel.LabelRow,
                IconRow: wheel.IconRow,
                HoldPageIds: holdPages.ToImmutableArray(),
                Rings: ringViews.ToImmutableArray(),
                Style: style,
                Excursion: excursionView,
                SelectorDeadZoneSquared: (excursionView?.DeadZoneSquared ?? (style.AxisDeadZone * style.AxisDeadZone)),
                SelectorSwitchThresholdSquared: (style.SwitchFraction * style.SwitchFraction)
            );

            foreach (var holdRow in holdRows) {
                wheelViewByRow[holdRow] = view;
            }
        }

        // Second pass: the per-group command-chord hint lists (shared by every page view of the group), then the rows.
        var commandRowsByGroup = new int[groupNames.Count][];
        var hintsByGroup = new IReadOnlyList<BindingChordCommandView>[groupNames.Count];

        for (var groupIndex = 0; (groupIndex < groupNames.Count); groupIndex++) {
            var commandRows = new List<int>();
            var hints = new List<BindingChordCommandView>();

            for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
                if (
                    (rowGroups[rowIndex] != groupIndex) ||
                    (documentRows[rowIndex].Command is not { } command)
                ) {
                    continue;
                }

                var effectiveCommand = ((command.Channel is { } channel)
                    ? channelCommandName(arg: channel)
                    : command.Command!
                );

                commandRows.Add(item: rowIndex);
                hints.Add(item: new BindingChordCommandView(
                    Chord: rowChords[rowIndex].Select(selector: index => modifiers[index].Id).ToImmutableArray(),
                    Command: effectiveCommand,
                    Held: rowHelds[rowIndex].Select(selector: index => modifiers[index].Id).ToImmutableArray(),
                    HoldRelease: command.HoldRelease,
                    Icon: command.Icon,
                    Label: command.Label,
                    Sources: rowHelds[rowIndex].Concat(second: rowChords[rowIndex]).Select(selector: index => modifiers[index].Sources[0]).ToImmutableArray()
                ));
            }

            commandRowsByGroup[groupIndex] = [.. commandRows];
            hintsByGroup[groupIndex] = hints.ToImmutableArray();
        }

        var rows = new CompiledBindingProfile.CompiledChordRow[documentRows.Count];
        var nextActivatorIndex = 0;

        for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
            var row = documentRows[rowIndex];
            var chord = rowChords[rowIndex];
            var groupIndex = rowGroups[rowIndex];

            if (effectivePages[rowIndex] is { } page) {
                var (table, activators) = BuildTable(
                    channelCommandName: channelCommandName,
                    nextActivatorIndex: ref nextActivatorIndex,
                    page: page
                );

                rows[rowIndex] = new CompiledBindingProfile.CompiledChordRow(
                    Chord: chord,
                    Command: null,
                    GroupIndex: groupIndex,
                    Held: rowHelds[rowIndex],
                    Table: table,
                    Activators: ((activators.Count > 0)
                    ? activators.ToImmutableArray()
                    : null),
                    View: BuildView(
                        group: groupNames[groupIndex],
                        hints: hintsByGroup[groupIndex],
                        modifiers: modifiers,
                        page: page,
                        // Held AND chord: both lists are members the player must be holding for this page to be the
                        // selected one, so both belong in the "required" set the bar renders chips from. Passing the
                        // chord alone left a page selected by an unordered hold rendering no held-modifier chip at all.
                        required: [.. rowHelds[rowIndex], .. chord],
                        channelCommandName: channelCommandName
                    )
                );
            } else {
                var command = row.Command!;
                var isChannel = (command.Channel is not null);
                var pressValue = CompiledBindingProfile.PressValue(
                    channelScale: (isChannel
                    ? (command.Scale ?? 1f)
                    : null),
                    explicitValue: command.Value
                );
                var effectiveCommand = (isChannel
                    ? channelCommandName(arg: command.Channel!)
                    : command.Command!
                );

                rows[rowIndex] = new CompiledBindingProfile.CompiledChordRow(
                    Chord: chord,
                    Held: rowHelds[rowIndex],
                    Command: new CompiledBindingProfile.CompiledCommandEdge(
                        Command: effectiveCommand,
                        // The same rule the activator path states above, for the same reason: a CHANNEL destination
                        // must dispatch its release whatever HoldRelease says, because only the channel verb's handler
                        // frees the channel and CommandRegistry.ApplySnapshot drops any edge whose Dispatch is false.
                        // A Hold-mode channel row left at the default holdRelease:false emitted its break edge with
                        // Dispatch:false and latched the channel on forever. A command destination keeps HoldRelease's
                        // own default (momentary; nothing to free).
                        DispatchRelease: (command.HoldRelease || isChannel),
                        PressValue: pressValue,
                        ReleaseValue: CommandValue.Inactive(kind: pressValue.Kind),
                        Reassertable: (isChannel && (command.Mode == BindingEntryMode.Hold)),
                        Mode: command.Mode,
                        Source: BindingSourceIdentity.ForCommand(command: effectiveCommand),
                        Text: command.Text
                    ),
                    GroupIndex: groupIndex,
                    Table: null,
                    View: null
                );
            }
        }

        var pageRowsByGroup = new int[groupNames.Count][];

        for (var groupIndex = 0; (groupIndex < groupNames.Count); groupIndex++) {
            var pageRows = new List<int>();

            for (var rowIndex = 0; (rowIndex < rows.Length); rowIndex++) {
                if (
                    (rowGroups[rowIndex] == groupIndex) &&
                    (rows[rowIndex].Table is not null)
                ) {
                    pageRows.Add(item: rowIndex);
                }
            }

            pageRowsByGroup[groupIndex] = [.. pageRows];
        }

        return new CompiledBindingProfile(
            activatorCount: nextActivatorIndex,
            commandRowsByGroup: commandRowsByGroup,
            groupIndexByName: groupIndexByName,
            groups: [.. groupNames],
            modifierIndexBySource: modifierIndexBySource,
            modifiers: modifiers,
            pageRowsByGroup: pageRowsByGroup,
            restingRowByGroup: [.. restingByGroup],
            rows: rows,
            wheelViewByRow: wheelViewByRow
        );
    }

}
