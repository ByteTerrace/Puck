using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The authored icon table's compile-time capacity ceilings — sized to hold every world's authored
/// repertoire without a per-world overlay-reservation change, the same posture <see cref="WorldBindingBarCapacity"/>
/// takes for the bar's own slot/bank ceilings.</summary>
public static class WorldIconCapacity {
    /// <summary>The most badge-mapping rows one world authors.</summary>
    public const int MaxBadges = 32;
    /// <summary>The most icon rows one world authors.</summary>
    public const int MaxIcons = 128;
    /// <summary>The most characters a <see cref="WorldIconRow.Label"/> carries — the ONE declaration of the label
    /// arity the whole draw path is cut for.</summary>
    /// <remarks>KEEP IN SYNC with the two shapes that carry a resolved label downstream, neither of which can grow
    /// without a wire change: <c>Puck.Overlays.OverlayResolvedGlyph</c> holds exactly two glyph slots
    /// (<c>Glyph0</c>/<c>Glyph1</c>), and <c>Puck.Overlays.OverlayFrameBuilder.WriteIcon</c> packs exactly two 7-bit
    /// badge-glyph fields into the icon element's state word (bits 9..15 and 16..22). Raising this constant alone
    /// silently truncates every third character at the composer.</remarks>
    public const int MaxLabelChars = 2;
}

/// <summary>The committed fixed-UI font ids an icon <see cref="WorldIconGlyphRef"/> is spelled with. Only
/// <see cref="JetBrainsMonoRegular"/> is admitted: the icon bake draws every glyph cell from that one MTSDF atlas
/// (<c>Puck.Overlays.OverlayGlyphAtlasSet.MonoFont</c>), so the validator refuses any other face by name rather than
/// admit a field the bake ignores. An icon glyph is never a document-shipped font file.</summary>
public static class WorldIconFontCatalog {
    /// <summary>The Inter Regular face — a committed proportional face the icon bake does NOT draw from, named by
    /// the refusal law that proves a non-mono face is rejected.</summary>
    public const string InterRegular = "inter-regular";
    /// <summary>The JetBrains Mono Regular face — the overlay's own text/badge font, and the only face an icon glyph
    /// may name.</summary>
    public const string JetBrainsMonoRegular = "jetbrains-mono-regular";
}

/// <summary>A single glyph reference into a world-shipped (committed fixed-UI) font: a font id
/// (<see cref="WorldIconFontCatalog"/>) plus one Unicode scalar, spelled either as the literal character or as a
/// <c>U+XXXX</c> escape (1-6 hex digits) for a glyph with no convenient literal spelling.</summary>
/// <param name="Font">The font id (<see cref="WorldIconFontCatalog"/>).</param>
/// <param name="Glyph">The glyph spelling: one literal character, or <c>U+XXXX</c>.</param>
public sealed record WorldIconGlyphRef(string Font, string Glyph) {
    /// <summary>Parses <see cref="Glyph"/> to exactly one Unicode scalar value.</summary>
    /// <param name="glyph">The glyph spelling.</param>
    /// <param name="codePoint">The parsed scalar value on success.</param>
    /// <returns><see langword="true"/> when <paramref name="glyph"/> spells exactly one valid, non-surrogate
    /// Unicode scalar.</returns>
    public static bool TryResolveCodePoint(string? glyph, out int codePoint) {
        codePoint = 0;

        if (string.IsNullOrEmpty(value: glyph)) {
            return false;
        }

        if (
            (glyph.Length > 2) &&
            (glyph[1] == '+') &&
            (glyph[0] is 'U' or 'u')
        ) {
            if (
                !int.TryParse(
                s: glyph.AsSpan(start: 2),
                style: System.Globalization.NumberStyles.AllowHexSpecifier,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var parsed
            ) ||
                !System.Text.Rune.IsValid(value: parsed)
            ) {
                return false;
            }

            codePoint = parsed;

            return true;
        }

        var enumerator = glyph.EnumerateRunes().GetEnumerator();

        if (!enumerator.MoveNext()) {
            return false;
        }

        var rune = enumerator.Current;

        if (enumerator.MoveNext()) {
            return false;
        }

        codePoint = rune.Value;

        return true;
    }
}
/// <summary>One authored icon: a stable name (the string binding entries and badge rows reference, e.g.
/// <c>action.jump</c>, <c>edit.next</c>) plus its content — exactly one of <see cref="Glyph"/> (a baked atlas
/// glyph) or <see cref="Label"/> (a short text badge, at most <see cref="WorldIconCapacity.MaxLabelChars"/>
/// characters — the LB/RB path made authorable).</summary>
/// <param name="Name">The stable icon name.</param>
/// <param name="Glyph">The glyph content, or <see langword="null"/> when this row carries <see cref="Label"/> instead.</param>
/// <param name="Label">The short text content, or <see langword="null"/> when this row carries <see cref="Glyph"/> instead.</param>
public sealed record WorldIconRow(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldIconGlyphRef? Glyph = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null
);
/// <summary>One family-specific badge override: the same physical button renders a different icon on the named
/// controller family (<c>Puck.Input.Devices.GamepadType</c> member name).</summary>
/// <param name="Family">The controller family name.</param>
/// <param name="Icon">The icon name this family shows instead of the row's default.</param>
public sealed record WorldIconBadgeOverride(string Family, string Icon);
/// <summary>One physical-control badge mapping: which authored icon an INPUT SOURCE ID shows on the binding bar, with
/// optional per-family overrides — the engine holds no button→glyph switch, and a control the bar can name is a
/// control a badge row can key. A slot, a modifier indicator, and a chord hint all resolve their badge through this
/// one table, by the same id.</summary>
/// <param name="Source">The physical control's input source id (<c>gamepad.buttonSouth</c>, <c>gamepad.leftTrigger</c>,
/// <c>mouse.button1</c>, …).</param>
/// <param name="Icon">The default icon name.</param>
/// <param name="OverridesRaw">Family-specific overrides — ABSENT resolves to none.</param>
public sealed record WorldIconBadgeRow(
    string Source,
    string Icon,
    [property: JsonPropertyName("overrides"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldIconBadgeOverride>? OverridesRaw = null
) {
    /// <summary>Gets the family-specific overrides — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldIconBadgeOverride> Overrides => (OverridesRaw ?? []);
}
/// <summary>The world's authored icon table: a keyed icon vocabulary (<see cref="Icons"/>) plus the physical-button
/// badge mapping that draws from it (<see cref="Badges"/>). ABSENT (no document in the basis chain authors this
/// section at all) means no icons: every badge and every bound action's icon string resolves to a blank plate — the
/// engine supplies no iconography of its own. A document that DOES author this section (even an empty one) puts
/// referential integrity in force: a badge row or a binding-entry icon string naming an unknown icon then refuses by
/// name.</summary>
/// <param name="IconsRaw">The icon rows.</param>
/// <param name="BadgesRaw">The badge-mapping rows.</param>
public sealed record WorldIconographySection(
    [property: JsonPropertyName("rows"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldIconRow>? IconsRaw = null,
    [property: JsonPropertyName("badges"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldIconBadgeRow>? BadgesRaw = null
) {
    /// <summary>Gets the policy applied when no document in the basis chain authors an <c>icons</c> section —
    /// absence draws no icons at all.</summary>
    public static WorldIconographySection Absent { get; } = new(
        BadgesRaw: [],
        IconsRaw: []
    );

    /// <summary>Gets the badge-mapping rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldIconBadgeRow> Badges => (BadgesRaw ?? []);
    /// <summary>Gets the icon rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldIconRow> Icons => (IconsRaw ?? []);
}
