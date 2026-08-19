using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

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
    private static void ValidateBindingBar(WorldBindingBarAuthoring? authoring, string path, List<string> errors) {
        if (authoring is null) {
            return;
        }

        ValidateOverlayPredicate(
            errors: errors,
            path: $"{path}.visible",
            predicate: authoring.Visible
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
            // No explicit count ceiling: known-name + uniqueness below already bound the slot set by the button
            // catalog itself, which is what the overlay reservation sizes from.
            var seenButtons = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < authoring.SlotSet.Count); index++) {
                var name = authoring.SlotSet[index];
                var slotPath = $"{path}.slotSet[{index}]";

                if (string.IsNullOrWhiteSpace(value: name)) {
                    errors.Add(item: $"{slotPath} is required.");
                } else if (!seenButtons.Add(item: name)) {
                    errors.Add(item: $"{slotPath} '{name}' is duplicated.");
                } else if (
                    (GamepadButtonVocabularyHook.IsKnownButtonName is { } isKnown) &&
                    !isKnown(name)
                ) {
                    errors.Add(item: $"{slotPath} '{name}' is not a declared GamepadButtons name.");
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

                if (!float.IsFinite(f: bank.OffsetX)) {
                    errors.Add(item: $"{bankPath}.offsetX {bank.OffsetX} must be a finite number.");
                }

                if (!float.IsFinite(f: bank.OffsetY)) {
                    errors.Add(item: $"{bankPath}.offsetY {bank.OffsetY} must be a finite number.");
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
            value: layout.ButtonSize,
            name: $"{path}.layout.buttonSize",
            errors: errors
        );
        RequireNonNegative(
            value: layout.CenterGap,
            name: $"{path}.layout.centerGap",
            errors: errors
        );

        if (
            !float.IsFinite(f: layout.AnchorOffsetY) ||
            (layout.AnchorOffsetY < 0f) ||
            (layout.AnchorOffsetY > 1f)
        ) {
            errors.Add(item: $"{path}.layout.anchorOffsetY {layout.AnchorOffsetY} is outside 0..1.");
        }

        RequireNonNegative(
            value: layout.GlyphOffsetRatio,
            name: $"{path}.layout.glyphOffsetRatio",
            errors: errors
        );
        RequirePositive(
            value: layout.GlyphSizeRatio,
            name: $"{path}.layout.glyphSizeRatio",
            errors: errors
        );
        RequirePositive(
            value: layout.Scale,
            name: $"{path}.layout.scale",
            errors: errors
        );
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
                errors: errors
            );
        }

        try {
            var compiled = BindingProfile.Compile(document: WorldBindingComposer.Compose(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: layers)));

            if (compiled.Modifiers.Count > WorldBindingBarCapacity.MaxModifiers) {
                errors.Add(item: $"bindingOverlays compose {compiled.Modifiers.Count} modifiers, exceeding the {WorldBindingBarCapacity.MaxModifiers}-modifier pip ceiling.");
            }

            ValidateBindingBarPageReferences(
                errors: errors,
                overlays: overlays,
                profile: compiled
            );

            // A bound action's icon string, checked against the icon table ONLY when some document in the basis
            // chain authored one (see ValidateIconography's Absent gate) — no authored icons.icons means every
            // icon string draws a blank plate, never a refusal.
            if (iconsAuthored) {
                foreach (var pageId in compiled.PageIds) {
                    if (!compiled.TryGetPageView(
                        pageId: pageId,
                        view: out var view
                    )) {
                        continue;
                    }

                    foreach (var button in view.Buttons) {
                        if (
                            !string.IsNullOrEmpty(value: button.Icon) &&
                            !iconNames.Contains(item: button.Icon)
                        ) {
                            errors.Add(item: $"bindingOverlays page '{pageId}' button '{button.Command}' icon '{button.Icon}' names no row in icons.icons.");
                        }
                    }
                }
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
