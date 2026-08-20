using System.Globalization;
using System.Numerics;

namespace Puck.Forge.Authoring;

/// <summary>
/// The one <c>#RRGGBB</c> ↔ RGB conversion every authored color crosses — creation palettes, identity/profile colors,
/// HUD foreground/background. Components map straight through <c>/255</c>: the render path is gamma-naive end to end
/// (linear shading written to a UNORM surface), so an unlit <c>#FF66B3</c> reaches the screen as <c>#FF66B3</c>.
/// </summary>
public static class HexColor {
    /// <summary>The prefix of a state-bound color — <c>state.&lt;row&gt;</c> or <c>state.&lt;row&gt;.&lt;key&gt;</c>
    /// naming a world Text cell that holds a <c>#RRGGBB</c>. A creation document carries the token verbatim; the
    /// hosting world resolves it (<c>Puck.World.WorldColor</c>), so a creation on its own can only admit the syntax.
    /// KEEP IN SYNC with the HUD state-binding prefix in <c>Puck.World.HudBindingVocabulary</c>.</summary>
    public const string StateBindingPrefix = "state.";

    /// <summary>Returns whether a value has the state-binding shape: the prefix followed by at least one character.</summary>
    /// <param name="value">The candidate string.</param>
    public static bool IsStateBinding(string? value) =>
        ((value is not null) &&
        (value.Length > StateBindingPrefix.Length) &&
        value.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: StateBindingPrefix
        ));
    /// <summary>Formats an RGB triple (each component in <c>[0, 1]</c>) as an uppercase <c>#RRGGBB</c> string.</summary>
    /// <param name="rgb">The RGB color.</param>
    /// <returns>The <c>#RRGGBB</c> string.</returns>
    public static string Format(Vector3 rgb) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"#{((int)MathF.Round(x: (Math.Clamp(max: 1f, min: 0f, value: rgb.X) * 255f))):X2}{((int)MathF.Round(x: (Math.Clamp(max: 1f, min: 0f, value: rgb.Y) * 255f))):X2}{((int)MathF.Round(x: (Math.Clamp(max: 1f, min: 0f, value: rgb.Z) * 255f))):X2}"
        );
    /// <summary>Parses a <c>#RRGGBB</c> string, falling back when it does not parse.</summary>
    /// <param name="value">The <c>#RRGGBB</c> string.</param>
    /// <param name="fallback">The RGB color returned when <paramref name="value"/> does not parse.</param>
    /// <returns>The RGB color.</returns>
    public static Vector3 Parse(string? value, Vector3 fallback) =>
        (TryParse(
            rgb: out var rgb,
            value: value
        )
            ? rgb
            : fallback);
    /// <summary>Parses a <c>#RRGGBB</c> string (exactly seven characters, hex digits in either case).</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="rgb">The RGB color, each component in <c>[0, 1]</c>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed <c>#RRGGBB</c> string.</returns>
    public static bool TryParse(string? value, out Vector3 rgb) {
        if (
            (value is { Length: 7 }) &&
            (value[0] == '#') &&
            int.TryParse(
                s: value.AsSpan(start: 1),
                style: NumberStyles.HexNumber,
                provider: CultureInfo.InvariantCulture,
                result: out var packed
            )
        ) {
            rgb = new Vector3(
                x: (((packed >> 16) & 0xff) / 255f),
                y: (((packed >> 8) & 0xff) / 255f),
                z: ((packed & 0xff) / 255f)
            );

            return true;
        }

        rgb = default;

        return false;
    }
    /// <summary>Formats an RGBA quad (each component in <c>[0, 1]</c>) as an uppercase <c>#RRGGBB</c> string when
    /// opaque, else <c>#RRGGBBAA</c> — the baked-alpha form a theme token authors when it carries partial
    /// transparency.</summary>
    /// <param name="rgba">The RGBA color.</param>
    /// <returns>The <c>#RRGGBB</c>/<c>#RRGGBBAA</c> string.</returns>
    public static string FormatRgba(Vector4 rgba) {
        var rgb = Format(rgb: new Vector3(x: rgba.X, y: rgba.Y, z: rgba.Z));

        return ((rgba.W >= 1f)
            ? rgb
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{rgb}{((int)MathF.Round(x: (Math.Clamp(max: 1f, min: 0f, value: rgba.W) * 255f))):X2}"
            )
        );
    }
    /// <summary>Parses a <c>#RRGGBB</c> (opaque) or <c>#RRGGBBAA</c> (baked-alpha) string, falling back when it does
    /// not parse.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="fallback">The RGBA color returned when <paramref name="value"/> does not parse.</param>
    /// <returns>The RGBA color.</returns>
    public static Vector4 ParseRgba(string? value, Vector4 fallback) =>
        (TryParseRgba(
            rgba: out var rgba,
            value: value
        )
            ? rgba
            : fallback);
    /// <summary>Parses a <c>#RRGGBB</c> (opaque, alpha 1) or <c>#RRGGBBAA</c> (baked-alpha) string.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="rgba">The RGBA color, each component in <c>[0, 1]</c>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed <c>#RRGGBB</c>/<c>#RRGGBBAA</c>
    /// string.</returns>
    public static bool TryParseRgba(string? value, out Vector4 rgba) {
        if (TryParse(
            rgb: out var rgb,
            value: value
        )) {
            rgba = new Vector4(
                x: rgb.X,
                y: rgb.Y,
                z: rgb.Z,
                w: 1f
            );

            return true;
        }

        if (
            (value is { Length: 9 }) &&
            (value[0] == '#') &&
            int.TryParse(
                s: value.AsSpan(
                    start: 1,
                    length: 6
                ),
                style: NumberStyles.HexNumber,
                provider: CultureInfo.InvariantCulture,
                result: out var packed
            ) &&
            int.TryParse(
                s: value.AsSpan(
                    start: 7,
                    length: 2
                ),
                style: NumberStyles.HexNumber,
                provider: CultureInfo.InvariantCulture,
                result: out var alpha
            )
        ) {
            rgba = new Vector4(
                x: (((packed >> 16) & 0xff) / 255f),
                y: (((packed >> 8) & 0xff) / 255f),
                z: ((packed & 0xff) / 255f),
                w: (alpha / 255f)
            );

            return true;
        }

        rgba = default;

        return false;
    }
}
