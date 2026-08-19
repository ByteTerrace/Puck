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
}
