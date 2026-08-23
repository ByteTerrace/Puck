namespace Puck.Commands;

/// <summary>
/// Validates a binding document's command references against the live affordance vocabulary — the check that turns a
/// typo'd command name from a silently dead key into a loud refusal, and an authority verb named as a binding
/// destination into a refused page. <see cref="BindingProfile.Compile"/> is the structural gate (row shapes,
/// uniqueness, chord well-formedness) and deliberately knows nothing about which commands exist; this is the
/// vocabulary gate beside it, fed by the host's <see cref="CommandRegistry"/> through plain lookups so it can run
/// anywhere a document enters (a live rebind, a recompose, a document validator) without coupling the document types
/// to the registry.
/// </summary>
/// <remarks>Each of the three lookups is independently optional, and a caller missing one still gets the others: a
/// caller with no registry passes a null <c>command</c> and keeps the channel checks; a caller with no channel table
/// passes null <c>channel</c> and keeps the command checks. Nothing here couples one half's absence to the other's.
/// <para>Four predicates, all per reference: the command must exist (an entry naming an unregistered command is
/// exactly the binding the <see cref="InputRouter"/> silently drops at resolve time today); it must be
/// <see cref="CommandBindability.Bindable"/> (an authority verb reached from a page would be an escalation the grant
/// table never sees, because the page — not the principal — chose the destination); a row carrying authored text must
/// target a command whose <see cref="CommandMetadata.AcceptsWireArgs"/> is true; and the value kind the binding
/// dispatches — its constant <c>Value</c> when it carries one, else the physical source's declared kind when the
/// caller can resolve it — must equal the command's declared <see cref="CommandMetadata.ValueKind"/>. A text-bearing
/// ordinary source must also be digital because analog samples carry no press edge, and so must every step of a
/// <see cref="BindingActivatorMode.Tapped"/> activator's sequence. A source the caller's catalog
/// cannot resolve (its lookup answering <see langword="null"/>) skips the source-kind half only: existence and
/// eligibility are always checked. Empty sources/commands are skipped entirely — they are the structural gate's
/// findings, not this one's.</para></remarks>
public static class BindingVocabularyCheck {
    private static void CheckChannel(
        ChannelRef channel,
        Func<ChannelRef, bool>? channelBinary,
        Func<ChannelRef, bool>? channelExists,
        List<string> errors,
        string prefix,
        float? scale
    ) {
        if (
            (channelExists is not null) &&
            !channelExists(arg: channel)
        ) {
            errors.Add(item: $"{prefix} {channel.Describe()}, which resolves no declared channel");
        } else if (
            (scale is { } value) &&
            (value != 1f) &&
            (channelBinary?.Invoke(arg: channel) ?? false)
        ) {
            errors.Add(item: $"{prefix} {channel.Describe()} with scale {value}, but a binary channel's scale is always the default (+1)");
        }
    }
    private static void CheckCommand(
        Func<string, CommandMetadata?> command,
        string commandName,
        CommandValueKind? dispatched,
        List<string> errors,
        string unknownError,
        string unbindableError,
        Func<CommandValueKind, CommandValueKind, string> mismatchError,
        bool requiresWireArgs = false,
        string? wireArgsError = null
    ) {
        if (command(arg: commandName) is not { } declared) {
            errors.Add(item: unknownError);
        } else if (declared.Bindability != CommandBindability.Bindable) {
            errors.Add(item: unbindableError);
        } else if (
            requiresWireArgs &&
            !declared.AcceptsWireArgs
        ) {
            errors.Add(item: wireArgsError!);
        } else if (
            (dispatched is { } actual) &&
            (actual != declared.ValueKind)
        ) {
            errors.Add(item: mismatchError(
                arg1: actual,
                arg2: declared.ValueKind
            ));
        }
    }
    private static string Word(CommandValueKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>Appends one error per unresolvable, unbindable, argument-incompatible, or kind-mismatched command or
    /// channel reference in <paramref name="document"/>.</summary>
    /// <param name="document">The binding document to check.</param>
    /// <param name="command">Resolves a command name (or alias) to its declared facts, answering <see langword="null"/>
    /// when no such command is registered — typically <see cref="CommandRegistry.TryGetMetadata"/>. Pass
    /// <see langword="null"/> to skip the command half entirely (a caller with no registry — an offline rehydrator, a
    /// pre-container boot parse); the channel and kind halves still run, so a caller that cannot check commands never
    /// has to abandon the checks it can make.</param>
    /// <param name="sourceKind">Resolves a physical source id to its declared value kind, or <see langword="null"/>
    /// when the source is unknown to the caller's catalog (the kind check is then skipped for that entry); pass
    /// <see langword="null"/> to skip source-kind resolution entirely.</param>
    /// <param name="errors">The list refusal lines are appended to.</param>
    /// <param name="channel">Resolves a declared channel name (a second, world-owned vocabulary a binding destination
    /// may name instead of a command — see <see cref="BindingPageEntryDefinition.Channel"/>), or <see langword="null"/>
    /// to skip channel-name resolution entirely (a caller with no channel table). A name this resolves
    /// <see langword="false"/> for gets the channel twin of the "names no registered command" refusal.</param>
    /// <param name="channelBinary">Resolves a declared channel name to whether its shape is binary, or
    /// <see langword="null"/> to skip the shape check entirely (a caller with no channel table). A binary channel's
    /// scale is always the default (<c>+1</c>, or an omitted <see cref="BindingPageEntryDefinition.Scale"/>) —
    /// <see cref="BindingPageEntryDefinition.Scale"/>'s own doc names this rule; this is where it is enforced. Only
    /// consulted for a channel <paramref name="channel"/> has already confirmed exists.</param>
    /// <param name="sourceAddressable">Optionally identifies declared sources that cannot be authored as binding
    /// controls despite carrying a known value kind, such as the text-payload source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="errors"/> is
    /// <see langword="null"/>.</exception>
    public static void Validate(
        BindingProfileDocument document,
        Func<string, CommandMetadata?>? command,
        Func<string, CommandValueKind?>? sourceKind,
        List<string> errors,
        Func<ChannelRef, bool>? channel = null,
        Func<ChannelRef, bool>? channelBinary = null,
        Func<string, bool>? sourceAddressable = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: errors);

        var declaredModifierIds = new HashSet<string>(
            collection: (document.Modifiers ?? []).Select(selector: static modifier => modifier.Id),
            comparer: StringComparer.Ordinal
        );

        foreach (var row in (document.Chords ?? [])) {
            if (row is null) {
                continue;
            }

            if (row.Page is { } page) {
                var entries = (page.Entries ?? []);

                for (var entryIndex = 0; (entryIndex < entries.Count); entryIndex++) {
                    var entry = entries[entryIndex];

                    if (entry is null) {
                        errors.Add(item: $"page \"{page.Id}\" entry {entryIndex} is null");

                        continue;
                    }

                    if (
                        (entry.Sources is not { Count: > 0 }) &&
                        (entry.Activator is null)
                    ) {
                        continue;
                    }

                    // An activator entry's own vocabulary half: every sequence member must name a REAL control —
                    // sourceKind answering null is exactly "unknown control name", the refusal this check exists
                    // to catch (BindingProfile.Compile knows nothing of the physical vocabulary; this is where it
                    // is enforced). Falls through to the same channel/command checks below, keyed by the entry's
                    // resolved label instead of (nonexistent) Sources.
                    if (
                        (entry.Activator is { } activator) &&
                        (sourceKind is not null)
                    ) {
                        foreach (var step in (activator.Sequence ?? [])) {
                            if (string.IsNullOrEmpty(value: step)) {
                                continue;
                            }

                            if (!(sourceAddressable?.Invoke(arg: step) ?? true)) {
                                errors.Add(item: $"page \"{page.Id}\" activator [{string.Join(
                                    separator: ", ",
                                    values: (activator.Sequence ?? [])
                                )}] names unaddressable control \"{step}\"");
                            } else if (sourceKind(arg: step) is not { } stepKind) {
                                errors.Add(item: $"page \"{page.Id}\" activator [{string.Join(
                                    separator: ", ",
                                    values: (activator.Sequence ?? [])
                                )}] names unknown control \"{step}\"");
                            } else if (
                                (activator.Mode == BindingActivatorMode.Tapped) &&
                                (stepKind != CommandValueKind.Digital)
                            ) {
                                // RowActivatorTracker.ApplyTapped advances only on CommandPhase.Started; analog
                                // sources (triggers, sticks, gyro) emit Active/Completed only, so a tapped step
                                // naming one can never be satisfied. Held is unaffected: HeldOrderTracker latches
                                // on the analog value crossing its threshold.
                                errors.Add(item: $"page \"{page.Id}\" tapped activator [{string.Join(
                                    separator: ", ",
                                    values: (activator.Sequence ?? [])
                                )}] names {Word(kind: stepKind)} control \"{step}\", which emits no press edge");
                            }
                        }
                    }

                    var label = entry.TriggerLabel;

                    foreach (var rawSource in (entry.Sources ?? [])) {
                        if (!(sourceAddressable?.Invoke(arg: rawSource) ?? true)) {
                            errors.Add(item: $"page \"{page.Id}\" binds unaddressable control \"{rawSource}\"");
                        }

                        // An axis-COMPONENT source's vocabulary half: the BASE control (with the .x/.y suffix
                        // parsed off — BindingProfile.Compile already refused a malformed suffix structurally) must
                        // name a real, two-dimensional control. An unresolvable base is "unknown control name"; a
                        // resolvable but non-Axis2D base is a distinct "malformed axis component" finding.
                        if (
                            (sourceKind is not null) &&
                            BindingSourceComponent.TrySplit(
                            baseSource: out var baseSource,
                            component: out var component,
                            source: rawSource
                        ) &&
                            (component is not null)
                        ) {
                            var baseKind = sourceKind(arg: baseSource);

                            if (baseKind is null) {
                                errors.Add(item: $"page \"{page.Id}\" binds {rawSource} to an axis component, but \"{baseSource}\" names unknown control");
                            } else if (baseKind != CommandValueKind.Axis2D) {
                                errors.Add(item: $"page \"{page.Id}\" binds {rawSource} to an axis component, but \"{baseSource}\" is not a two-dimensional axis control");
                            }
                        }

                        if (
                            (entry.Text is not null) &&
                            (sourceKind?.Invoke(arg: rawSource) is { } textSourceKind) &&
                            (textSourceKind != CommandValueKind.Digital)
                        ) {
                            errors.Add(item: $"page \"{page.Id}\" binds text arguments to {rawSource}, but a {Word(kind: textSourceKind)} source has no press edge");
                        }
                    }

                    if (entry.Channel is { } channelRef) {
                        CheckChannel(
                            channel: channelRef,
                            channelBinary: channelBinary,
                            channelExists: channel,
                            errors: errors,
                            prefix: $"page \"{page.Id}\" binds {label} to channel",
                            scale: entry.Scale
                        );

                        continue;
                    }

                    if (
                        string.IsNullOrEmpty(value: entry.Command) ||
                        (command is null)
                    ) {
                        continue;
                    }

                    // A constant Value or an activator's synthesized press value dispatches the SAME kind
                    // regardless of which source triggers the row, so one check suffices; an ordinary sourced row's
                    // dispatched kind is each source's OWN declared kind, checked per source so a row combining
                    // sources of different physical kinds is caught.
                    if (
                        (entry.Value?.Kind ?? ((entry.Activator is not null)
                        ? CompiledBindingProfile.PressValue(
                            channelScale: null,
                            explicitValue: null
                        ).Kind
                        : (CommandValueKind?)null)) is { } fixedDispatch
                    ) {
                        CheckCommand(
                            command: command,
                            commandName: entry.Command,
                            dispatched: fixedDispatch,
                            errors: errors,
                            unknownError: $"page \"{page.Id}\" binds {label} to \"{entry.Command}\", which names no registered command",
                            unbindableError: $"page \"{page.Id}\" binds {label} to \"{entry.Command}\", which is not bindable",
                            mismatchError: (actual, declared) => $"page \"{page.Id}\" sends {Word(kind: actual)} from {label} to \"{entry.Command}\", which takes {Word(kind: declared)}",
                            requiresWireArgs: (entry.Text is not null),
                            wireArgsError: $"page \"{page.Id}\" binds text arguments to \"{entry.Command}\", which accepts no wire arguments"
                        );
                    } else {
                        foreach (var dispatchSource in (entry.Sources ?? [])) {
                            CheckCommand(
                                command: command,
                                commandName: entry.Command,
                                dispatched: sourceKind?.Invoke(arg: dispatchSource),
                                errors: errors,
                                unknownError: $"page \"{page.Id}\" binds {label} to \"{entry.Command}\", which names no registered command",
                                unbindableError: $"page \"{page.Id}\" binds {label} to \"{entry.Command}\", which is not bindable",
                                mismatchError: (actual, declared) => $"page \"{page.Id}\" sends {Word(kind: actual)} from {dispatchSource} to \"{entry.Command}\", which takes {Word(kind: declared)}",
                                requiresWireArgs: (entry.Text is not null),
                                wireArgsError: $"page \"{page.Id}\" binds text arguments to \"{entry.Command}\", which accepts no wire arguments"
                            );
                        }
                    }
                }
            }

            foreach (var member in row.Members) {
                if (
                    !string.IsNullOrEmpty(value: member) &&
                    !declaredModifierIds.Contains(item: member) &&
                    !(sourceAddressable?.Invoke(arg: member) ?? true)
                ) {
                    errors.Add(item: $"row [{string.Join(
                        separator: '+',
                        values: row.Members
                    )}] (group \"{row.Group}\") names \"{member}\", which is neither a declared modifier nor an addressable control");
                }
            }

            if (row.Command is { } chordCommand) {
                if (chordCommand.Channel is { } chordChannel) {
                    CheckChannel(
                        channel: chordChannel,
                        channelBinary: channelBinary,
                        channelExists: channel,
                        errors: errors,
                        prefix: $"row [{string.Join(
                            separator: '+',
                            values: row.Members
                        )}] (group \"{row.Group}\") folds into channel",
                        scale: chordCommand.Scale
                    );

                    continue;
                }

                if (
                    string.IsNullOrEmpty(value: chordCommand.Command) ||
                    (command is null)
                ) {
                    continue;
                }

                var chordLabel = $"row [{string.Join(
                    separator: '+',
                    values: row.Members
                )}] (group \"{row.Group}\")";
                // A command-meaning chord (a channel one continues above) dispatches the same press value
                // BindingProfile.Compile builds; validate the kind it will actually send.
                var pressed = CompiledBindingProfile.PressValue(
                    channelScale: null,
                    explicitValue: chordCommand.Value
                ).Kind;

                CheckCommand(
                    command: command,
                    commandName: chordCommand.Command,
                    dispatched: pressed,
                    errors: errors,
                    unknownError: $"{chordLabel} fires \"{chordCommand.Command}\", which names no registered command",
                    unbindableError: $"{chordLabel} fires \"{chordCommand.Command}\", which is not bindable",
                    mismatchError: (actual, declared) => $"{chordLabel} sends {Word(kind: actual)} to \"{chordCommand.Command}\", which takes {Word(kind: declared)}",
                    requiresWireArgs: (chordCommand.Text is not null),
                    wireArgsError: $"{chordLabel} binds text arguments to \"{chordCommand.Command}\", which accepts no wire arguments"
                );
            }
        }

        if (command is null) {
            return;
        }

        // Radial sectors are ordinary compiled binding activations. They therefore obey the same existence,
        // bindability, and value-kind contract as a physical page entry; only the trigger is supplied by the
        // presenter's sector gesture rather than a provider source.
        foreach (var wheel in (document.Wheels ?? [])) {
            foreach (var ring in (wheel?.Rings ?? [])) {
                foreach (var sector in (ring?.Entries ?? [])) {
                    if (sector is null) {
                        errors.Add(item: $"wheel \"{wheel!.Id}\" ring \"{ring!.Id}\" carries a null sector");

                        continue;
                    }

                    if (string.IsNullOrEmpty(value: sector.Command)) {
                        continue;
                    }

                    var sectorKind = CompiledBindingProfile.PressValue(
                        channelScale: null,
                        explicitValue: sector.Value
                    ).Kind;

                    CheckCommand(
                        command: command,
                        commandName: sector.Command,
                        dispatched: sectorKind,
                        errors: errors,
                        unknownError: $"wheel \"{wheel!.Id}\" ring \"{ring!.Id}\" commits \"{sector.Command}\", which names no registered command",
                        unbindableError: $"wheel \"{wheel!.Id}\" ring \"{ring!.Id}\" commits \"{sector.Command}\", which is not bindable",
                        mismatchError: (actual, declared) => $"wheel \"{wheel!.Id}\" ring \"{ring!.Id}\" sends {Word(kind: actual)} to \"{sector.Command}\", which takes {Word(kind: declared)}"
                    );
                }
            }
        }
    }

}
