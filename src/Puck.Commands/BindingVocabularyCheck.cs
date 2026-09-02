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
/// <remarks>The vocabularies come in as one <see cref="BindingVocabularyLookups"/>, each independently optional; the
/// refusals go out as one <see cref="BindingVocabularyReport"/>, never appended to state the caller already holds.
/// <para>Four predicates, all per reference: the command must exist (an entry naming an unregistered command is
/// exactly the binding the <see cref="InputRouter"/> silently drops at resolve time today); it must be
/// <see cref="CommandBindability.Bindable"/> (an authority verb reached from a page would be an escalation the grant
/// table never sees, because the page — not the principal — chose the destination); a row carrying authored text must
/// target a command whose <see cref="CommandMetadata.AcceptsWireArgs"/> is true; and the value kind the binding
/// dispatches — its constant <c>Value</c> when it carries one, else the physical source's declared kind when the
/// caller can resolve it — must equal the command's declared <see cref="CommandMetadata.ValueKind"/>. A text-bearing
/// ordinary source must also be digital because analog samples carry no press edge, and so must every step of a
/// <see cref="BindingActivatorMode.Tapped"/> activator's sequence. Empty sources/commands are skipped entirely — they
/// are the structural gate's findings, not this one's.</para>
/// <para>The physical half is symmetrical with the command half: a source a caller's catalog cannot resolve is
/// refused by name, whether it appears as a page entry's <c>sources</c>, an activator step, a declared
/// <see cref="BindingProfileDocument.Modifiers"/> entry's own <c>sources</c>, or a chord row's
/// <c>held</c>/<c>chord</c> member that names no declared modifier. All four compile to a control that will never
/// signal, so all four are permanently dead rows — the exact class of typo this gate exists to turn loud. An
/// axis-COMPONENT source resolves its BASE control instead, and a control marked unaddressable is refused for that
/// reason alone (one refusal per source, never both). Callers that supply no
/// <see cref="BindingVocabularyLookups.SourceKind"/> keep every other check.</para></remarks>
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

    /// <summary>Reports one refusal line per unresolvable, unbindable, argument-incompatible, or kind-mismatched
    /// command, channel, or physical-control reference in <paramref name="document"/>.</summary>
    /// <param name="document">The binding document to check.</param>
    /// <param name="lookups">The live vocabularies to resolve against; each is independently optional, and
    /// <see cref="BindingVocabularyLookups.None"/> still runs the checks that need no vocabulary.</param>
    /// <returns>The refusal lines, in document order — <see cref="BindingVocabularyReport.IsClean"/> when there are
    /// none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="lookups"/> is
    /// <see langword="null"/>.</exception>
    public static BindingVocabularyReport Validate(BindingProfileDocument document, BindingVocabularyLookups lookups) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: lookups);

        var channel = lookups.Channel;
        var channelBinary = lookups.ChannelBinary;
        var command = lookups.Command;
        var errors = new List<string>();
        var sourceAddressable = lookups.SourceAddressable;
        var sourceKind = lookups.SourceKind;

        // OrdinalIgnoreCase, matching how BindingProfile.Compile resolves a row member against a declared modifier
        // id: a member differing only by case IS that modifier there, so refusing it here as an unknown control
        // would contradict the structural gate.
        var declaredModifierIds = new HashSet<string>(
            collection: (document.Modifiers ?? []).Select(selector: static modifier => modifier.Id),
            comparer: StringComparer.OrdinalIgnoreCase
        );

        // The THIRD place a physical source id appears, and the one that used to go unchecked. A declared
        // modifier's own sources are ordinary controls: a typo here compiles into a modifier no signal can ever
        // latch, so every page that modifier selects is dead forever — the same failure a typo'd page source
        // causes, refused in the same words for the same reason.
        foreach (var modifier in (document.Modifiers ?? [])) {
            if (modifier is null) {
                continue;
            }

            foreach (var modifierSource in (modifier.Sources ?? [])) {
                if (string.IsNullOrEmpty(value: modifierSource)) {
                    continue;
                }

                // One refusal per source, matching the page-source rule: an unaddressable control answers null
                // for its kind too, so falling through would report it twice. The addressability half stands on
                // its own, so a caller with no kind lookup still gets it.
                if (!(sourceAddressable?.Invoke(arg: modifierSource) ?? true)) {
                    errors.Add(item: $"modifier \"{modifier.Id}\" declares unaddressable control \"{modifierSource}\"");
                } else if (
                    (sourceKind is not null) &&
                    (sourceKind(arg: modifierSource) is null)
                ) {
                    errors.Add(item: $"modifier \"{modifier.Id}\" declares unknown control \"{modifierSource}\"");
                }
            }
        }

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
                        // One refusal per source: an unaddressable control is already refused, and the catalog
                        // answers null for its kind by construction, so falling through would double-report it.
                        if (!(sourceAddressable?.Invoke(arg: rawSource) ?? true)) {
                            errors.Add(item: $"page \"{page.Id}\" binds unaddressable control \"{rawSource}\"");

                            continue;
                        }

                        if (
                            (sourceKind is null) ||
                            !BindingSourceComponent.TrySplit(
                            baseSource: out var baseSource,
                            component: out var component,
                            source: rawSource
                        )
                        ) {
                            continue;
                        }

                        var baseKind = sourceKind(arg: baseSource);

                        // An axis-COMPONENT source's vocabulary half: the BASE control (with the .x/.y suffix
                        // parsed off — BindingProfile.Compile already refused a malformed suffix structurally) must
                        // name a real, two-dimensional control. An unresolvable base is "unknown control name"; a
                        // resolvable but non-Axis2D base is a distinct "malformed axis component" finding.
                        if (component is not null) {
                            if (baseKind is null) {
                                errors.Add(item: $"page \"{page.Id}\" binds {rawSource} to an axis component, but \"{baseSource}\" names unknown control");
                            } else if (baseKind != CommandValueKind.Axis2D) {
                                errors.Add(item: $"page \"{page.Id}\" binds {rawSource} to an axis component, but \"{baseSource}\" is not a two-dimensional axis control");
                            }
                        } else if (baseKind is null) {
                            // A plain source the catalog cannot resolve is a typo, and the structural gate has no
                            // physical vocabulary to catch it with: the row compiles, tables a control that will
                            // never signal, and is silently dead forever. Refused by name, exactly as an activator
                            // step naming an unknown control already is.
                            errors.Add(item: $"page \"{page.Id}\" binds unknown control \"{rawSource}\"");
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

            var rowLabel = $"row [{string.Join(
                separator: '+',
                values: row.Members
            )}] (group \"{BindingProfile.ResolveIdentifier(identifier: row.Group)}\")";

            foreach (var member in row.Members) {
                if (
                    string.IsNullOrEmpty(value: member) ||
                    declaredModifierIds.Contains(item: member)
                ) {
                    continue;
                }

                // A member that names no declared modifier is compiled into an IMPLICIT modifier over a source of
                // that name. If no such control exists the row can never be held, so the whole row — page or command
                // — is dead. Same refusal, same reason, as an unresolvable page source.
                if (!(sourceAddressable?.Invoke(arg: member) ?? true)) {
                    errors.Add(item: $"{rowLabel} names \"{member}\", which is neither a declared modifier nor an addressable control");
                } else if (
                    (sourceKind is not null) &&
                    (sourceKind(arg: member) is null)
                ) {
                    errors.Add(item: $"{rowLabel} names \"{member}\", which is neither a declared modifier nor a known control");
                }
            }

            if (row.Command is { } chordCommand) {
                if (chordCommand.Channel is { } chordChannel) {
                    CheckChannel(
                        channel: chordChannel,
                        channelBinary: channelBinary,
                        channelExists: channel,
                        errors: errors,
                        prefix: $"{rowLabel} folds into channel",
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
                    unknownError: $"{rowLabel} fires \"{chordCommand.Command}\", which names no registered command",
                    unbindableError: $"{rowLabel} fires \"{chordCommand.Command}\", which is not bindable",
                    mismatchError: (actual, declared) => $"{rowLabel} sends {Word(kind: actual)} to \"{chordCommand.Command}\", which takes {Word(kind: declared)}",
                    requiresWireArgs: (chordCommand.Text is not null),
                    wireArgsError: $"{rowLabel} binds text arguments to \"{chordCommand.Command}\", which accepts no wire arguments"
                );
            }
        }

        if (command is null) {
            return new BindingVocabularyReport(Errors: [.. errors]);
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
                        mismatchError: (actual, declared) => $"wheel \"{wheel!.Id}\" ring \"{ring!.Id}\" sends {Word(kind: actual)} to \"{sector.Command}\", which takes {Word(kind: declared)}",
                        // A sector's authored text is submitted as the line "<command> <text>" exactly as a page
                        // entry's is (InputRouter.Activate), so it needs the same wire-args destination. Omitting the
                        // gate here let a radial commit arguments a verb parses as nothing and silently drops.
                        requiresWireArgs: (sector.Text is not null),
                        wireArgsError: $"wheel \"{wheel!.Id}\" ring \"{ring!.Id}\" binds text arguments to \"{sector.Command}\", which accepts no wire arguments"
                    );
                }
            }
        }

        return new BindingVocabularyReport(Errors: [.. errors]);
    }
}
