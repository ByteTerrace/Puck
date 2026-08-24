namespace Puck.World;

/// <summary>The <c>hud.validate</c> door's whole refusal vocabulary — every reason <see cref="HudValidationException"/>
/// can be constructed with. <see cref="WorldDefinitionValidator"/>'s HUD checks each name exactly one of these; there
/// is no other way to construct an <see cref="HudValidationException"/>, so <c>world.refusals</c>' catalog (which
/// reads this enum's <see cref="RefusalAttribute"/> tags) is exhaustive over what this door can refuse with, by
/// construction rather than by convention — the same discipline <c>Client.Sdf.SdfRefusal</c> uses for
/// <c>sdf.decode</c>.</summary>
internal enum HudRefusal {
    /// <summary>The section's panel count exceeds its scope's ceiling — <see cref="WorldHudCapacity.MaxWorldPanels"/>
    /// for a world document, <see cref="WorldHudCapacity.MaxSeatPanels"/> for an identity-owned one.</summary>
    [Refusal(door: "hud.validate", condition: "the section's panel count exceeds its scope's ceiling: WorldHudCapacity.MaxWorldPanels for a world document, WorldHudCapacity.MaxSeatPanels for an identity-owned one", kind: RefusalKind.Verdict)]
    TooManyPanels,

    /// <summary>Two panel rows share the same id.</summary>
    [Refusal(door: "hud.validate", condition: "two panel rows share the same id", kind: RefusalKind.Verdict)]
    DuplicatePanelId,

    /// <summary>A panel's element count exceeds <see cref="WorldHudCapacity.MaxElementsPerPanel"/>.</summary>
    [Refusal(door: "hud.validate", condition: "a panel's element count exceeds WorldHudCapacity.MaxElementsPerPanel", kind: RefusalKind.Verdict)]
    TooManyElements,

    /// <summary>A HUD section names more than <see cref="WorldHudCapacity.MaxFrameSources"/> structurally unique
    /// frame sources.</summary>
    [Refusal(door: "hud.validate", condition: "a HUD section names more than WorldHudCapacity.MaxFrameSources structurally unique frame sources", kind: RefusalKind.Verdict)]
    TooManyFrameSources,

    /// <summary>Two element rows within the same panel share the same id.</summary>
    [Refusal(door: "hud.validate", condition: "two element rows within the same panel share the same id", kind: RefusalKind.Verdict)]
    DuplicateElementId,

    /// <summary>A panel or element rect is non-finite, or its width/height is not strictly positive.</summary>
    [Refusal(door: "hud.validate", condition: "a panel or element rect is non-finite, or its width/height is not strictly positive", kind: RefusalKind.Verdict)]
    InvalidRect,

    /// <summary>An element's <see cref="WorldHudElement.Binding"/> names a token outside the closed
    /// <see cref="HudBindingVocabulary"/>.</summary>
    [Refusal(door: "hud.validate", condition: "an element's binding names a token outside the closed HudBindingVocabulary", kind: RefusalKind.Verdict)]
    UnknownBinding,

    /// <summary>A player-scope (seat) panel declares <see cref="WorldHudLayer.Replace"/> — meaningless for a panel
    /// confined to one seat's viewport rather than the whole screen (there is no base slot to take over).</summary>
    [Refusal(door: "hud.validate", condition: "a player-scope panel declares the replace layer, which has no base slot to take over inside one seat's viewport", kind: RefusalKind.Verdict)]
    SeatPanelReplaceRefused,

    /// <summary>An element's <see cref="WorldHudElement.Template"/> violates the <see cref="HudTemplate"/> brace/escape
    /// grammar — an unterminated <c>{</c>, an empty <c>{}</c>, or a lone unescaped <c>}</c>.</summary>
    [Refusal(door: "hud.validate", condition: "an element's template violates the HudTemplate brace/escape grammar", kind: RefusalKind.Verdict)]
    MalformedTemplate,

    /// <summary>One of an element's <see cref="WorldHudElement.Template"/> placeholders names a token outside the
    /// closed <see cref="HudBindingVocabulary"/>, or (for a <c>state.*</c> token) a row/cell the document does not
    /// declare — the SAME existence check <see cref="UnknownBinding"/> applies to a plain <see cref="WorldHudElement.Binding"/>,
    /// named separately because a template's failure points at ONE placeholder among possibly several.</summary>
    [Refusal(door: "hud.validate", condition: "an element's template names a placeholder outside the closed HudBindingVocabulary, or a state row/cell the document does not declare", kind: RefusalKind.Verdict)]
    UnknownTemplatePlaceholder,

    /// <summary>An element carries both <see cref="WorldHudElement.Binding"/> and <see cref="WorldHudElement.Template"/>
    /// — exactly one live-value source is admitted, never both.</summary>
    [Refusal(door: "hud.validate", condition: "an element carries both a binding and a template — exactly one live-value source is admitted", kind: RefusalKind.Verdict)]
    TemplateBindingConflict,

    /// <summary>The defaults row's <see cref="WorldHudCursor"/> carries a non-finite or out-of-band value — a
    /// non-positive or over-1024 hover radius, a non-positive or over-64-pixel ring size, or an undefined palette
    /// role.</summary>
    [Refusal(door: "hud.validate", condition: "the defaults row's cursor policy carries a non-finite or out-of-band hover radius, ring size, or an undefined palette role", kind: RefusalKind.Verdict)]
    CursorInvalid,

    /// <summary>A <see cref="WorldHudElementKind.Frame"/> element's <see cref="WorldHudElement.Source"/> is missing
    /// or fails the shared frame-source gate (an undeclared view camera, an unrecognized camera sensor, an
    /// undeclared probe, or a malformed capture selector/profile).</summary>
    [Refusal(door: "hud.validate", condition: "a frame element's source is missing or fails the shared frame-source gate", kind: RefusalKind.Verdict)]
    FrameSourceInvalid,

    /// <summary>A non-<see cref="WorldHudElementKind.Frame"/> element carries a <see cref="WorldHudElement.Source"/>.</summary>
    [Refusal(door: "hud.validate", condition: "a non-frame element carries a source", kind: RefusalKind.Verdict)]
    FrameSourceNotAllowed,

    /// <summary>A <see cref="WorldHudElementKind.Frame"/> element's <see cref="WorldHudElement.Radius"/> is
    /// non-finite or negative.</summary>
    [Refusal(door: "hud.validate", condition: "a frame element's radius is non-finite or negative", kind: RefusalKind.Verdict)]
    InvalidFrameRadius,

    /// <summary>A <see cref="WorldHudElementKind.Frame"/> element's <see cref="WorldHudElement.Opacity"/> is
    /// non-finite or outside [0, 1].</summary>
    [Refusal(door: "hud.validate", condition: "a frame element's opacity is non-finite or outside [0, 1]", kind: RefusalKind.Verdict)]
    InvalidFrameOpacity,
    /// <summary>A <see cref="WorldHudElementKind.Frame"/> element authors both <see cref="WorldHudElement.Source"/>
    /// and <see cref="WorldHudElement.Sources"/>, or an empty or over-long candidate list.</summary>
    [Refusal(door: "hud.validate", condition: "a frame element authors both source and sources, or sources is empty or exceeds WorldHudCapacity.MaxFrameCandidatesPerElement", kind: RefusalKind.Verdict)]
    FrameCandidatesInvalid,
    /// <summary>A frame candidate's <c>when</c> predicate is malformed.</summary>
    [Refusal(door: "hud.validate", condition: "a frame candidate's when predicate is malformed", kind: RefusalKind.Verdict)]
    FramePredicateInvalid,
    /// <summary>A <see cref="WorldHudElementKind.Frame"/> element's <see cref="WorldHudElement.FadeSeconds"/> is
    /// non-finite or negative.</summary>
    [Refusal(door: "hud.validate", condition: "a frame element's fadeSeconds is non-finite or negative", kind: RefusalKind.Verdict)]
    InvalidFrameFade,
    /// <summary>A panel, defaults, or cursor <c>visible</c> predicate is malformed.</summary>
    [Refusal(door: "hud.validate", condition: "a visible predicate is malformed", kind: RefusalKind.Verdict)]
    VisiblePredicateInvalid,
}
/// <summary>
/// Row-level HUD validation shared by <see cref="WorldDefinitionValidator"/> (world-scope panels, capped by
/// <see cref="WorldHudCapacity.MaxElementsPerPanel"/>) and the identity-owned world validator (the one
/// seat-scope panel a profile authors, capped by <see cref="WorldHudCapacity.MaxElementsPerSeatPanel"/>) — the same
/// rect-sanity, element-id-uniqueness, and closed-binding-vocabulary checks apply at both scopes, so this is the one
/// place they can never drift apart. Every check throws an enum-reasoned <see cref="HudValidationException"/> at its
/// first violation (the <c>sdf.decode</c>/<c>SdfRefusal</c> discipline).
/// </summary>
internal static class HudRowValidation {
    // Shared by a plain Binding and every Template placeholder: parse against the closed HudBindingVocabulary, then
    // — for a state.* token — resolve its existence against the document's OWN declared state rows (world scope only
    // carries a real map; seat scope passes null, refusing every state.* token, same as ValidateElement's remarks).
    // 'refusal' lets the two callers report under their own HudRefusal reason while sharing every other line.
    private static void ValidateBindingToken(string token, string path, IReadOnlyDictionary<string, WorldStateRow>? stateRows, HudRefusal refusal) {
        if (!HudBindingVocabulary.TryParse(
            binding: out var parsed,
            token: token
        )) {
            throw new HudValidationException(
                message: $"{path} '{token}' is not in the closed HudBindingVocabulary.",
                reason: refusal
            );
        }

        if (parsed.Kind == HudBindingKind.StateNamed) {
            if (
                (stateRows is null) ||
                !stateRows.TryGetValue(
                key: parsed.StateName!,
                value: out var row
            )
            ) {
                throw new HudValidationException(
                    message: $"{path} '{token}' names no declared state row.",
                    reason: refusal
                );
            }

            if (
                (parsed.StateCellKey is { } cellKey) &&
                !row.HasCell(key: cellKey)
            ) {
                throw new HudValidationException(
                    reason: refusal,
                    message: $"{path} '{token}' names no declared cell '{cellKey}' on state row '{parsed.StateName}'."
                );
            }
        }
    }

    // Bridges ValidateFrameSource's collect-many-errors style (a List<string>, shared with every other screen/probe
    // frame-source site) into this door's throw-on-first-violation style: run the shared gate into a scratch list,
    // then fold it into ONE HudValidationException naming every collected line, rather than forking a second,
    // throwing-only copy of the gate.
    private static void ValidateFrameElementSource(WorldHudElement element, string path, WorldDefinition? definition, HashSet<string>? cameras) {
        var isFrame = (element.Kind == WorldHudElementKind.Frame);

        if (!isFrame) {
            if ((element.Source is not null) || (element.Sources is not null)) {
                throw new HudValidationException(
                    message: $"{path}.source is only legal on a 'frame' kind element (got '{element.Kind}').",
                    reason: HudRefusal.FrameSourceNotAllowed
                );
            }

            return;
        }

        if ((element.Source is not null) && (element.Sources is not null)) {
            throw new HudValidationException(
                message: $"{path} authors both source and sources — a single unconditional source is 'source', a ranked list is 'sources'.",
                reason: HudRefusal.FrameCandidatesInvalid
            );
        }

        var candidates = element.FrameCandidates;

        if ((candidates.Count == 0) || (candidates.Count > WorldHudCapacity.MaxFrameCandidatesPerElement)) {
            throw new HudValidationException(
                message: $"{path} needs 1..{WorldHudCapacity.MaxFrameCandidatesPerElement} frame source candidates (got {candidates.Count}).",
                reason: HudRefusal.FrameCandidatesInvalid
            );
        }

        for (var index = 0; (index < candidates.Count); index++) {
            var candidate = candidates[index];
            var candidatePath = ((element.Sources is null)
                ? $"{path}.source"
                : $"{path}.sources[{index}]"
            );
            var frameErrors = new List<string>();

            WorldDefinitionValidator.ValidateFrameSource(
                cameras: (cameras ?? []),
                definition: definition!,
                errors: frameErrors,
                path: ((element.Sources is null) ? candidatePath : $"{candidatePath}.source"),
                source: candidate?.Source
            );

            if (frameErrors.Count > 0) {
                throw new HudValidationException(
                    message: string.Join(separator: " ", values: frameErrors),
                    reason: HudRefusal.FrameSourceInvalid
                );
            }

            var predicateErrors = new List<string>();

            WorldDefinitionValidator.ValidateOverlayPredicate(
                definition: definition,
                errors: predicateErrors,
                path: $"{candidatePath}.when",
                predicate: candidate?.When
            );

            if (predicateErrors.Count > 0) {
                throw new HudValidationException(
                    message: string.Join(separator: " ", values: predicateErrors),
                    reason: HudRefusal.FramePredicateInvalid
                );
            }
        }

        if (!float.IsFinite(f: element.FadeSeconds) || (element.FadeSeconds < 0f)) {
            throw new HudValidationException(
                message: $"{path}.fadeSeconds must be finite and non-negative (got {element.FadeSeconds}).",
                reason: HudRefusal.InvalidFrameFade
            );
        }

        if (
            !float.IsFinite(f: element.Radius) ||
            (element.Radius < 0f)
        ) {
            throw new HudValidationException(
                reason: HudRefusal.InvalidFrameRadius,
                message: $"{path}.radius must be finite and non-negative (got {element.Radius})."
            );
        }

        if (
            !float.IsFinite(f: element.Opacity) ||
            (element.Opacity < 0f) ||
            (element.Opacity > 1f)
        ) {
            throw new HudValidationException(
                reason: HudRefusal.InvalidFrameOpacity,
                message: $"{path}.opacity must be finite and within [0, 1] (got {element.Opacity})."
            );
        }
    }
    /// <summary>Validates one element row: a required id unique within <paramref name="elementIds"/>, a valid rect,
    /// and (when present) a binding token in the closed <see cref="HudBindingVocabulary"/> — for a
    /// <c>state.&lt;row&gt;</c> token, additionally that the row resolves against <paramref name="stateRows"/>
    /// (world scope only carries a real map; seat scope passes <see langword="null"/>, so every <c>state.*</c> token
    /// refuses there — a player-profile document is authored independent of any particular world and can never know
    /// which state rows one will declare); for a <c>state.&lt;row&gt;.&lt;key&gt;</c> token, additionally that the
    /// key resolves against that row's own authored cells — a binding naming a row that exists but no such cell
    /// refuses exactly like one naming no row at all, never a silently blank panel. A <see cref="WorldHudElementKind.Frame"/>
    /// element additionally requires a valid <see cref="WorldHudElement.Source"/> (the shared <c>ValidateFrameSource</c>
    /// gate); every other kind refuses one. <paramref name="definition"/>/<paramref name="cameras"/> are required only
    /// for that check — <see langword="null"/> is legal on a caller that never authors a Frame element.</summary>
    /// <param name="element">The element to validate.</param>
    /// <param name="path">The dotted path to name in a thrown message.</param>
    /// <param name="elementIds">The owning panel's id set so far (mutated: the element's id is added on success).</param>
    /// <param name="stateRows">The world's declared <c>state</c> rows by name, or <see langword="null"/> when no such
    /// context exists (seat scope). A <c>state.*</c> token's trailing <c>.$target</c> facet (<see cref="HudBinding.Target"/>)
    /// resolves against this SAME map — existence, never live value, is what validation checks.</param>
    /// <param name="definition">The owning document, for a Frame element's declared-probe lookup, or
    /// <see langword="null"/> when the caller carries no Frame element.</param>
    /// <param name="cameras">The document's declared <c>cameras[]</c> names, for a Frame element's <c>view</c> arm, or
    /// <see langword="null"/> when the caller carries no Frame element.</param>
    /// <exception cref="HudValidationException">The element is invalid.</exception>
    public static void ValidateElement(WorldHudElement element, string path, HashSet<string> elementIds, IReadOnlyDictionary<string, WorldStateRow>? stateRows, WorldDefinition? definition = null, HashSet<string>? cameras = null) {
        if (string.IsNullOrWhiteSpace(value: element.Id)) {
            throw new HudValidationException(
                message: $"{path}.id is required.",
                reason: HudRefusal.DuplicateElementId
            );
        }

        if (!elementIds.Add(item: element.Id)) {
            throw new HudValidationException(
                reason: HudRefusal.DuplicateElementId,
                message: $"{path}.id '{element.Id}' is duplicated within its owning panel."
            );
        }

        ValidateRect(
            rect: element.Rect,
            path: $"{path}.rect"
        );

        ValidateFrameElementSource(
            cameras: cameras,
            definition: definition,
            element: element,
            path: path
        );

        var hasBinding = (element.Binding is { Length: > 0 });
        var hasTemplate = (element.Template is { Length: > 0 });

        if (
            hasBinding &&
            hasTemplate
        ) {
            throw new HudValidationException(
                message: $"{path} carries both 'binding' and 'template' — a template is a richer binding source that composes many facts into one string, never both on the same element.",
                reason: HudRefusal.TemplateBindingConflict
            );
        }

        if (hasBinding) {
            ValidateBindingToken(
                token: element.Binding!,
                path: $"{path}.binding",
                stateRows: stateRows,
                refusal: HudRefusal.UnknownBinding
            );
        }

        if (hasTemplate) {
            if (!HudTemplate.TryEnumeratePlaceholders(
                template: element.Template!,
                placeholders: out var placeholders,
                error: out var templateError
            )) {
                throw new HudValidationException(
                    message: $"{path}.template {templateError}.",
                    reason: HudRefusal.MalformedTemplate
                );
            }

            foreach (var placeholder in placeholders) {
                ValidateBindingToken(
                    path: $"{path}.template placeholder",
                    refusal: HudRefusal.UnknownTemplatePlaceholder,
                    stateRows: stateRows,
                    token: placeholder
                );
            }
        }
    }
    /// <summary>Validates a panel's whole element list against a caller-supplied cap (world scope passes
    /// <see cref="WorldHudCapacity.MaxElementsPerPanel"/>; seat scope passes
    /// <see cref="WorldHudCapacity.MaxElementsPerSeatPanel"/>): the count against the cap, then every element via
    /// <see cref="ValidateElement"/>.</summary>
    /// <param name="elements">The panel's elements.</param>
    /// <param name="panelPath">The owning panel's dotted path (for messages).</param>
    /// <param name="maxElements">The element-count ceiling for this scope.</param>
    /// <param name="stateRows">The world's declared <c>state</c> rows by name, or <see langword="null"/> when no such
    /// context exists (seat scope) — see <see cref="ValidateElement"/>.</param>
    /// <param name="definition">The owning document, threaded to <see cref="ValidateElement"/> for a Frame element's
    /// source gate, or <see langword="null"/> for a caller that never authors one.</param>
    /// <param name="cameras">The document's declared <c>cameras[]</c> names, threaded to <see cref="ValidateElement"/>
    /// for a Frame element's <c>view</c> arm, or <see langword="null"/> for a caller that never authors one.</param>
    /// <exception cref="HudValidationException">The element list is invalid.</exception>
    public static void ValidateElements(IReadOnlyList<WorldHudElement> elements, string panelPath, int maxElements, IReadOnlyDictionary<string, WorldStateRow>? stateRows = null, WorldDefinition? definition = null, HashSet<string>? cameras = null) {
        if (elements.Count > maxElements) {
            throw new HudValidationException(
                reason: HudRefusal.TooManyElements,
                message: $"{panelPath} elements count {elements.Count} exceeds the maximum of {maxElements}."
            );
        }

        var elementIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < elements.Count); index++) {
            ValidateElement(
                cameras: cameras,
                definition: definition,
                element: elements[index],
                path: $"{panelPath}.elements[{index}]",
                elementIds: elementIds,
                stateRows: stateRows
            );
        }
    }
    /// <summary>Validates a normalized rect: every component finite, width/height strictly positive.</summary>
    /// <param name="rect">The rect to validate.</param>
    /// <param name="path">The dotted path to name in a thrown message.</param>
    /// <exception cref="HudValidationException">The rect is invalid.</exception>
    public static void ValidateRect(WorldHudRect rect, string path) {
        if (
            !float.IsFinite(f: rect.X) ||
            !float.IsFinite(f: rect.Y) ||
            !float.IsFinite(f: rect.Width) ||
            !float.IsFinite(f: rect.Height)
        ) {
            throw new HudValidationException(
                message: $"{path} carries a non-finite component.",
                reason: HudRefusal.InvalidRect
            );
        }

        if (
            (rect.Width <= 0f) ||
            (rect.Height <= 0f)
        ) {
            throw new HudValidationException(
                reason: HudRefusal.InvalidRect,
                message: $"{path} width/height must be strictly positive (got {rect.Width}x{rect.Height})."
            );
        }
    }
}
/// <summary>Thrown by <see cref="WorldDefinitionValidator"/>'s HUD checks, naming exactly one <see cref="HudRefusal"/>
/// reason. <see cref="WorldDefinitionValidator.Validate"/> catches this and folds <see cref="Exception.Message"/> into
/// the whole-document error list — the enum-reasoned throw and the aggregate error list are not in tension: the throw
/// is how the section decides, the catch is how it reports alongside every other section's findings.</summary>
/// <param name="reason">Which of this door's finite refusal reasons fired.</param>
/// <param name="message">The human-readable, refusal-named message.</param>
internal sealed class HudValidationException(HudRefusal reason, string message) : Exception(message) {
    /// <summary>Gets which of this door's finite refusal reasons fired.</summary>
    public HudRefusal Reason { get; } = reason;
}
