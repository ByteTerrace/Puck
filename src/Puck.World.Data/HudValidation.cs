namespace Puck.World;

/// <summary>The <c>hud.validate</c> door's whole refusal vocabulary — every reason <see cref="HudValidationException"/>
/// can be constructed with. <see cref="WorldDefinitionValidator"/>'s HUD checks each name exactly one of these; there
/// is no other way to construct an <see cref="HudValidationException"/>, so <c>world.refusals</c>' catalog (which
/// reads this enum's <see cref="RefusalAttribute"/> tags) is exhaustive over what this door can refuse with, by
/// construction rather than by convention — the same discipline <c>Client.Sdf.SdfRefusal</c> uses for
/// <c>sdf.decode</c>.</summary>
internal enum HudRefusal {
    /// <summary>The section's panel count exceeds <see cref="WorldHudCapacity.MaxWorldPanels"/>.</summary>
    [Refusal(door: "hud.validate", condition: "the section's panel count exceeds WorldHudCapacity.MaxWorldPanels", kind: RefusalKind.Verdict)]
    TooManyPanels,

    /// <summary>Two panel rows share the same id.</summary>
    [Refusal(door: "hud.validate", condition: "two panel rows share the same id", kind: RefusalKind.Verdict)]
    DuplicatePanelId,

    /// <summary>A panel's element count exceeds <see cref="WorldHudCapacity.MaxElementsPerPanel"/>.</summary>
    [Refusal(door: "hud.validate", condition: "a panel's element count exceeds WorldHudCapacity.MaxElementsPerPanel", kind: RefusalKind.Verdict)]
    TooManyElements,

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
}

/// <summary>
/// Row-level HUD validation shared by <see cref="WorldDefinitionValidator"/> (world-scope panels, capped by
/// <see cref="WorldHudCapacity.MaxElementsPerPanel"/>) and the identity-owned world validator (the one
/// seat-scope panel a profile authors, capped by <see cref="WorldHudCapacity.MaxElementsPerSeatPanel"/>) — the SAME
/// rect-sanity, element-id-uniqueness, and closed-binding-vocabulary checks apply at both scopes, so this is the one
/// place they can never drift apart. Every check throws an ENUM-REASONED <see cref="HudValidationException"/> at its
/// FIRST violation (the <c>sdf.decode</c>/<c>SdfRefusal</c> discipline).
/// </summary>
internal static class HudRowValidation {
    /// <summary>Validates a normalized rect: every component finite, width/height strictly positive.</summary>
    /// <param name="rect">The rect to validate.</param>
    /// <param name="path">The dotted path to name in a thrown message.</param>
    /// <exception cref="HudValidationException">The rect is invalid.</exception>
    public static void ValidateRect(WorldHudRect rect, string path) {
        if (!float.IsFinite(f: rect.X) || !float.IsFinite(f: rect.Y) || !float.IsFinite(f: rect.Width) || !float.IsFinite(f: rect.Height)) {
            throw new HudValidationException(reason: HudRefusal.InvalidRect, message: $"{path} carries a non-finite component.");
        }

        if ((rect.Width <= 0f) || (rect.Height <= 0f)) {
            throw new HudValidationException(reason: HudRefusal.InvalidRect, message: $"{path} width/height must be strictly positive (got {rect.Width}x{rect.Height}).");
        }
    }

    /// <summary>Validates one element row: a required id unique within <paramref name="elementIds"/>, a valid rect,
    /// and (when present) a binding token in the closed <see cref="HudBindingVocabulary"/> — for a
    /// <c>state.&lt;row&gt;</c> token, ADDITIONALLY that the row resolves against <paramref name="stateRows"/>
    /// (world scope only carries a real map; seat scope passes <see langword="null"/>, so every <c>state.*</c> token
    /// refuses there — a player-profile document is authored independent of any particular world and can never know
    /// which state rows one will declare); for a <c>state.&lt;row&gt;.&lt;key&gt;</c> token, ADDITIONALLY that the
    /// key resolves against that row's OWN authored cells — a binding naming a row that exists but no such cell
    /// refuses exactly like one naming no row at all, never a silently blank panel.</summary>
    /// <param name="element">The element to validate.</param>
    /// <param name="path">The dotted path to name in a thrown message.</param>
    /// <param name="elementIds">The owning panel's id set so far (mutated: the element's id is added on success).</param>
    /// <param name="stateRows">The world's declared <c>state</c> rows by name, or <see langword="null"/> when no such
    /// context exists (seat scope).</param>
    /// <exception cref="HudValidationException">The element is invalid.</exception>
    public static void ValidateElement(WorldHudElement element, string path, HashSet<string> elementIds, IReadOnlyDictionary<string, WorldStateRow>? stateRows) {
        if (string.IsNullOrWhiteSpace(value: element.Id)) {
            throw new HudValidationException(reason: HudRefusal.DuplicateElementId, message: $"{path}.id is required.");
        }

        if (!elementIds.Add(item: element.Id)) {
            throw new HudValidationException(reason: HudRefusal.DuplicateElementId, message: $"{path}.id '{element.Id}' is duplicated within its owning panel.");
        }

        ValidateRect(rect: element.Rect, path: $"{path}.rect");

        var hasBinding = (element.Binding is { Length: > 0 });
        var hasTemplate = (element.Template is { Length: > 0 });

        if (hasBinding && hasTemplate) {
            throw new HudValidationException(reason: HudRefusal.TemplateBindingConflict, message: $"{path} carries both 'binding' and 'template' — a template is a richer binding source that composes many facts into one string, never both on the same element.");
        }

        if (hasBinding) {
            ValidateBindingToken(token: element.Binding!, path: $"{path}.binding", stateRows: stateRows, refusal: HudRefusal.UnknownBinding);
        }

        if (hasTemplate) {
            if (!HudTemplate.TryEnumeratePlaceholders(template: element.Template!, placeholders: out var placeholders, error: out var templateError)) {
                throw new HudValidationException(reason: HudRefusal.MalformedTemplate, message: $"{path}.template {templateError}.");
            }

            foreach (var placeholder in placeholders) {
                ValidateBindingToken(token: placeholder, path: $"{path}.template placeholder", stateRows: stateRows, refusal: HudRefusal.UnknownTemplatePlaceholder);
            }
        }
    }

    // Shared by a plain Binding and every Template placeholder: parse against the closed HudBindingVocabulary, then
    // — for a state.* token — resolve its existence against the document's OWN declared state rows (world scope only
    // carries a real map; seat scope passes null, refusing every state.* token, same as ValidateElement's remarks).
    // 'refusal' lets the two callers report under their own HudRefusal reason while sharing every other line.
    private static void ValidateBindingToken(string token, string path, IReadOnlyDictionary<string, WorldStateRow>? stateRows, HudRefusal refusal) {
        if (!HudBindingVocabulary.TryParse(token: token, binding: out var parsed)) {
            throw new HudValidationException(reason: refusal, message: $"{path} '{token}' is not in the closed HudBindingVocabulary.");
        }

        if (parsed.Kind == HudBindingKind.StateNamed) {
            if ((stateRows is null) || !stateRows.TryGetValue(key: parsed.StateName!, value: out var row)) {
                throw new HudValidationException(reason: refusal, message: $"{path} '{token}' names no declared state row.");
            }

            if ((parsed.StateCellKey is { } cellKey) && !row.HasCell(key: cellKey)) {
                throw new HudValidationException(reason: refusal, message: $"{path} '{token}' names no declared cell '{cellKey}' on state row '{parsed.StateName}'.");
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
    /// <exception cref="HudValidationException">The element list is invalid.</exception>
    public static void ValidateElements(IReadOnlyList<WorldHudElement> elements, string panelPath, int maxElements, IReadOnlyDictionary<string, WorldStateRow>? stateRows = null) {
        if (elements.Count > maxElements) {
            throw new HudValidationException(reason: HudRefusal.TooManyElements, message: $"{panelPath} elements count {elements.Count} exceeds the maximum of {maxElements}.");
        }

        var elementIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < elements.Count); index++) {
            ValidateElement(element: elements[index], path: $"{panelPath}.elements[{index}]", elementIds: elementIds, stateRows: stateRows);
        }
    }
}

/// <summary>Thrown by <see cref="WorldDefinitionValidator"/>'s HUD checks, naming exactly one <see cref="HudRefusal"/>
/// reason. <see cref="WorldDefinitionValidator.Validate"/> catches this and folds <see cref="Exception.Message"/> into
/// the whole-document error list — the enum-reasoned throw and the aggregate error list are not in tension: the throw
/// is HOW the section decides, the catch is how it reports alongside every other section's findings.</summary>
/// <param name="reason">Which of this door's finite refusal reasons fired.</param>
/// <param name="message">The human-readable, refusal-named message.</param>
internal sealed class HudValidationException(HudRefusal reason, string message) : Exception(message) {
    /// <summary>Gets which of this door's finite refusal reasons fired.</summary>
    public HudRefusal Reason { get; } = reason;
}
