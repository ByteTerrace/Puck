using System.Numerics;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>
/// Shared color math for the world: the HSV→RGB conversion the population's simulated-avatar palette
/// (<c>world.population</c>) uses, the uppercase <c>#RRGGBB</c> formatting the persisted owned-world identity
/// catalog stores, and the one authored-color grammar every color-bearing document field speaks — a
/// <c>#RRGGBB</c> literal, or a <c>state.&lt;row&gt;</c> / <c>state.&lt;row&gt;.&lt;key&gt;</c> binding (the HUD's
/// state grammar) naming a <see cref="CellKind.Text"/> cell that holds one. A bound color is state: named once,
/// read everywhere it is bound, and moved live by <c>world.state.cell.set</c>.
/// </summary>
public static class WorldColor {
    /// <summary>Converts an HSV triple (each component in <c>[0, 1]</c>) straight to an uppercase <c>#RRGGBB</c> hex
    /// string.</summary>
    /// <param name="h">Hue in <c>[0, 1)</c>.</param>
    /// <param name="s">Saturation in <c>[0, 1]</c>.</param>
    /// <param name="v">Value in <c>[0, 1]</c>.</param>
    /// <returns>The <c>#RRGGBB</c> hex string.</returns>
    public static string HsvToHex(float h, float s, float v) => RgbToHex(rgb: HsvToRgb(
        h: h,
        s: s,
        v: v
    ));
    /// <summary>Converts an HSV triple (each component in <c>[0, 1]</c>) to RGB in <c>[0, 1]</c>.</summary>
    /// <param name="h">Hue in <c>[0, 1)</c> (values outside wrap through the sextant math).</param>
    /// <param name="s">Saturation in <c>[0, 1]</c>.</param>
    /// <param name="v">Value in <c>[0, 1]</c>.</param>
    /// <returns>The RGB color as a <see cref="Vector3"/>.</returns>
    public static Vector3 HsvToRgb(float h, float s, float v) {
        var sector = ((int)MathF.Floor(x: (h * 6f)));
        var fraction = ((h * 6f) - sector);
        var p = (v * (1f - s));
        var q = (v * (1f - (s * fraction)));
        var t = (v * (1f - (s * (1f - fraction))));

        return (((sector % 6) + 6) % 6) switch {
            0 => new Vector3(
            x: v,
            y: t,
            z: p
        ),
            1 => new Vector3(
            x: q,
            y: v,
            z: p
        ),
            2 => new Vector3(
            x: p,
            y: v,
            z: t
        ),
            3 => new Vector3(
            x: p,
            y: q,
            z: v
        ),
            4 => new Vector3(
            x: t,
            y: p,
            z: v
        ),
            _ => new Vector3(
            x: v,
            y: p,
            z: q
        ),
        };
    }
    /// <summary>Returns whether an authored color is admissible against a document: a <c>#RRGGBB</c> literal, or a
    /// state binding naming a declared Text cell whose text is one.</summary>
    /// <param name="definition">The definition the binding is checked against.</param>
    /// <param name="value">The authored color.</param>
    public static bool IsAuthorable(WorldDefinition definition, string? value) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!TryParseBinding(
            key: out var key,
            row: out var row,
            value: value
        )) {
            return HexColor.TryParse(
                rgb: out _,
                value: value
            );
        }

        return (
            WorldStateReader.TryRead(
            definition: definition,
            key: key,
            rawValue: out _,
            row: out var stateRow,
            rowName: row,
            text: out var text,
            tick: 0UL
        ) &&
            (stateRow.Kind == CellKind.Text) &&
            HexColor.TryParse(
            rgb: out _,
            value: text
        )
        );
    }
    /// <summary>Resolves an authored color against the live document — a hex literal parses directly, a state
    /// binding reads its Text cell — falling back when the value is neither, the cell is absent, or its text is not
    /// a hex color (the validator refuses all three at author time; a live cell edit can still put a non-color there).</summary>
    /// <param name="definition">The live definition the binding reads.</param>
    /// <param name="value">The authored color, or <see langword="null"/>.</param>
    /// <param name="fallback">The color used when <paramref name="value"/> resolves to nothing.</param>
    public static Vector3 Resolve(WorldDefinition definition, string? value, Vector3 fallback) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!TryParseBinding(
            key: out var key,
            row: out var row,
            value: value
        )) {
            return HexColor.Parse(
                fallback: fallback,
                value: value
            );
        }

        return ((WorldStateReader.TryRead(
            definition: definition,
            key: key,
            rawValue: out _,
            row: out var stateRow,
            rowName: row,
            text: out var text,
            tick: 0UL
        ) && (stateRow.Kind == CellKind.Text))
            ? HexColor.Parse(
                fallback: fallback,
                value: text
            )
            : fallback
        );
    }
    /// <summary>Formats an RGB triple (each component in <c>[0, 1]</c>) as an uppercase <c>#RRGGBB</c> hex string,
    /// matching the catalog's stored convention.</summary>
    /// <param name="rgb">The RGB color.</param>
    /// <returns>The <c>#RRGGBB</c> hex string.</returns>
    public static string RgbToHex(Vector3 rgb) => HexColor.Format(rgb: rgb);
    /// <summary>Returns an authored generated color for a 0-based index.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <param name="defaults">The authored color sequence.</param>
    /// <returns>The RGB color as a <see cref="Vector3"/>.</returns>
    public static Vector3 SequenceColor(int index, WorldPlayerDefaults defaults) =>
        HsvToRgb(
            h: SequenceHue(
                index: index,
                sequence: defaults.ColorSequence
            ),
            s: defaults.Saturation,
            v: defaults.Value
        );
    /// <summary>Returns an authored generated color as an uppercase <c>#RRGGBB</c> string.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <param name="defaults">The authored color sequence.</param>
    /// <returns>The <c>#RRGGBB</c> hex string.</returns>
    public static string SequenceColorHex(int index, WorldPlayerDefaults defaults) =>
        HsvToHex(
            h: SequenceHue(
                index: index,
                sequence: defaults.ColorSequence
            ),
            s: defaults.Saturation,
            v: defaults.Value
        );
    /// <summary>Returns an authored sequence hue for a 0-based index, wrapped to <c>[0, 1)</c>.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <param name="sequence">The authored scalar sequence.</param>
    /// <returns>The hue in <c>[0, 1)</c>.</returns>
    public static float SequenceHue(int index, WorldSequence sequence) => WorldSequenceSampling.Scalar(
        index: index,
        sequence: sequence
    );
    /// <summary>Parses the state-binding arm of the color grammar: <c>state.&lt;row&gt;</c> (the row's slot cell) or
    /// <c>state.&lt;row&gt;.&lt;key&gt;</c>. A hex literal, or anything else, is not a binding. Delegates to
    /// <see cref="BindableState.TryParseBinding"/> — the shared grammar every bindable document token speaks.</summary>
    /// <param name="value">The authored color.</param>
    /// <param name="row">The bound row name.</param>
    /// <param name="key">The bound cell key, or <see langword="null"/> for the row's slot cell.</param>
    public static bool TryParseBinding(string? value, out string row, out string? key) => BindableState.TryParseBinding(
        key: out key,
        row: out row,
        value: value
    );

    /// <summary>The refusal every color-bearing field shares.</summary>
    public const string Grammar = "must be #RRGGBB, or state.<row>[.<key>] naming a Text cell that holds one";
}
