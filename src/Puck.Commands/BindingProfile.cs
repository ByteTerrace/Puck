namespace Puck.Commands;

/// <summary>
/// Validates a <see cref="BindingProfileDocument"/> and compiles it into the runtime
/// <see cref="CompiledBindingProfile"/>: one compiled chord row per document row (a binding table and a
/// precomputed <see cref="BindingPageView"/> for a page meaning; edge payloads for a command meaning), plus the
/// group table the per-slot resolution scopes to.
/// </summary>
/// <remarks>
/// The two uniqueness rules a document must satisfy, rejected loudly otherwise: exactly one meaning per
/// <c>(group, ordered chord)</c>, and exactly one resting (empty-chord) page per group — and the resting row must
/// be a page, since an empty chord has no completion edge to fire a command with. Page ids are unique across the
/// whole document (they address pages in editors and guided sessions). The first row's group is the default group.
/// </remarks>
public static class BindingProfile {
    /// <summary>The command-name prefix a channel destination compiles down to (see
    /// <see cref="BindingPageEntryDefinition.Channel"/>/<see cref="BindingCommandDefinition.Channel"/>). Compiling a
    /// channel destination to a synthesized command keeps the runtime dispatch machinery here (<see cref="CommandBinding"/>,
    /// <see cref="InputRouter"/>, <see cref="CommandRegistry"/>) entirely command-shaped — this project never learns
    /// what a "channel" is. This portable name-shaped form is the default; <see cref="Compile"/> also accepts a host
    /// lowering callback so a fixed command vocabulary can represent a per-seat or remotely discovered table.</summary>
    public const string ChannelCommandPrefix = "channel.";

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

        var modifierIndexById = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var modifierIndexBySource = new Dictionary<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
        var modifiers = (document.Modifiers ?? []);

        for (var modifierIndex = 0; (modifierIndex < modifiers.Count); modifierIndex++) {
            var modifier = modifiers[modifierIndex];

            if (string.IsNullOrEmpty(value: modifier.Id)) {
                throw new ArgumentException(
                    message: "A modifier id must be non-empty.",
                    paramName: nameof(document)
                );
            }

            if (string.IsNullOrEmpty(value: modifier.Source)) {
                throw new ArgumentException(
                    message: $"Modifier \"{modifier.Id}\" must name a source.",
                    paramName: nameof(document)
                );
            }

            if (modifier.ReleaseThreshold > modifier.PressThreshold) {
                throw new ArgumentException(
                    message: $"Modifier \"{modifier.Id}\" has a release threshold above its press threshold.",
                    paramName: nameof(document)
                );
            }

            if (!modifierIndexById.TryAdd(
                key: modifier.Id,
                value: modifierIndex
            )) {
                throw new ArgumentException(
                    message: $"Duplicate modifier id \"{modifier.Id}\".",
                    paramName: nameof(document)
                );
            }

            if (!modifierIndexBySource.TryAdd(
                key: modifier.Source,
                value: modifierIndex
            )) {
                throw new ArgumentException(
                    message: $"Modifiers \"{modifiers[modifierIndexBySource[modifier.Source]].Id}\" and \"{modifier.Id}\" share the source \"{modifier.Source}\".",
                    paramName: nameof(document)
                );
            }
        }

        var documentRows = ((document.Chords is { Count: > 0 } chords)
            ? chords
            : throw new ArgumentException(
            message: "A binding profile must carry at least one chord row.",
            paramName: nameof(document)
        ));
        // First pass: group registration, chord resolution, the uniqueness rules, and the raw row facts. Views are
        // built in a second pass so each page view can carry its whole group's command-chord hints.
        var groupIndexByName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var groupNames = new List<string>();
        // Page id → (owning group, chord row) — the uniqueness set AND the wheel section's hold-page resolver.
        var pageRowsById = new Dictionary<string, (int GroupIndex, int RowIndex)>(comparer: StringComparer.Ordinal);
        var restingByGroup = new List<int>();
        var rowChords = new int[documentRows.Count][];
        var rowGroups = new int[documentRows.Count];
        var seenChordKeys = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
            var row = (documentRows[rowIndex]
                ?? throw new ArgumentException(
                message: $"Chord row {rowIndex} is null.",
                paramName: nameof(document)
            ));

            if (string.IsNullOrEmpty(value: row.Group)) {
                throw new ArgumentException(
                    message: $"Chord row {rowIndex} must name a group.",
                    paramName: nameof(document)
                );
            }

            if (!groupIndexByName.TryGetValue(
                key: row.Group,
                value: out var groupIndex
            )) {
                groupIndex = groupNames.Count;
                groupIndexByName[row.Group] = groupIndex;
                groupNames.Add(item: row.Group);
                restingByGroup.Add(item: -1);
            }

            rowGroups[rowIndex] = groupIndex;

            var chordIds = (row.Chord ?? []);
            var chord = new int[chordIds.Count];
            var chordModifiers = new HashSet<int>();

            for (var chordIndex = 0; (chordIndex < chordIds.Count); chordIndex++) {
                if (!modifierIndexById.TryGetValue(
                    key: chordIds[chordIndex],
                    value: out var modifierIndex
                )) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{row.Group}\") chords on undeclared modifier \"{chordIds[chordIndex]}\".",
                        paramName: nameof(document)
                    );
                }

                if (!chordModifiers.Add(item: modifierIndex)) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{row.Group}\") repeats modifier \"{chordIds[chordIndex]}\" in its chord.",
                        paramName: nameof(document)
                    );
                }

                chord[chordIndex] = modifierIndex;
            }

            rowChords[rowIndex] = chord;

            // Rule 1: exactly one meaning per (group, chord).
            if (!seenChordKeys.Add(item: $"{groupIndex}\0{string.Join(
                separator: ',',
                values: chord
            )}")) {
                throw new ArgumentException(
                    message: $"Group \"{row.Group}\" declares two meanings for the chord [{string.Join(
                        separator: ", ",
                        values: chordIds
                    )}] — exactly one meaning per (group, chord).",
                    paramName: nameof(document)
                );
            }

            if ((row.Page is null) == (row.Command is null)) {
                throw new ArgumentException(
                    message: $"Chord row {rowIndex} (group \"{row.Group}\") must carry exactly one meaning — a page or a command.",
                    paramName: nameof(document)
                );
            }

            if (row.Page is { } page) {
                if (string.IsNullOrEmpty(value: page.Id)) {
                    throw new ArgumentException(
                        message: "A page id must be non-empty.",
                        paramName: nameof(document)
                    );
                }

                if (!pageRowsById.TryAdd(
                    key: page.Id,
                    value: (GroupIndex: groupIndex, RowIndex: rowIndex)
                )) {
                    throw new ArgumentException(
                        message: $"Duplicate page id \"{page.Id}\".",
                        paramName: nameof(document)
                    );
                }

                if (chord.Length == 0) {
                    restingByGroup[groupIndex] = rowIndex;
                }
            } else {
                var chordCommand = row.Command!;

                if ((chordCommand.Command is null) == (chordCommand.Channel is null)) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{row.Group}\") must carry exactly one destination — a command or a channel.",
                        paramName: nameof(document)
                    );
                }

                ValidateValue(
                    value: chordCommand.Value,
                    path: $"Chord row {rowIndex} (group \"{row.Group}\")",
                    isChannel: (chordCommand.Channel is not null),
                    paramName: nameof(document)
                );

                if (chordCommand.Channel is { } channel) {
                    ValidateChannelRef(
                        channel: channel,
                        path: $"Chord row {rowIndex} (group \"{row.Group}\")",
                        paramName: nameof(document)
                    );

                    ValidateChannelScale(
                        channel: channel,
                        path: $"Chord row {rowIndex} (group \"{row.Group}\")",
                        paramName: nameof(document),
                        scale: chordCommand.Scale
                    );
                } else if (string.IsNullOrEmpty(value: chordCommand.Command)) {
                    throw new ArgumentException(
                        message: $"Chord row {rowIndex} (group \"{row.Group}\") must name the command or channel it fires.",
                        paramName: nameof(document)
                    );
                }

                // Rule 2's command half: an empty chord has no completion edge — the resting row must be a page.
                if (chord.Length == 0) {
                    throw new ArgumentException(
                        message: $"Group \"{row.Group}\" binds a command to the empty chord — the resting row must be a page.",
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

            if (string.IsNullOrEmpty(value: context.Group)) {
                throw new ArgumentException(
                    message: $"Contexts row {contextIndex} (family \"{context.Family}\", state \"{context.State}\") must name a group.",
                    paramName: nameof(document)
                );
            }

            if (!seenContextKeys.Add(item: $"{context.Family}\0{context.State}")) {
                throw new ArgumentException(
                    message: $"Contexts row {contextIndex} re-declares (family \"{context.Family}\", state \"{context.State}\") — exactly one group per (family, state).",
                    paramName: nameof(document)
                );
            }

            if (!groupIndexByName.ContainsKey(key: context.Group)) {
                throw new ArgumentException(
                    message: $"Contexts row {contextIndex} (family \"{context.Family}\", state \"{context.State}\") names group \"{context.Group}\", which no chord row declares.",
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

        foreach (var wheel in wheels) {
            if (wheel is null) {
                throw new ArgumentException(
                    message: "A wheels row is null.",
                    paramName: nameof(document)
                );
            }

            if (
                string.IsNullOrEmpty(value: wheel.Id) ||
                !wheelIds.Add(item: wheel.Id)
            ) {
                throw new ArgumentException(
                    message: $"Wheel id \"{wheel.Id}\" must be non-empty and profile-unique.",
                    paramName: nameof(document)
                );
            }

            if (
                string.IsNullOrEmpty(value: wheel.Group) ||
                !groupIndexByName.TryGetValue(
                key: wheel.Group,
                value: out var wheelGroupIndex
            )
            ) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" names group \"{wheel.Group}\", which no chord row declares.",
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
                        message: $"Wheel \"{wheel.Id}\" holds on invalid or repeated page \"{holdPage}\"; every hold page must be a distinct chord-row page of group \"{wheel.Group}\".",
                        paramName: nameof(document)
                    );
                }

                if (wheelViewByRow.ContainsKey(key: holdRow.RowIndex)) {
                    throw new ArgumentException(
                        message: $"Hold page \"{holdPage}\" presents more than one wheel.",
                        paramName: nameof(document)
                    );
                }

                holdRows[holdIndex] = holdRow.RowIndex;
            }

            var style = (wheel.Style ?? new BindingWheelStyleDefinition());

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
                !float.IsFinite(f: style.RotationDegrees)
            ) {
                throw new ArgumentException(
                    message: $"Wheel \"{wheel.Id}\" carries invalid style geometry.",
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
                    ThresholdsSquared: thresholdSquares,
                    OutwardThresholdsSquared: outwardSquares,
                    InwardThresholdsSquared: inwardSquares,
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
                        message: $"Duplicate page id \"{ring.Id}\".",
                        paramName: nameof(document)
                    );
                }

                var sectorCount = (ring.Entries?.Count ?? 0);

                if (
                    (sectorCount < BindingWheelDefinition.MinSectorsPerRing) ||
                    (sectorCount > BindingWheelDefinition.MaxSectorsPerRing)
                ) {
                    throw new ArgumentException(
                        message: $"Wheel ring \"{ring.Id}\" (group \"{wheel.Group}\") declares {sectorCount} sectors; a ring presents {BindingWheelDefinition.MinSectorsPerRing}..{BindingWheelDefinition.MaxSectorsPerRing}.",
                        paramName: nameof(document)
                    );
                }

                var sectorViews = new BindingWheelSectorView[sectorCount];

                for (var sectorIndex = 0; (sectorIndex < sectorCount); sectorIndex++) {
                    var sector = (ring.Entries![sectorIndex]
                        ?? throw new ArgumentException(
                        message: $"Wheel ring \"{ring.Id}\" (group \"{wheel.Group}\") sector {sectorIndex} is null.",
                        paramName: nameof(document)
                    ));
                    var sectorPath = $"Wheel ring \"{ring.Id}\" (group \"{wheel.Group}\") sector {sectorIndex}";

                    if (string.IsNullOrEmpty(value: sector.Command)) {
                        throw new ArgumentException(
                            message: $"{sectorPath} must name the command it commits.",
                            paramName: nameof(document)
                        );
                    }

                    // The narrowed sector shape — each foreign member refused BY NAME rather than ignored, so an
                    // authored field never silently means nothing.
                    if (!string.IsNullOrEmpty(value: sector.Source)) {
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
                            phase: phase
                        ),
                        Label: sector.Label,
                        Icon: sector.Icon
                    );
                }

                ringViews[ringIndex] = new BindingWheelRingView(
                    PageId: ring.Id,
                    Label: ring.Label,
                    Sectors: sectorViews
                );
            }

            var view = new BindingWheelView(
                Id: wheel.Id,
                Group: wheel.Group,
                HoldPageIds: holdPages,
                Rings: ringViews,
                Style: style,
                Excursion: excursionView
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
                    : command.Command!);

                commandRows.Add(item: rowIndex);
                hints.Add(item: new BindingChordCommandView(
                    Chord: [.. rowChords[rowIndex].Select(selector: index => modifiers[index].Id)],
                    Command: effectiveCommand,
                    HoldRelease: command.HoldRelease,
                    Icon: command.Icon,
                    Label: command.Label,
                    Sources: [.. rowChords[rowIndex].Select(selector: index => modifiers[index].Source)]
                ));
            }

            commandRowsByGroup[groupIndex] = [.. commandRows];
            hintsByGroup[groupIndex] = hints;
        }

        var rows = new CompiledBindingProfile.CompiledChordRow[documentRows.Count];
        var nextActivatorIndex = 0;

        for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
            var row = documentRows[rowIndex];
            var chord = rowChords[rowIndex];
            var groupIndex = rowGroups[rowIndex];

            if (row.Page is { } page) {
                var (table, activators) = BuildTable(
                    page: page,
                    channelCommandName: channelCommandName,
                    nextActivatorIndex: ref nextActivatorIndex
                );

                rows[rowIndex] = new CompiledBindingProfile.CompiledChordRow(
                    Chord: chord,
                    Command: null,
                    GroupIndex: groupIndex,
                    Table: table,
                    Activators: ((activators.Count > 0)
                    ? activators
                    : null),
                    View: BuildView(
                        chord: [.. chord],
                        group: groupNames[groupIndex],
                        hints: hintsByGroup[groupIndex],
                        modifiers: modifiers,
                        page: page,
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
                    : command.Command!);

                rows[rowIndex] = new CompiledBindingProfile.CompiledChordRow(
                    Chord: chord,
                    Command: new CompiledBindingProfile.CompiledCommandEdge(
                        Command: effectiveCommand,
                        DispatchRelease: command.HoldRelease,
                        PressValue: pressValue,
                        ReleaseValue: CommandValue.Inactive(kind: pressValue.Kind)
                    ),
                    GroupIndex: groupIndex,
                    Table: null,
                    View: null
                );
            }
        }

        return new CompiledBindingProfile(
            commandRowsByGroup: commandRowsByGroup,
            groupIndexByName: groupIndexByName,
            groups: [.. groupNames],
            modifierIndexBySource: modifierIndexBySource,
            modifiers: modifiers,
            restingRowByGroup: [.. restingByGroup],
            rows: rows,
            activatorCount: nextActivatorIndex,
            wheelViewByRow: wheelViewByRow
        );
    }

    private static (IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> Table, List<CompiledBindingProfile.CompiledActivatorEntry> Activators) BuildTable(BindingPageDefinition page, Func<ChannelRef, string> channelCommandName, ref int nextActivatorIndex) {
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

        for (var entryIndex = 0; (entryIndex < entries.Count); entryIndex++) {
            var entry = entries[entryIndex];
            var hasSource = !string.IsNullOrEmpty(value: entry.Source);
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
                    Activator: activator,
                    Edge: new CompiledBindingProfile.CompiledCommandEdge(
                        Command: effectiveCommand,
                        // A channel destination must dispatch its release edge: CommandRegistry.ApplySnapshot skips any
                        // entry whose Dispatch is false, and only the channel verb's handler calls seat.ReleaseChannel —
                        // without dispatch, a closed gate or completed tap would hold the channel forever. A command
                        // destination keeps HoldRelease's own default (momentary; no release needed).
                        DispatchRelease: (channelScale is not null),
                        PressValue: pressValue,
                        ReleaseValue: CommandValue.Inactive(kind: pressValue.Kind)
                    )
                ));

                continue;
            }

            // A plain-source entry may name an axis COMPONENT (gamepad.leftStick.x) instead of a bare control — the
            // table key is always the BASE source (what a raw InputSignal actually carries); the component rides
            // the compiled CommandBinding and is extracted at resolve time (see InputRouter's ResolveValue).
            if (!BindingSourceComponent.TrySplit(
                source: entry.Source!,
                baseSource: out var baseSource,
                component: out var component
            )) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" entry for {entry.Source} names a malformed axis component — the final segment must be \"x\" or \"y\".",
                    paramName: nameof(page)
                );
            }

            if (
                (component is not null) &&
                (channelScale is null)
            ) {
                throw new ArgumentException(
                    message: $"Page \"{page.Id}\" entry for {entry.Source} names an axis component, which is only meaningful on a channel destination.",
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
                Mode: entry.Mode
            ));
        }

        var table = new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var (source, list) in grouped) {
            table[source] = list;
        }

        return (table, activators);
    }

    private static BindingPageView BuildView(
        HashSet<int> chord,
        string group,
        IReadOnlyList<BindingChordCommandView> hints,
        IReadOnlyList<BindingModifierDefinition> modifiers,
        BindingPageDefinition page,
        Func<ChannelRef, string> channelCommandName
    ) {
        var buttons = new BindingPageButtonView[(page.Entries?.Count ?? 0)];

        for (var entryIndex = 0; (entryIndex < buttons.Length); entryIndex++) {
            var entry = page.Entries![entryIndex];

            buttons[entryIndex] = new BindingPageButtonView(
                Command: ((entry.Channel is { } channel)
                ? channelCommandName(arg: channel)
                : entry.Command!),
                Icon: entry.Icon,
                Label: entry.Label,
                // An activator entry has no Source — its synthetic "activator[...]" label stands in, so a
                // binding-bar consumer never renders a null/blank chip for it.
                Source: entry.TriggerLabel
            );
        }

        var modifierViews = new BindingModifierView[modifiers.Count];

        for (var modifierIndex = 0; (modifierIndex < modifiers.Count); modifierIndex++) {
            var modifier = modifiers[modifierIndex];

            modifierViews[modifierIndex] = new BindingModifierView(
                Icon: modifier.Icon,
                Id: modifier.Id,
                Label: modifier.Label,
                Required: chord.Contains(item: modifierIndex),
                Source: modifier.Source
            );
        }

        return new BindingPageView(
            Buttons: buttons,
            CommandChords: hints,
            Group: group,
            Icon: page.Icon,
            Label: page.Label,
            Modifiers: modifierViews,
            PageId: page.Id
        );
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
                message: $"{path} Value kind {(int)constant.Kind} is not declared.",
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

}
