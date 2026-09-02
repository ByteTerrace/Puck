using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Whether an authored seconds value compiles to a tick count the runtime's int-typed compiled fields (the checked
    // casts in WorldInputHoldAuthoring.Compile) can hold. Delegates to the same ulong-typed conversion Compile uses
    // (WorldSimulationTickConversion.DurationTicks) so int overflow is a plain comparison, never a duplicated
    // rounding rule. The catch covers a value large enough to overflow that conversion's own ulong arithmetic.
    private static bool FitsCompiledRange(float seconds, uint ratePerSecond) {
        try {
            return (WorldSimulationTickConversion.DurationTicks(
                ratePerSecond: ratePerSecond,
                seconds: seconds
            ) <= int.MaxValue);
        } catch (OverflowException) {
            return false;
        }
    }
    // Slot-set/bank structure: names, uniqueness, and ceilings — everything checkable WITHOUT the composed binding
    // profile. The bank PageId existence check runs separately, after ValidateBindingOverlays compiles the
    // composed profile (a bank's page reference is checkable only against the whole overlay stack's result).
    private static void ValidateBindingBar(WorldBindingBarAuthoring? authoring, string path, IReadOnlyDictionary<string, WorldStateRow> stateRows, List<string> errors) {
        if (authoring is null) {
            return;
        }

        ValidateOverlayPredicate(
            errors: errors,
            path: $"{path}.visible",
            predicate: authoring.Visible
        );
        WorldStateBindingContext.ValidatePresentationRowReference(
            errors: errors,
            path: $"{path}.iconRow",
            reference: authoring.IconRow,
            stateRows: stateRows
        );

        RequireUnitInterval(
            value: authoring.MultiSeatAlpha,
            name: $"{path}.multiSeatAlpha",
            errors: errors
        );

        if (authoring.SlotSet is null) {
            errors.Add(item: $"{path}.slotSet is required.");
        } else {
            if (authoring.SlotSet.Count > WorldBindingBarCapacity.MaxSlots) {
                errors.Add(item: $"{path}.slotSet declares {authoring.SlotSet.Count} entries, exceeding the {WorldBindingBarCapacity.MaxSlots}-slot ceiling.");
            }

            var seenSources = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < authoring.SlotSet.Count); index++) {
                var source = authoring.SlotSet[index];
                var slotPath = $"{path}.slotSet[{index}]";

                if (
                    RequireUniqueName(
                    errors: errors,
                    field: "",
                    path: slotPath,
                    seen: seenSources,
                    value: source
                ) &&
                    (InputSourceVocabularyHook.IsKnownSourceId is { } isKnown) &&
                    !isKnown(source)
                ) {
                    errors.Add(item: $"{slotPath} '{source}' is not a declared input source id.");
                }
            }
        }

        void ValidateSlots(IReadOnlyList<WorldBindingBarSlotPlacement?> slots, string slotsPath) {
            var placed = new HashSet<string>(comparer: StringComparer.Ordinal);
            var slotSet = new HashSet<string>(collection: (authoring.SlotSet ?? []), comparer: StringComparer.Ordinal);

            for (var index = 0; (index < slots.Count); index++) {
                var placement = slots[index];
                var slotPath = $"{slotsPath}[{index}]";

                if (
                    RequireUniqueName(
                    value: placement?.Source,
                    seen: placed,
                    path: slotPath,
                    field: "source",
                    errors: errors
                ) &&
                    !slotSet.Contains(item: placement!.Source)
                ) {
                    errors.Add(item: $"{slotPath}.source '{placement.Source}' is not in {path}.slotSet — a placement names a control the bar shows.");
                }

                if ((placement is not null) && (!float.IsFinite(f: placement.X) || !float.IsFinite(f: placement.Y))) {
                    errors.Add(item: $"{slotPath} needs finite x and y pitches.");
                }

                if (placement?.Badge is { } badge) {
                    switch (ValidateBadge(badge: badge)) {
                        case BadgeValidity.WrongCount:
                            errors.Add(item: $"{slotPath}.badge needs exactly [x, y].");
                            break;
                        case BadgeValidity.OutOfRange:
                            errors.Add(item: $"{slotPath}.badge [{badge[0]}, {badge[1]}] is outside [-1, 1] on an axis.");
                            break;
                    }
                }
            }
        }

        void ValidateAnchor(WorldBindingBarAnchor anchor, string anchorPath) {
            RequireNonNegative(
                value: anchor.Inset,
                name: $"{anchorPath}.inset",
                errors: errors
            );
        }

        void ValidateLayout(WorldBindingBarLayout layout, string layoutPath) {
            if (layout.Anchor is { } layoutAnchor) {
                ValidateAnchor(
                    anchor: layoutAnchor,
                    anchorPath: $"{layoutPath}.anchor"
                );
            }

            var tableNames = new HashSet<string>(comparer: StringComparer.Ordinal);

            if (layout.Tables is { } tables) {
                foreach (var (tableName, rows) in tables) {
                    tableNames.Add(item: tableName);

                    if (rows is null) {
                        errors.Add(item: $"{layoutPath}.tables['{tableName}'] is required.");

                        continue;
                    }

                    ValidateSlots(
                        slots: rows,
                        slotsPath: $"{layoutPath}.tables['{tableName}']"
                    );
                }
            }

            if (layout.Banks is { } bankPlacements) {
                var bankIds = new HashSet<string>(collection: (authoring.Banks ?? []).Where(predicate: static bank => (bank?.Id is not null)).Select(selector: static bank => bank!.Id), comparer: StringComparer.Ordinal);

                foreach (var (bankId, placement) in bankPlacements) {
                    var bankPath = $"{layoutPath}.banks['{bankId}']";

                    if (!bankIds.Contains(item: bankId)) {
                        errors.Add(item: $"{bankPath} names no bank in {path}.banks.");
                    }

                    if (placement is null) {
                        errors.Add(item: $"{bankPath} is required.");

                        continue;
                    }

                    if (placement.Anchor is { } bankAnchor) {
                        ValidateAnchor(
                            anchor: bankAnchor,
                            anchorPath: $"{bankPath}.anchor"
                        );
                    }

                    if (placement.Pieces is null) {
                        errors.Add(item: $"{bankPath}.pieces is required — a bank shows what it places.");

                        continue;
                    }

                    for (var index = 0; (index < placement.Pieces.Count); index++) {
                        var piece = placement.Pieces[index];
                        var piecePath = $"{bankPath}.pieces[{index}]";

                        if ((piece is null) || string.IsNullOrWhiteSpace(value: piece.Table)) {
                            errors.Add(item: $"{piecePath}.table is required.");

                            continue;
                        }

                        if (!tableNames.Contains(item: piece.Table)) {
                            errors.Add(item: $"{piecePath}.table '{piece.Table}' names no entry of {layoutPath}.tables.");
                        }

                        if ((piece.At is { } at) && ((at.Count != 2) || !float.IsFinite(f: at[0]) || !float.IsFinite(f: at[1]))) {
                            errors.Add(item: $"{piecePath}.at needs finite [x, y] pitches.");
                        }

                        if ((piece.Badge is { } badge) && (ValidateBadge(badge: badge) != BadgeValidity.Valid)) {
                            errors.Add(item: $"{piecePath}.badge needs [x, y] within [-1, 1].");
                        }
                    }
                }
            }

            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.buttonSize", value: layout.ButtonSize);
            RequireOptionalNonNegative(errors: errors, name: $"{layoutPath}.glyphOffsetRatio", value: layout.GlyphOffsetRatio);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.glyphSizeRatio", value: layout.GlyphSizeRatio);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.modifierHalfRatio", value: layout.ModifierHalfRatio);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.modifierSpacingRatio", value: layout.ModifierSpacingRatio);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.modifierGlyphRatio", value: layout.ModifierGlyphRatio);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.labelCellRatio", value: layout.LabelCellRatio);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.labelCellMinPx", value: layout.LabelCellMinPx);
            RequireOptionalFinite(errors: errors, name: $"{layoutPath}.labelGapRatio", value: layout.LabelGapRatio);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.hintCellRatio", value: layout.HintCellRatio);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.hintCellMinPx", value: layout.HintCellMinPx);
            RequireOptionalPositive(errors: errors, name: $"{layoutPath}.hintLineStepRatio", value: layout.HintLineStepRatio);
            RequireOptionalFinite(errors: errors, name: $"{layoutPath}.hintBaseGapRatio", value: layout.HintBaseGapRatio);
        }

        if (authoring.Layouts is { } namedLayouts) {
            foreach (var (name, named) in namedLayouts) {
                if (named is null) {
                    errors.Add(item: $"{path}.layouts['{name}'] is required.");

                    continue;
                }

                ValidateLayout(
                    layout: named,
                    layoutPath: $"{path}.layouts['{name}']"
                );
            }

            if ((authoring.Layout is { } defaultName) && !namedLayouts.ContainsKey(key: defaultName)) {
                errors.Add(item: $"{path}.layout '{defaultName}' names no entry of {path}.layouts.");
            }
        } else if (authoring.Layout is { } orphanName) {
            errors.Add(item: $"{path}.layout '{orphanName}' names no entry of {path}.layouts — none are authored.");
        }

        if (
            (authoring.LayoutCell is { } layoutCell) &&
            !(BindableState.TryParseBinding(
            key: out var layoutKey,
            row: out _,
            value: layoutCell
        ) && (layoutKey is not null))
        ) {
            errors.Add(item: $"{path}.layoutCell '{layoutCell}' must be spelled state.<row>.<key>.");
        }

        if (
            (authoring.ModelCell is { } modelCell) &&
            !(BindableState.TryParseBinding(
            key: out var modelKey,
            row: out _,
            value: modelCell
        ) && (modelKey is not null))
        ) {
            errors.Add(item: $"{path}.modelCell '{modelCell}' must be spelled state.<row>.<key>.");
        }

        if (authoring.Banks is null) {
            errors.Add(item: $"{path}.banks is required.");
        } else if (authoring.Banks.Count == 0) {
            errors.Add(item: $"{path}.banks must declare at least one bank.");
        } else if (authoring.Banks.Count > WorldBindingBarCapacity.MaxBanks) {
            errors.Add(item: $"{path}.banks declares {authoring.Banks.Count} entries, exceeding the {WorldBindingBarCapacity.MaxBanks}-bank ceiling.");
        } else {
            var seenBanks = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < authoring.Banks.Count); index++) {
                var bank = authoring.Banks[index];
                var bankPath = $"{path}.banks[{index}]";

                if (bank is null) {
                    errors.Add(item: $"{bankPath} is required.");

                    continue;
                }

                RequireUniqueName(
                    value: bank.Id,
                    seen: seenBanks,
                    path: bankPath,
                    field: "id",
                    errors: errors
                );

                if (string.IsNullOrWhiteSpace(value: bank.PageId)) {
                    errors.Add(item: $"{bankPath}.pageId is required.");
                }

                RequireUnitInterval(
                    value: bank.Alpha,
                    name: $"{bankPath}.alpha",
                    errors: errors
                );

                if (bank.ActiveAlpha is { } activeAlpha) {
                    RequireUnitInterval(
                        errors: errors,
                        name: $"{bankPath}.activeAlpha",
                        value: activeAlpha
                    );
                }
            }
        }

    }

    // A badge's failure mode, so a caller can pick its own wording per case while sharing the underlying rule.
    private enum BadgeValidity {
        Valid,
        WrongCount,
        OutOfRange
    }

    // Shared badge rule: exactly two elements, both finite, each within [-1, 1] — the same [x, y] offset shape used
    // by a slot placement and a bank piece.
    private static BadgeValidity ValidateBadge(IReadOnlyList<float> badge) {
        if (badge.Count != 2) {
            return BadgeValidity.WrongCount;
        }

        if (!float.IsFinite(f: badge[0]) || !float.IsFinite(f: badge[1]) || (MathF.Abs(x: badge[0]) > 1f) || (MathF.Abs(x: badge[1]) > 1f)) {
            return BadgeValidity.OutOfRange;
        }

        return BadgeValidity.Valid;
    }
    private static void RequireOptionalFinite(float? value, string name, List<string> errors) {
        if (value is { } authored) {
            RequireFinite(
                errors: errors,
                name: name,
                value: authored
            );
        }
    }
    private static void RequireOptionalNonNegative(float? value, string name, List<string> errors) {
        if (value is { } authored) {
            RequireNonNegative(
                errors: errors,
                name: name,
                value: authored
            );
        }
    }
    private static void RequireOptionalPositive(float? value, string name, List<string> errors) {
        if (value is { } authored) {
            RequirePositive(
                errors: errors,
                name: name,
                value: authored
            );
        }
    }
    // One authored icon string, refused by name when it names no row in the composed icon table.
    private static void CheckComposedIcon(string? icon, string door, IReadOnlySet<string> iconNames, List<string> errors) {
        if (
            !string.IsNullOrEmpty(value: icon) &&
            !iconNames.Contains(item: icon)
        ) {
            errors.Add(item: $"bindingOverlays {door} icon '{icon}' names no row in icons.icons.");
        }
    }
    // A page's own display icon — the shape a chord-row page and a wheel ring page share. An ENTRY carries no icon:
    // a row says what it does, and what that looks like is resolved from authored state by the surface drawing it.
    private static void CheckComposedPageIcons(BindingPageDefinition page, string door, IReadOnlySet<string> iconNames, List<string> errors) {
        CheckComposedIcon(
            door: door,
            errors: errors,
            icon: page.Icon,
            iconNames: iconNames
        );

    }
    // Every icon-bearing door the composed binding profile carries directly — pages, modifiers, and chord commands.
    // Entry and sector presentation is state-backed and validated separately by WorldStateBindingContext.
    private static void ValidateComposedIcons(BindingProfileDocument composed, IReadOnlySet<string> iconNames, List<string> errors) {
        foreach (var modifier in composed.Modifiers) {
            if (modifier is not null) {
                CheckComposedIcon(
                    door: $"modifier '{modifier.Id}'",
                    errors: errors,
                    icon: modifier.Icon,
                    iconNames: iconNames
                );
            }
        }

        foreach (var chord in composed.Chords) {
            if (chord is null) {
                continue;
            }

            CheckComposedIcon(
                door: $"group '{chord.Group}' command",
                errors: errors,
                icon: chord.Command?.Icon,
                iconNames: iconNames
            );

            if (chord.Page is { } page) {
                CheckComposedPageIcons(
                    door: $"page '{page.Id}'",
                    errors: errors,
                    iconNames: iconNames,
                    page: page
                );
            }
        }

        foreach (var wheel in (composed.Wheels ?? [])) {
            if (wheel is null) {
                continue;
            }

            foreach (var ring in wheel.Rings) {
                if (ring is not null) {
                    CheckComposedPageIcons(
                        door: $"wheel '{wheel.Id}' ring '{ring.Id}'",
                        errors: errors,
                        iconNames: iconNames,
                        page: ring
                    );
                }
            }
        }
    }
    // The player-profile-side bar preferences (BindingProfileDocument.BindingBar) — a LOOK override, presentation
    // only. Validated to the same strictness as the world-side layout so an out-of-range scale refuses by name here
    // rather than being silently dropped at the runtime resolver (WorldBindingBarControl reads a finite positive
    // scale and ignores anything else). Absence (a null preferences block, or a null field within it) defers to the
    // world-authored policy and is never a refusal.
    private static void ValidateBindingBarPreferences(BindingBarPreferences? preferences, string path, List<string> errors) {
        if (preferences?.Scale is { } scale) {
            RequirePositive(
                errors: errors,
                name: $"{path}.scale",
                value: scale
            );
        }

        if (preferences?.ContrastBoost is { } contrastBoost) {
            RequireRange(
                value: contrastBoost,
                min: 1f,
                max: 2f,
                name: $"{path}.contrastBoost",
                errors: errors
            );
        }

        if (preferences?.UiScale is { } uiScale) {
            RequireRange(
                value: uiScale,
                min: 0.5f,
                max: 2f,
                name: $"{path}.uiScale",
                errors: errors
            );
        }
    }
    // The bank PageId existence check — run AFTER the composed profile compiles successfully (a bank's page
    // reference is only checkable against the WHOLE overlay stack's result, never one overlay's own document).
    private static void ValidateBindingBarPageReferences(IReadOnlyList<WorldBindingOverlay> overlays, CompiledBindingProfile profile, List<string> errors) {
        for (var index = 0; (index < overlays.Count); index++) {
            var banks = overlays[index]?.BindingBar?.Banks;

            if (banks is null) {
                continue;
            }

            for (var bankIndex = 0; (bankIndex < banks.Count); bankIndex++) {
                var pageId = banks[bankIndex]?.PageId;

                if (
                    !string.IsNullOrWhiteSpace(value: pageId) &&
                    !profile.TryGetPageView(
                    pageId: pageId,
                    view: out _
                )
                ) {
                    errors.Add(item: $"bindingOverlays[{index}].bindingBar.banks[{bankIndex}].pageId '{pageId}' names no page in the composed binding profile.");
                }
            }
        }
    }
    // The per-world binding overlays: non-empty unique ids, and the COMPOSED result (every overlay, in order) passes
    // the existing binding compiler — a partial overlay page that only makes sense post-merge still gates against the
    // real runtime artifact, and the binding validator is never reimplemented. No overlays compose to the empty
    // document: a world with no bindings is valid. The vocabulary half resolves channel names against THIS document's
    // own table (the `channels` parameter), never a process-global.
    private static void ValidateBindingOverlays(IReadOnlyList<WorldBindingOverlay> overlays, WorldChannelTable? channels, IReadOnlyDictionary<string, WorldStateRow> stateRows, IReadOnlyList<WorldSeatModeFamily> seatModes, IReadOnlySet<string> iconNames, bool iconsAuthored, List<string> errors) {
        if (overlays is null) {
            errors.Add(item: "bindingOverlays is required.");

            return;
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        var layers = new List<BindingProfileDocument?>();

        for (var index = 0; (index < overlays.Count); index++) {
            var overlay = overlays[index];
            var path = $"bindingOverlays[{index}]";

            if (overlay is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(
                value: overlay.Id,
                seen: ids,
                path: path,
                field: "id",
                errors: errors
            );

            if (overlay.Document is null) {
                errors.Add(item: $"{path}.document is required.");
            } else {
                layers.Add(item: overlay.Document);
                ValidateBindingBarPreferences(
                    errors: errors,
                    path: $"{path}.document.bindingBar",
                    preferences: overlay.Document.BindingBar
                );
                var stateContextErrors = new List<string>();

                WorldStateBindingContext.Validate(
                    document: overlay.Document,
                    stateRows: stateRows,
                    errors: stateContextErrors
                );

                foreach (var error in stateContextErrors) {
                    errors.Add(item: $"{path} ('{overlay.Id}') {error}");
                }

                // The vocabulary half, per overlay so the finding names WHICH overlay carries the dead reference.
                // Skipped (never silently passed) when no vocabulary is installed — an offline/pre-container caller
                // has no registry to ask; the composition root's post-build sweep re-covers the boot documents.
                if (channels is { } table) {
                    var vocabularyErrors = new List<string>();

                    BindingVocabularyHook.VocabularyCheck?.Invoke(
                        overlay.Document,
                        table,
                        seatModes,
                        vocabularyErrors
                    );

                    foreach (var error in vocabularyErrors) {
                        errors.Add(item: $"{path} ('{overlay.Id}') {error}");
                    }
                }
            }

            ValidateBindingBar(
                authoring: overlay.BindingBar,
                path: $"{path}.bindingBar",
                stateRows: stateRows,
                errors: errors
            );
        }

        var composed = WorldBindingComposer.Compose(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: layers));

        try {
            var compiled = BindingProfile.Compile(document: composed);

            if (compiled.Modifiers.Count > WorldBindingBarCapacity.MaxModifiers) {
                errors.Add(item: $"bindingOverlays compose {compiled.Modifiers.Count} modifiers, exceeding the {WorldBindingBarCapacity.MaxModifiers}-modifier ceiling.");
            }

            ValidateBindingBarPageReferences(
                errors: errors,
                overlays: overlays,
                profile: compiled
            );

            // Every authored icon string, checked against the icon table ONLY when some document in the basis chain
            // authored one (see ValidateIconography's Absent gate) — no authored icons.icons means every icon string
            // draws a blank plate, never a refusal.
            if (iconsAuthored) {
                ValidateComposedIcons(
                    composed: composed,
                    errors: errors,
                    iconNames: iconNames
                );
            }
        } catch (ArgumentException exception) {
            errors.Add(item: $"bindingOverlays do not compose into a valid mapping: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }
    }
    // The channel table (SIM-AFFECTING — the PlayerIntent vector's vocabulary): name uniqueness; exactly one
    // consumer per row (a role XOR a composition trigger); role channels are bipolar only; channel-count ceiling;
    // threshold range on binary rows; motion-model role completeness (Grounded needs move-forward/move-strafe/turn,
    // Free needs all six). Returns the composition-channel name set kit Actions maps resolve against; composition
    // channels carry no shape restriction.
    private static (HashSet<string> AllNames, HashSet<string> CompositionNames) ValidateChannels(WorldDefinition definition, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var compositionNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var roleClaims = new Dictionary<ChannelRole, string>();
        var channels = (definition.Channels ?? []);

        if (channels.Count > ChannelLimits.MaxChannels) {
            errors.Add(item: $"channels declares {channels.Count} rows, exceeding the {ChannelLimits.MaxChannels}-channel ceiling.");
        }

        for (var index = 0; (index < channels.Count); index++) {
            var channel = channels[index];
            var path = $"channels[{index}]";

            if (channel is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(
                value: channel.Name,
                seen: names,
                path: path,
                field: "",
                errors: errors
            );

            if (!Enum.IsDefined(value: channel.Shape)) {
                errors.Add(item: $"{path}.shape '{channel.Shape}' is not a defined ChannelShape.");
            }

            var hasRole = (channel.Role is not null);

            if (hasRole == channel.Composition) {
                errors.Add(item: $"{path} must be exactly one of a role or a composition channel.");
            } else if (hasRole) {
                var role = channel.Role!.Value;

                if (!Enum.IsDefined(value: role)) {
                    errors.Add(item: $"{path}.role '{role}' is not a defined ChannelRole.");
                } else if (!roleClaims.TryAdd(
                    key: role,
                    value: (channel.Name ?? path)
                )) {
                    errors.Add(item: $"{path}.role '{role}' is already claimed by channel '{roleClaims[role]}'.");
                }

                // A role is a signed axis by construction — reverse/left/down are half the domain, not a degenerate
                // case — so a non-bipolar shape is meaningless to the motion model, never merely unusual. Refusing it
                // here makes WorldBody.Clamped's and SeatController.HeldIntent's hardcoded [-1,1] role range a
                // CONSEQUENCE of this rule instead of a lucky coincidence with the fold's shape-driven range
                // (Puck.Maths.FixedContributionFold, whose minimum/maximum WorldServer derives from this shape).
                if (
                    Enum.IsDefined(value: channel.Shape) &&
                    (channel.Shape != ChannelShape.Bipolar)
                ) {
                    errors.Add(item: $"{path}.shape '{channel.Shape}' on channel '{(channel.Name ?? path)}' must be '{ChannelShape.Bipolar}' for role '{role}' — every role channel is a signed axis.");
                }
            } else {
                if (!string.IsNullOrWhiteSpace(value: channel.Name)) {
                    _ = compositionNames.Add(item: channel.Name);
                }
            }

            if (!Enum.IsDefined(value: channel.Frame)) {
                errors.Add(item: $"{path}.frame '{channel.Frame}' is not a defined ChannelFrame.");
            } else if (
                (channel.Frame != ChannelFrame.World) &&
                (channel.Role is not (ChannelRole.MoveAdvance or ChannelRole.MoveStrafe))
            ) {
                errors.Add(item: $"{path}.frame '{channel.Frame}' is only meaningful on the MoveAdvance/MoveStrafe roles.");
            }

            if (channel.Threshold is { } threshold) {
                if (channel.Shape != ChannelShape.Binary) {
                    errors.Add(item: $"{path}.threshold is only meaningful on a binary channel.");
                } else if (!float.IsFinite(f: threshold)) {
                    errors.Add(item: $"{path}.threshold {threshold} is not a finite number.");
                } else {
                    // The fold compares the QUANTIZED raw threshold, never the authored float (WorldChannelTable.Compile
                    // runs the identical FixedQ4816.FromDouble conversion) — an authored value that quantizes to raw 0
                    // would make bit(v) = (v >= T) true for a NEGATIVE trusted delta on a neutral channel, since the
                    // authored-float check "(0, 1]" alone cannot see the representation the fold actually compares.
                    var quantizedThreshold = FixedQ4816.FromDouble(value: threshold);

                    if (
                        (quantizedThreshold.Value < 1L) ||
                        (quantizedThreshold > FixedQ4816.One)
                    ) {
                        errors.Add(item: $"{path}.threshold {threshold} quantizes to raw {quantizedThreshold.Value}, outside [1, {FixedQ4816.One.Value}] raw units.");
                    }
                }
            }
        }

        var moveFrame = ChannelFrame.World;
        var moveFramed = false;

        foreach (var channel in channels) {
            if (channel?.Role is not (ChannelRole.MoveAdvance or ChannelRole.MoveStrafe)) {
                continue;
            }

            if (!moveFramed) {
                moveFrame = channel.Frame;
                moveFramed = true;
            } else if (channel.Frame != moveFrame) {
                errors.Add(item: $"channels claiming MoveAdvance and MoveStrafe must declare the same frame ('{moveFrame}' and '{channel.Frame}' differ) — the pair rotates together.");

                break;
            }
        }

        foreach (var kit in (definition.Kits ?? [])) {
            if (kit is null) {
                continue;
            }

            // A Camera/Heading-framed pair is composed into world axes by the seat's client, which the sim's Heading
            // arm would then rotate a second time by the body's own heading — refuse the double rotation.
            if (
                (moveFrame != ChannelFrame.World) &&
                (kit.Motion.DeclaredMoveFrame != MotionMoveFrame.World)
            ) {
                errors.Add(item: $"kit '{kit.Name}' motion frame '{kit.Motion.DeclaredMoveFrame}' cannot carry a '{moveFrame}'-framed MoveAdvance/MoveStrafe pair — a framed pair needs the kit's World frame.");
            }

            if (!programs.TryGetValue(
                key: kit.BodyMotionProgram,
                value: out var program
            )) {
                continue;
            }

            foreach (var role in Enum.GetValues<ChannelRole>()) {
                if (
                    program.RequiresRole(role: role) &&
                    !roleClaims.ContainsKey(key: role)
                ) {
                    errors.Add(item: $"kit '{kit.Name}' body motion program '{program.Name}' requires channel role '{role}', but no declared channel claims it.");
                }
            }
        }

        return (AllNames: names, CompositionNames: compositionNames);
    }
    // Validated in the AUTHORED unit (seconds), not the compiled tick count: DurationTicks' rounding-up guarantee
    // only holds once its FixedQ4816 conversion sees a nonzero value, so a positive value below half a Q48.16 LSB
    // quantizes to zero ticks at any rate — LowerAfterSeconds must be checked as "positive AND does not quantize to
    // FixedQ4816.Zero", not merely positive. `defaultSeconds > ceilingSeconds` is also not exactly equivalent to the
    // ticks-domain `defaultTicks > ceilingTicks` comparison it replaces (DurationTicks' ceiling-rounding is
    // monotonic non-decreasing, so two seconds values under one tick apart can compile to the same count) — this
    // refuses strictly more, never less, which is the safe direction. Every seconds field is also checked finite
    // (NaN/Infinity evade the ordered comparisons and can overflow the checked casts inside Compile) and checked to
    // fit the runtime's int-typed compiled fields via FitsCompiledRange before Compile ever runs.
    private static void ValidateInputHold(WorldInputHoldAuthoring settings, uint ratePerSecond, int populationCapacity, List<string> errors) {
        var ceilingFinite = float.IsFinite(f: settings.CeilingSeconds);
        var defaultFinite = float.IsFinite(f: settings.DefaultSeconds);

        if (!ceilingFinite) {
            errors.Add(item: $"inputHold.ceilingSeconds {settings.CeilingSeconds} must be a finite number.");
        } else if (!FitsCompiledRange(
            seconds: settings.CeilingSeconds,
            ratePerSecond: ratePerSecond
        )) {
            errors.Add(item: $"inputHold.ceilingSeconds {settings.CeilingSeconds} compiles to more simulation ticks than the runtime's compiled field can hold at {ratePerSecond} Hz.");
        }

        if (!float.IsFinite(f: settings.LowerAfterSeconds)) {
            errors.Add(item: $"inputHold.lowerAfterSeconds {settings.LowerAfterSeconds} must be a finite number.");
        } else if (!(settings.LowerAfterSeconds > 0f)) {
            errors.Add(item: $"inputHold.lowerAfterSeconds {settings.LowerAfterSeconds} must be positive.");
        } else if (FixedQ4816.FromDouble(value: settings.LowerAfterSeconds) == FixedQ4816.Zero) {
            errors.Add(item: $"inputHold.lowerAfterSeconds {settings.LowerAfterSeconds} is positive but quantizes to zero in fixed point (Q48.16) — too small to represent as a duration at ANY rate; author a larger value.");
        } else if (!FitsCompiledRange(
            seconds: settings.LowerAfterSeconds,
            ratePerSecond: ratePerSecond
        )) {
            errors.Add(item: $"inputHold.lowerAfterSeconds {settings.LowerAfterSeconds} compiles to more simulation ticks than the runtime's compiled field can hold at {ratePerSecond} Hz.");
        }

        if (!defaultFinite) {
            errors.Add(item: $"inputHold.defaultSeconds {settings.DefaultSeconds} must be a finite number.");
        } else if (!FitsCompiledRange(
            seconds: settings.DefaultSeconds,
            ratePerSecond: ratePerSecond
        )) {
            errors.Add(item: $"inputHold.defaultSeconds {settings.DefaultSeconds} compiles to more simulation ticks than the runtime's compiled field can hold at {ratePerSecond} Hz.");
        }

        if (
            ceilingFinite &&
            defaultFinite &&
            (settings.DefaultSeconds > settings.CeilingSeconds)
        ) {
            errors.Add(item: $"inputHold.defaultSeconds {settings.DefaultSeconds} exceeds inputHold.ceilingSeconds {settings.CeilingSeconds}.");
        }

        if (settings.Participants is null) {
            return;
        }

        var bodies = new HashSet<int>();

        for (var index = 0; (index < settings.Participants.Count); index++) {
            var participant = settings.Participants[index];
            var path = $"inputHold.participants[{index}]";

            if (
                (participant.BodyIndex < 0) ||
                (participant.BodyIndex >= populationCapacity)
            ) {
                errors.Add(item: $"{path}.bodyIndex {participant.BodyIndex} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
            } else if (!bodies.Add(item: participant.BodyIndex)) {
                errors.Add(item: $"{path}.bodyIndex {participant.BodyIndex} is duplicated.");
            }

            if (!float.IsFinite(f: participant.Seconds)) {
                errors.Add(item: $"{path}.seconds {participant.Seconds} must be a finite number.");

                continue;
            }

            if (participant.Seconds > settings.CeilingSeconds) {
                errors.Add(item: $"{path}.seconds {participant.Seconds} exceeds inputHold.ceilingSeconds {settings.CeilingSeconds}.");
            }
            if (!FitsCompiledRange(
                seconds: participant.Seconds,
                ratePerSecond: ratePerSecond
            )) {
                errors.Add(item: $"{path}.seconds {participant.Seconds} compiles to more simulation ticks than the runtime's compiled field can hold at {ratePerSecond} Hz.");
            }
        }
    }
    private static HashSet<string> ValidateTargetRegisters(IReadOnlyList<WorldTargetRegister> registers, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < registers.Count); index++) {
            var register = registers[index];
            var path = $"targetRegisters[{index}]";

            if (register is null) {
                errors.Add(item: $"{path} is required.");
                continue;
            }
            RequireUniqueName(
                value: register.Name,
                seen: names,
                path: path,
                field: "name",
                errors: errors
            );
            RequirePositive(
                value: register.MaximumRange,
                name: $"{path}.maximumRange",
                errors: errors
            );
            RequireRange(
                value: register.MaximumHalfAngleDegrees,
                min: 0f,
                max: 180f,
                name: $"{path}.maximumHalfAngleDegrees",
                errors: errors,
                minExclusive: true
            );
        }

        return names;
    }
}
