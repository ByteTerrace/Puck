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

        if (
            !float.IsFinite(f: authoring.MultiSeatAlpha) ||
            (authoring.MultiSeatAlpha < 0f) ||
            (authoring.MultiSeatAlpha > 1f)
        ) {
            errors.Add(item: $"{path}.multiSeatAlpha {authoring.MultiSeatAlpha} is outside 0..1.");
        }

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

                if (string.IsNullOrWhiteSpace(value: source)) {
                    errors.Add(item: $"{slotPath} is required.");
                } else if (!seenSources.Add(item: source)) {
                    errors.Add(item: $"{slotPath} '{source}' is duplicated.");
                } else if (
                    (InputSourceVocabularyHook.IsKnownSourceId is { } isKnown) &&
                    !isKnown(source)
                ) {
                    errors.Add(item: $"{slotPath} '{source}' is not a declared input source id.");
                }
            }
        }

        if (authoring.Banks is null) {
            errors.Add(item: $"{path}.banks is required.");
        } else if (authoring.Banks.Count == 0) {
            errors.Add(item: $"{path}.banks must declare at least one bank.");
        } else if (authoring.Banks.Count > WorldBindingBarCapacity.MaxBanks) {
            errors.Add(item: $"{path}.banks declares {authoring.Banks.Count} entries, exceeding the {WorldBindingBarCapacity.MaxBanks}-bank ceiling.");
        } else {
            var seenBanks = new HashSet<string>(comparer: StringComparer.Ordinal);
            var seenOrders = new HashSet<int>();

            for (var index = 0; (index < authoring.Banks.Count); index++) {
                var bank = authoring.Banks[index];
                var bankPath = $"{path}.banks[{index}]";

                if (bank is null) {
                    errors.Add(item: $"{bankPath} is required.");

                    continue;
                }

                if (string.IsNullOrWhiteSpace(value: bank.Id)) {
                    errors.Add(item: $"{bankPath}.id is required.");
                } else if (!seenBanks.Add(item: bank.Id)) {
                    errors.Add(item: $"{bankPath}.id '{bank.Id}' is duplicated.");
                }

                if (string.IsNullOrWhiteSpace(value: bank.PageId)) {
                    errors.Add(item: $"{bankPath}.pageId is required.");
                }

                // The stack arrangement is derived from order alone, so a repeated order would place two banks on
                // top of each other with nothing to say which is in front.
                if (bank.Order < 0) {
                    errors.Add(item: $"{bankPath}.order {bank.Order} must not be negative.");
                } else if (!seenOrders.Add(item: bank.Order)) {
                    errors.Add(item: $"{bankPath}.order {bank.Order} is duplicated.");
                }

                if (
                    (bank.OffsetX is { } offsetX) &&
                    !float.IsFinite(f: offsetX)
                ) {
                    errors.Add(item: $"{bankPath}.offsetX {offsetX} must be a finite number.");
                }

                if (
                    (bank.OffsetY is { } offsetY) &&
                    !float.IsFinite(f: offsetY)
                ) {
                    errors.Add(item: $"{bankPath}.offsetY {offsetY} must be a finite number.");
                }

                if (
                    !float.IsFinite(f: bank.Alpha) ||
                    (bank.Alpha < 0f) ||
                    (bank.Alpha > 1f)
                ) {
                    errors.Add(item: $"{bankPath}.alpha {bank.Alpha} is outside 0..1.");
                }

                if (
                    (bank.ActiveAlpha is { } activeAlpha) &&
                    (!float.IsFinite(f: activeAlpha) || (activeAlpha < 0f) || (activeAlpha > 1f))
                ) {
                    errors.Add(item: $"{bankPath}.activeAlpha {activeAlpha} is outside 0..1.");
                }
            }
        }

        if (authoring.Layout is not { } layout) {
            return;
        }

        RequirePositive(
            value: layout.Scale,
            name: $"{path}.layout.scale",
            errors: errors
        );
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.buttonSize", value: layout.ButtonSize);
        RequireOptionalNonNegative(errors: errors, name: $"{path}.layout.centerGap", value: layout.CenterGap);

        if (
            (layout.AnchorOffsetY is { } anchorOffsetY) &&
            (!float.IsFinite(f: anchorOffsetY) || (anchorOffsetY < 0f) || (anchorOffsetY > 1f))
        ) {
            errors.Add(item: $"{path}.layout.anchorOffsetY {anchorOffsetY} is outside 0..1.");
        }

        RequireOptionalNonNegative(errors: errors, name: $"{path}.layout.glyphOffsetRatio", value: layout.GlyphOffsetRatio);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.glyphSizeRatio", value: layout.GlyphSizeRatio);
        RequireOptionalNonNegative(errors: errors, name: $"{path}.layout.centerRowLift", value: layout.CenterRowLift);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.centerSlotSpacing", value: layout.CenterSlotSpacing);
        RequireOptionalNonNegative(errors: errors, name: $"{path}.layout.exoticRowLift", value: layout.ExoticRowLift);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.exoticSlotSpacing", value: layout.ExoticSlotSpacing);
        RequireOptionalFinite(errors: errors, name: $"{path}.layout.badgeCorner", value: layout.BadgeCorner);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.modifierHalfRatio", value: layout.ModifierHalfRatio);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.modifierSpacingRatio", value: layout.ModifierSpacingRatio);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.modifierGlyphRatio", value: layout.ModifierGlyphRatio);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.labelCellRatio", value: layout.LabelCellRatio);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.labelCellMinPx", value: layout.LabelCellMinPx);
        RequireOptionalFinite(errors: errors, name: $"{path}.layout.labelGapRatio", value: layout.LabelGapRatio);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.hintCellRatio", value: layout.HintCellRatio);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.hintCellMinPx", value: layout.HintCellMinPx);
        RequireOptionalPositive(errors: errors, name: $"{path}.layout.hintLineStepRatio", value: layout.HintLineStepRatio);
        RequireOptionalFinite(errors: errors, name: $"{path}.layout.hintBaseGapRatio", value: layout.HintBaseGapRatio);
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
        if (
            (preferences?.Scale is { } scale) &&
            (!float.IsFinite(f: scale) || (scale <= 0f))
        ) {
            errors.Add(item: $"{path}.scale {scale} must be a finite positive number.");
        }

        if (
            (preferences?.ContrastBoost is { } contrastBoost) &&
            (!float.IsFinite(f: contrastBoost) || (contrastBoost < 1f) || (contrastBoost > 2f))
        ) {
            errors.Add(item: $"{path}.contrastBoost {contrastBoost} must be a finite number in [1, 2].");
        }

        if (
            (preferences?.UiScale is { } uiScale) &&
            (!float.IsFinite(f: uiScale) || (uiScale < 0.5f) || (uiScale > 2f))
        ) {
            errors.Add(item: $"{path}.uiScale {uiScale} must be a finite number in [0.5, 2].");
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

            if (string.IsNullOrWhiteSpace(value: overlay.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: overlay.Id)) {
                errors.Add(item: $"{path}.id '{overlay.Id}' is duplicated.");
            }

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

            if (string.IsNullOrWhiteSpace(value: channel.Name)) {
                errors.Add(item: $"{path} requires a non-empty name.");
            } else if (!names.Add(item: channel.Name)) {
                errors.Add(item: $"{path} duplicates the name '{channel.Name}'.");
            }

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
            if (string.IsNullOrWhiteSpace(value: register.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!names.Add(item: register.Name)) {
                errors.Add(item: $"{path}.name '{register.Name}' is duplicated.");
            }
            RequirePositive(
                value: register.MaximumRange,
                name: $"{path}.maximumRange",
                errors: errors
            );
            ValidateHalfAngle(
                value: register.MaximumHalfAngleDegrees,
                name: $"{path}.maximumHalfAngleDegrees",
                errors: errors
            );
        }

        return names;
    }
}
